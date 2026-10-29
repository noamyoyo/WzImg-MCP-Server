using System.Text.Json;

namespace WzImgMCP.Viewer.Diff;

/// <summary>
/// The hashes the editors recorded when a person's copy was made: <c>&lt;person root&gt;/.base.json</c>, a JSON object
/// <c>{ "files": { "String/Pet.img": "&lt;sha256 hex&gt;", ... } }</c> (the other members are ignored). A file that equals its
/// recorded hash has not been edited, IF the map can be trusted.
///
/// ADVISORY ONLY: the map lives in the person's own root, which that person's editor can write, so they can record the
/// hash of an edited file. <c>matchesBase</c> is a convenience for the panel, never proof of "not edited"; the diff
/// against canon (<c>state</c>, the sha256 values) is what decides.
///
/// The map is read through the safe reader and kept until the file's stamp (size, mtime, ctime, inode) changes. A
/// missing or unreadable map gives "unknown" (null) for every file, never an error. A map that is malformed or too big
/// is remembered as unusable under its stamp, so it is not read and parsed again on every request; a transient failure
/// (503/500 from the reader) is not remembered. Keys are normalised (<c>\</c> is <c>/</c>, a leading <c>./</c> or <c>/</c> is
/// dropped, case is ignored); a key that two entries write differently with different hashes is ambiguous and gives null.
/// </summary>
public sealed class ViewerBaseMap
{
    private const long MaxBytes = 64L * 1024 * 1024;

    private readonly ViewerOptions _options;
    private readonly IViewerFileSystem _fileSystem;
    private readonly Dictionary<string, (FileStamp Stamp, Dictionary<string, string?>? Files)> _maps = new();

    /// <summary>Times a map file was read and parsed (for tests: a bad map must not be re-read while its stamp is unchanged).</summary>
    public long Reads => Interlocked.Read(ref _reads);
    private long _reads;

    public ViewerBaseMap(ViewerOptions options, IViewerFileSystem fileSystem)
    {
        _options = options;
        _fileSystem = fileSystem;
    }

    /// <summary>The recorded sha256 of <paramref name="relativePath"/> in <paramref name="source"/>'s copy, or null when not recorded (or ambiguous).</summary>
    public string? Get(string source, string relativePath)
    {
        var files = Load(source);
        return files != null && files.TryGetValue(NormalizeKey(relativePath), out var hash) ? hash : null;
    }

    /// <summary>Lower-case, '\\' as '/', no leading "./" or '/'.</summary>
    internal static string NormalizeKey(string key)
    {
        var k = key.Trim().Replace('\\', '/');
        while (k.StartsWith("./", StringComparison.Ordinal))
            k = k.Substring(2);
        return k.TrimStart('/').ToLowerInvariant();
    }

    private Dictionary<string, string?>? Load(string source)
    {
        if (!_options.Roots.TryGetValue(source, out var root))
            return null;
        var path = Path.Combine(root, ".base.json");
        FileStamp? stamp = null;
        try
        {
            stamp = _fileSystem.Stat(path);
            if (stamp == null)
                return null;
            lock (_maps)
                if (_maps.TryGetValue(source, out var known) && known.Stamp == stamp.Value)
                    return known.Files;

            if (stamp.Value.Size > MaxBytes)
                return Remember(source, stamp.Value, null);

            Interlocked.Increment(ref _reads);
            var read = _fileSystem.Read(path, MaxBytes, null);   // a configured path, not a client one
            Dictionary<string, string?>? files = null;
            try
            {
                using var doc = JsonDocument.Parse(read.Bytes);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("files", out var list)
                    && list.ValueKind == JsonValueKind.Object)
                {
                    files = new Dictionary<string, string?>(StringComparer.Ordinal);
                    foreach (var entry in list.EnumerateObject())
                    {
                        if (entry.Value.ValueKind != JsonValueKind.String)
                            continue;
                        var key = NormalizeKey(entry.Name);
                        var hash = entry.Value.GetString()!.Trim().ToLowerInvariant();
                        if (!files.TryGetValue(key, out var existing))
                            files[key] = hash;
                        else if (existing != hash)
                            files[key] = null;   // two spellings of one file with different hashes: unknown
                    }
                }
            }
            catch (JsonException)
            {
                files = null;   // malformed: unusable until the file changes
            }
            return Remember(source, read.After, files);
        }
        catch (ViewerApiException ex) when (stamp.HasValue && ex.Status is 400 or 413)
        {
            return Remember(source, stamp.Value, null);   // not a regular file, or too big: the same until the file changes
        }
        catch (Exception ex) when (ex is ViewerApiException or IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return null;   // transient or unknown: try again next time
        }
    }

    private Dictionary<string, string?>? Remember(string source, FileStamp stamp, Dictionary<string, string?>? files)
    {
        lock (_maps)
            _maps[source] = (stamp, files);
        return files;
    }
}
