using System.Security.Cryptography;
using System.Text;
using WzImgMCP.Utils;

namespace WzImgMCP.Import;

/// <summary>
/// File helpers of the import: every path written or deleted is checked to lie inside the allowed root and to have no
/// symbolic link on the way, and every file is written to a temporary name next to its target and renamed into place.
/// </summary>
internal static class ImportFs
{
    public const string TempPrefix = ".import-";

    public static string Sha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Sha256Bytes(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Text(string text) => Sha256Bytes(Encoding.UTF8.GetBytes(text));

    /// <summary>The allowed root with symbolic links resolved, or an <see cref="ImportException"/> when no root is configured.</summary>
    public static string RequireRoot()
        => PathGuard.AllowedRoot ?? throw new ImportException("import_disabled",
            "Import by link is switched off: this server has no allowed root (WZIMG_ALLOWED_ROOT is not set).");

    /// <summary>
    /// Confines <paramref name="path"/> to the allowed root with the existing helper (symbolic links followed, must stay inside)
    /// and then refuses it when any part below the root is a symbolic link, so a link planted inside the root cannot redirect a write.
    /// </summary>
    public static string Confine(string path)
    {
        string resolved;
        try
        {
            resolved = PathGuard.Resolve(path);
        }
        catch (PathNotAllowedException)
        {
            throw new ImportException("path_not_allowed", "A path left the allowed folder; the operation was refused.");
        }
        RequireNoLinks(resolved);
        return resolved;
    }

    /// <summary>Throws when the file or any folder between the allowed root and it (inclusive) is a symbolic link.</summary>
    public static void RequireNoLinks(string fullPath)
    {
        var root = RequireRoot();
        var full = Path.GetFullPath(fullPath);
        var relative = Path.GetRelativePath(root, full);
        if (relative == ".")
            return;
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ImportException("path_not_allowed", "A path left the allowed folder; the operation was refused.");
        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget != null)
                throw new ImportException("path_not_allowed", "A symbolic link was found on the way to a file; the operation was refused.");
        }
    }

    /// <summary>Joins a '/'-separated relative path to a base folder after checking every part is one plain name.</summary>
    public static string CombineRelative(string baseDir, string relative)
    {
        var current = baseDir;
        foreach (var part in relative.Split('/'))
        {
            if (!PathGuard.IsSafeComponent(part, out _))
                throw new ImportException("unsafe_name", "A file name was refused because it is not a plain name.");
            current = Path.Combine(current, part);
        }
        return current;
    }

    /// <summary>Creates the folders of a '/'-separated relative path below an existing base, one by one, never through a link. Returns the folders it created (full paths).</summary>
    public static List<string> EnsureDirectories(string baseDir, string relativeDirectory)
    {
        var created = new List<string>();
        if (string.IsNullOrEmpty(relativeDirectory))
            return created;
        var current = baseDir;
        foreach (var part in relativeDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!PathGuard.IsSafeComponent(part, out _))
                throw new ImportException("unsafe_name", "A folder name was refused because it is not a plain name.");
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.Exists)
            {
                if (info.LinkTarget != null)
                    throw new ImportException("path_not_allowed", "A symbolic link was found on the way to a file; the operation was refused.");
                continue;
            }
            Confine(current);
            Directory.CreateDirectory(current);
            created.Add(current);
        }
        return created;
    }

    /// <summary>
    /// Copies <paramref name="source"/> over <paramref name="target"/>: bytes go to a new temporary file in the target's folder,
    /// the temporary file must hash to <paramref name="expectedSha"/>, and then it replaces the target by one rename.
    /// A link at the target is refused. Never overwrites through a link.
    /// </summary>
    public static void CopyAtomic(string source, string target, string expectedSha)
    {
        RequireNoLinks(target);
        var temp = Path.Combine(Path.GetDirectoryName(target)!, TempPrefix + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            if (Sha256File(temp) != expectedSha)
                throw new ImportException("copy_failed", "A file was damaged while it was being copied; nothing was replaced.");
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public static void WriteTextAtomic(string target, string text)
    {
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>Deletes a folder tree. Symbolic links inside are removed as links, never followed.</summary>
    public static void DeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left for the next clean-up
        }
    }

    /// <summary>
    /// The .img files below <paramref name="directory"/>: '/'-separated path relative to it, and the full path. Folders named
    /// Backups, symbolic links and everything that is not an .img file are skipped; links are counted in <paramref name="links"/>.
    /// </summary>
    public static List<(string Relative, string Full)> EnumerateImages(string directory, out int links)
    {
        var result = new List<(string, string)>();
        int linkCount = 0;
        void Walk(string dir, string prefix)
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget != null) { linkCount++; continue; }
                if (entry is DirectoryInfo d)
                {
                    if (d.Name.Equals("Backups", StringComparison.OrdinalIgnoreCase)) continue;
                    Walk(d.FullName, prefix + d.Name + "/");
                }
                else if (entry.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase) && !entry.Name.StartsWith(TempPrefix, StringComparison.Ordinal))
                {
                    result.Add((prefix + entry.Name, entry.FullName));
                }
            }
        }
        Walk(directory, "");
        links = linkCount;
        result.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return result;
    }
}
