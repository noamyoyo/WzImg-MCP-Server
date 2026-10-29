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
/// <param name="Identity">Device and inode of the file when the resolver checked it (null if unavailable); the reader compares the file it opens with it.</param>
public sealed record ResolvedImage(string Source, string RelativePath, string FullPath, long SizeBytes, FileIdentity? Identity = null);

/// <summary>
/// Turns (source name, relative image path such as <c>String/Consume.img</c>) into a file under that source's
/// <c>img</c> folder, or throws a <see cref="ViewerApiException"/>: <c>unknown_source</c> (400), <c>bad_path</c> (400),
/// <c>not_found</c> (404, the file is absent in a readable <c>img</c> folder) or <c>source_unavailable</c> (503, the
/// <c>img</c> folder itself is missing or cannot be read, for instance a mount that is down; this is NOT "no such file").
///
/// The path must match a strict pattern before the file system is touched: only ASCII letters, digits, space,
/// '_', '-', '.' and '/' as separator, no empty segment, no segment that starts with '.', last segment ends with
/// ".img", no segment is "Backups" (any case), at most 260 characters. Then every component is checked on disk: no
/// symbolic link anywhere between the root's <c>img</c> folder and the file, the file is not a directory, and the
/// fully resolved path is inside the <c>img</c> folder (and inside WZIMG_ALLOWED_ROOT, when that is set).
/// Names are compared as they are: Linux is case-sensitive and nothing is lower-cased.
///
/// Limits in V1: "not a directory" is all the type check does, so a FIFO or device file named x.img would pass, and
/// the path is checked, not an open handle (a race between check and use is possible). The resolver only reports a
/// size, so this is harmless now. The code that opens the file (V2) must lstat/fstat for S_IFREG, open with
/// O_NOFOLLOW | O_NONBLOCK, and compare device and inode of the opened handle with the checked path.
/// </summary>
public class ViewerPathResolver
{
    public const int MaxPathLength = 260;
    private const string ImgFolder = "img";
    private const string ImgExtension = ".img";

    private readonly IReadOnlyDictionary<string, string> _roots;
    private readonly IViewerFileSystem? _fileSystem;

    public ViewerPathResolver(IReadOnlyDictionary<string, string> roots, IViewerFileSystem? fileSystem = null)
    {
        _roots = roots;
        _fileSystem = fileSystem ?? (OperatingSystem.IsLinux() ? new PosixViewerFileSystem() : null);
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
        var current = imgRoot;
        try
        {
            if (new FileInfo(imgRoot).LinkTarget != null)
                throw new ViewerApiException(400, "bad_path", "the source folder is a symbolic link");
            if (!Directory.Exists(imgRoot) || !IsReadableDirectory(imgRoot))
                throw SourceUnavailable();

            // Walk down one component at a time, so a link in any parent folder (or in the file itself) is seen.
            for (int i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                if (new FileInfo(current).LinkTarget != null)
                    throw new ViewerApiException(400, "bad_path", "symbolic links are not allowed");

                bool last = i == segments.Length - 1;
                if (last ? !File.Exists(current) : !Directory.Exists(current))   // File.Exists is false for a directory
                    throw new ViewerApiException(404, "not_found", "image not found");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A folder that cannot be searched or read is a problem of the source, not proof that the image is absent.
            throw SourceUnavailable();
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
            throw SourceUnavailable();
        }

        FileIdentity? identity = null;
        try { identity = _fileSystem?.Identity(real); } catch (PlatformNotSupportedException) { }

        return new ResolvedImage(source, string.Join('/', segments), real, size, identity);
    }

    private static ViewerApiException SourceUnavailable() => new(503, "source_unavailable", "the source is not available; retry later");

    /// <summary>True when the folder exists and its entries can be listed.</summary>
    internal static bool IsReadableDirectory(string path)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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

        foreach (var segment in segments)
            if (string.Equals(segment, "Backups", StringComparison.OrdinalIgnoreCase))
                throw Bad("path is in a Backups folder");
        if (!segments[^1].EndsWith(ImgExtension, StringComparison.Ordinal))
            throw Bad("path must end with .img");

        return segments;
    }

    private static ViewerApiException Bad(string message) => new(400, "bad_path", message);
}
