using System.Text.Json;

namespace WzImgMCP.Viewer.Diff;

/// <summary>
/// The hashes the editors recorded when a person's copy was made: <c>&lt;person root&gt;/.base.json</c>, a JSON object
/// <c>{ "files": { "String/Pet.img": "&lt;sha256 hex&gt;", ... } }</c> (the other members are ignored). A file that equals its
/// recorded hash has not been edited. The map is read through the safe reader and kept until the file's size or mtime
/// changes. A missing or unreadable map gives "unknown" (null) for every file, never an error.
/// </summary>
public sealed class ViewerBaseMap
{
    private const long MaxBytes = 64L * 1024 * 1024;

    private readonly ViewerOptions _options;
    private readonly IViewerFileSystem _fileSystem;
    private readonly Dictionary<string, (FileStamp Stamp, Dictionary<string, string>? Files)> _maps = new();

    public ViewerBaseMap(ViewerOptions options, IViewerFileSystem fileSystem)
    {
        _options = options;
        _fileSystem = fileSystem;
    }

    /// <summary>The recorded sha256 of <paramref name="relativePath"/> in <paramref name="source"/>'s copy, or null when not recorded.</summary>
    public string? Get(string source, string relativePath)
    {
        var files = Load(source);
        return files != null && files.TryGetValue(relativePath, out var hash) ? hash : null;
    }

    private Dictionary<string, string>? Load(string source)
    {
        if (!_options.Roots.TryGetValue(source, out var root))
            return null;
        var path = Path.Combine(root, ".base.json");
        try
        {
            var stamp = _fileSystem.Stat(path);
            if (stamp == null)
                return null;
            lock (_maps)
                if (_maps.TryGetValue(source, out var known) && known.Stamp == stamp.Value)
                    return known.Files;

            var read = _fileSystem.Read(path, MaxBytes, null);   // a configured path, not a client one
            Dictionary<string, string>? files = null;
            using (var doc = JsonDocument.Parse(read.Bytes))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("files", out var list)
                    && list.ValueKind == JsonValueKind.Object)
                {
                    files = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var entry in list.EnumerateObject())
                        if (entry.Value.ValueKind == JsonValueKind.String)
                            files[entry.Name] = entry.Value.GetString()!.ToLowerInvariant();
                }
            }
            lock (_maps)
                _maps[source] = (read.After, files);
            return files;
        }
        catch (Exception ex) when (ex is ViewerApiException or JsonException or IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return null;
        }
    }
}
