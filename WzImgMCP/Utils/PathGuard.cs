namespace WzImgMCP.Utils;

/// <summary>
/// Thrown when a tool is given a file system path outside the configured allowed root.
/// </summary>
public sealed class PathNotAllowedException : Exception
{
    public PathNotAllowedException(string message) : base(message) { }
}

/// <summary>
/// Optional file system sandbox for tool path arguments.
///
/// When the WZIMG_ALLOWED_ROOT environment variable is set, every path a tool receives is resolved
/// with <see cref="Path.GetFullPath(string)"/>, symbolic links are followed, and the result must lie
/// inside that root. When it is unset (the default) paths are returned unchanged, so the Windows
/// desktop behaviour is not affected. The container image sets it to /data.
/// </summary>
public static class PathGuard
{
    public const string EnvVar = "WZIMG_ALLOWED_ROOT";
    private const int MaxLinkDepth = 40;

    /// <summary>The configured root (full path, symlinks resolved), or null when no restriction is set.</summary>
    public static string? AllowedRoot
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrWhiteSpace(configured))
                return null;
            return ResolveRealPath(Path.GetFullPath(configured.Trim()));
        }
    }

    /// <summary>
    /// Returns the path to use for the operation. Without a configured root the input is returned as is.
    /// With a root, relative paths are taken relative to the root; the result is fully resolved and must be
    /// inside the root, otherwise <see cref="PathNotAllowedException"/> is thrown.
    /// </summary>
    public static string Resolve(string path)
    {
        var root = AllowedRoot;
        if (root == null)
            return path;

        if (string.IsNullOrWhiteSpace(path))
            throw new PathNotAllowedException($"path outside allowed root: (empty path); allowed root is {root}");

        string resolved;
        try
        {
            resolved = ResolveRealPath(Path.GetFullPath(path, root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            throw new PathNotAllowedException($"path outside allowed root: invalid path '{path}'; allowed root is {root}");
        }

        if (!IsInside(root, resolved))
            throw new PathNotAllowedException($"path outside allowed root: {path} (resolves to {resolved}); allowed root is {root}");

        return resolved;
    }

    /// <summary>Like <see cref="Resolve(string)"/> but passes null through.</summary>
    public static string? ResolveOptional(string? path) => path == null ? null : Resolve(path);

    private static bool IsInside(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        if (string.Equals(trimmedRoot, candidate, comparison))
            return true;
        // A filesystem root such as "/" keeps its separator; every absolute path is inside it.
        var prefix = Path.EndsInDirectorySeparator(trimmedRoot) ? trimmedRoot : trimmedRoot + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }

    /// <summary>
    /// Resolves symbolic links in every component of an absolute path. Components that do not exist yet
    /// are kept as they are (an output directory that will be created cannot contain a link).
    /// </summary>
    internal static string ResolveRealPath(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var pending = new LinkedList<string>(
            fullPath.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries));
        var current = root;
        int links = 0;

        while (pending.Count > 0)
        {
            var part = pending.First!.Value;
            pending.RemoveFirst();
            var next = Path.Combine(current, part);

            string? target = null;
            try
            {
                target = new FileInfo(next).LinkTarget;
            }
            catch (IOException)
            {
                // not inspectable: treat as a plain component
            }

            if (target == null)
            {
                current = next;
                continue;
            }

            if (++links > MaxLinkDepth)
                throw new IOException("Too many levels of symbolic links.");

            var targetFull = Path.GetFullPath(target, current);
            var targetRoot = Path.GetPathRoot(targetFull) ?? string.Empty;
            var targetParts = targetFull.Substring(targetRoot.Length)
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = targetParts.Length - 1; i >= 0; i--)
                pending.AddFirst(targetParts[i]);
            current = targetRoot;
        }

        return current;
    }
}
