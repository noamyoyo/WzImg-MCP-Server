using WzImgMCP.Utils;

namespace WzImgMCP.Viewer;

/// <summary>A failure the viewer reports to the client as a typed JSON error: <c>{ "error": { "code", "message" } }</c>.</summary>
public sealed class ViewerApiException : Exception
{
    public int Status { get; }
    public string Code { get; }

    public ViewerApiException(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }
}

/// <summary>A source file the resolver accepted: a regular file inside the source's <c>img</c> folder.</summary>
public sealed record ResolvedImage(string Source, string RelativePath, string FullPath, long SizeBytes);

/// <summary>
/// Turns (source name, relative image path such as <c>String/Consume.img</c>) into a file under that source's
/// <c>img</c> folder, or throws a <see cref="ViewerApiException"/>: <c>unknown_source</c> (400), <c>bad_path</c> (400)
/// or <c>not_found</c> (404).
///
/// The path must match a strict pattern before the file system is touched: only ASCII letters, digits, space,
/// '_', '-', '.' and '/' as separator, no empty segment, no segment that starts with '.', last segment ends with
/// ".img", first segment is not "Backups", at most 260 characters. Then every component is checked on disk: no
/// symbolic link anywhere between the root's <c>img</c> folder and the file, the file is a regular file, and the
/// fully resolved path is inside the <c>img</c> folder (and inside WZIMG_ALLOWED_ROOT, when that is set).
/// Names are compared as they are: Linux is case-sensitive and nothing is lower-cased.
/// </summary>
public class ViewerPathResolver
{
    public const int MaxPathLength = 260;
    private const string ImgFolder = "img";
    private const string ImgExtension = ".img";

    private readonly IReadOnlyDictionary<string, string> _roots;

    public ViewerPathResolver(IReadOnlyDictionary<string, string> roots)
    {
        _roots = roots;
    }

    /// <summary>Resolves the file. Hook for the async endpoints; tests override it.</summary>
    public virtual Task<ResolvedImage> ResolveAsync(string? source, string? path, CancellationToken cancellationToken)
        => Task.FromResult(Resolve(source, path));

    public ResolvedImage Resolve(string? source, string? path)
    {
        if (source == null || !_roots.TryGetValue(source, out var root))
            throw new ViewerApiException(400, "unknown_source", "unknown source; use canon, owner or friend");

        var segments = ValidatePattern(path);

        var imgRoot = Path.Combine(root, ImgFolder);
        if (new FileInfo(imgRoot).LinkTarget != null)
            throw new ViewerApiException(400, "bad_path", "the source folder is a symbolic link");
        if (!Directory.Exists(imgRoot))
            throw new ViewerApiException(404, "not_found", "image not found");

        // Walk down one component at a time, so a link in any parent folder (or in the file itself) is seen.
        var current = imgRoot;
        for (int i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            if (new FileInfo(current).LinkTarget != null)
                throw new ViewerApiException(400, "bad_path", "symbolic links are not allowed");

            bool last = i == segments.Length - 1;
            if (last ? !File.Exists(current) : !Directory.Exists(current))   // File.Exists is false for a directory
                throw new ViewerApiException(404, "not_found", "image not found");
        }

        // Defence in depth: the walk above already refused links, so this holds by construction. Re-check it on the
        // fully resolved path the same way PathGuard does.
        string real, realImg;
        try
        {
            real = PathGuard.ResolveRealPath(Path.GetFullPath(current));
            realImg = PathGuard.ResolveRealPath(Path.GetFullPath(imgRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            throw new ViewerApiException(400, "bad_path", "invalid path");
        }
        if (!real.StartsWith(Path.TrimEndingDirectorySeparator(realImg) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ViewerApiException(400, "bad_path", "path is outside the source folder");
        try
        {
            PathGuard.Resolve(real);   // WZIMG_ALLOWED_ROOT, when set
        }
        catch (PathNotAllowedException)
        {
            throw new ViewerApiException(400, "bad_path", "path is outside the allowed root");
        }

        long size;
        try
        {
            size = new FileInfo(real).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ViewerApiException(404, "not_found", "image not found");
        }

        return new ResolvedImage(source, string.Join('/', segments), real, size);
    }

    /// <summary>Checks the text rules and returns the segments. Throws <c>bad_path</c>.</summary>
    internal static string[] ValidatePattern(string? path)
    {
        if (string.IsNullOrEmpty(path))
            throw Bad("path is empty");
        if (path.Length > MaxPathLength)
            throw Bad($"path is longer than {MaxPathLength} characters");

        foreach (var c in path)
        {
            // Rejects backslash, control characters, '%', ':' and every non-ASCII character as well.
            if (!(char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' or '/'))
                throw Bad("path contains a character that is not allowed");
        }

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                throw Bad("path has an empty segment or starts or ends with '/'");
            if (segment[0] == '.')
                throw Bad("path has a segment that starts with '.'");   // also covers ".", ".." and hidden names
        }

        if (string.Equals(segments[0], "Backups", StringComparison.OrdinalIgnoreCase))
            throw Bad("path is in the Backups folder");
        if (!segments[^1].EndsWith(ImgExtension, StringComparison.Ordinal))
            throw Bad("path must end with .img");

        return segments;
    }

    private static ViewerApiException Bad(string message) => new(400, "bad_path", message);
}
