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

    /// <summary>
    /// True when <paramref name="name"/> is a single plain path component: not empty, no directory separators,
    /// no "..", not rooted, no drive prefix or other colon, no control characters.
    /// </summary>
    public static bool IsSafeComponent(string? name, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(name)) { reason = "is empty"; return false; }
        if (name.Contains('/') || name.Contains('\\')) { reason = "contains a path separator"; return false; }
        if (name.Contains("..")) { reason = "contains '..'"; return false; }
        if (name.Contains(':')) { reason = "contains ':' (drive prefix)"; return false; }
        if (Path.IsPathRooted(name)) { reason = "is a rooted path"; return false; }
        if (name.Any(char.IsControl)) { reason = "contains control characters"; return false; }
        if (name.Trim().Trim('.').Length == 0) { reason = "is not a file name"; return false; }
        return true;
    }

    /// <summary>Throws <see cref="PathNotAllowedException"/> when <paramref name="name"/> is not a safe single path component.</summary>
    public static string RequireSafeComponent(string? name, string what)
    {
        if (!IsSafeComponent(name, out var reason))
            throw new PathNotAllowedException($"invalid {what} '{name}': it {reason}; only a single plain name is allowed");
        return name!;
    }

    /// <summary>
    /// Turns arbitrary text (a canvas name, a property path) into one safe file name: separators, colons, control
    /// characters and ".." are replaced by '_'. Never empty.
    /// </summary>
    public static string SanitizeFileName(string? text, string fallback = "unnamed")
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text ?? "")
            sb.Append(ch is '/' or '\\' or ':' || char.IsControl(ch) ? '_' : ch);
        var s = sb.ToString().Replace("..", "__").Trim();
        if (s.Trim('.').Length == 0) return fallback;
        return s.Length > 150 ? s.Substring(0, 150) : s;
    }

    /// <summary>Returns <paramref name="name"/>, or name_2, name_3 ... when it is already in <paramref name="used"/>; records the result.</summary>
    public static string UniqueName(string name, ISet<string> used)
    {
        var candidate = name;
        for (int i = 2; !used.Add(candidate); i++)
            candidate = $"{name}_{i}";
        return candidate;
    }

    /// <summary>
    /// Writes a file under <paramref name="directory"/> after the final path was resolved through <see cref="Resolve(string)"/>
    /// (symlinks followed, must be inside the allowed root). Directories are only created after that check.
    /// Returns the path written.
    /// </summary>
    public static string WriteFileGuarded(string directory, string fileName, byte[] data)
    {
        var finalPath = Resolve(Path.Combine(directory, fileName));
        var finalDir = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(finalDir) && !Directory.Exists(finalDir))
            Directory.CreateDirectory(finalDir);
        WriteFileNoFollow(finalPath, data);
        return finalPath;
    }

    /// <summary>
    /// Writes <paramref name="data"/> to <paramref name="resolvedPath"/> (already resolved through <see cref="Resolve(string)"/>)
    /// without ever following a symbolic link at that path. <see cref="Resolve(string)"/> and the write are two steps, so a
    /// link planted in between would be followed by a plain File.WriteAllBytes. Here the bytes go to a new file
    /// (<see cref="FileMode.CreateNew"/>, never follows a link) next to the target, which then replaces the target by a
    /// rename; a rename replaces a link itself and never writes through it. A link found at the target is refused.
    /// </summary>
    public static void WriteFileNoFollow(string resolvedPath, byte[] data)
    {
        if (new FileInfo(resolvedPath).LinkTarget != null)
            throw new PathNotAllowedException($"path outside allowed root: refusing to write through the symbolic link {resolvedPath}");

        var temp = Path.Combine(Path.GetDirectoryName(resolvedPath) ?? ".", "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(data, 0, data.Length);
            File.Move(temp, resolvedPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

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
