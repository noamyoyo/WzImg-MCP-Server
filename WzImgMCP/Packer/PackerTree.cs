using System.Text.RegularExpressions;
using WzImgMCP.Viewer;

namespace WzImgMCP.Packer;

/// <summary>A failure of a pack job, reported in the job's <c>error</c> as <c>{ code, message }</c>. The message never holds a path.</summary>
public sealed class PackerJobException : Exception
{
    public string Code { get; }

    public PackerJobException(string code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>One .img found in a category: its path below the <c>img</c> folder (<c>String/Pet.img</c>) and its size.</summary>
public readonly record struct PackerImage(string RelativePath, long SizeBytes);

/// <summary>
/// Checks a category name and walks a category folder without following anything. The walk is a lstat of every entry: a
/// symbolic link, a FIFO, a device or any other entry that is not a plain folder or a plain <c>.img</c> file stops the
/// pack, as does a name the viewer's path rules would refuse (verify addresses every file through them). Folders named
/// <c>Backups</c> are skipped, the same way the library skips them, and so is the extractor's <c>.imgcase.json</c> at the top of the category.
///
/// Limit: the walk is a check of the state now. A file swapped for a link afterwards, by a writer of the person's tree,
/// is not seen by it. The trees are mounted read only in the packer, so only another container can do that.
/// </summary>
internal static class PackerTree
{
    public const int MaxDepth = 32;

    private static readonly Regex CategoryPattern = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>One plain name: letters, digits, '_' and '-', at most 64 characters, not Backups. Throws 400 <c>bad_category</c>.</summary>
    public static string RequireCategory(string? category)
    {
        if (category == null || !CategoryPattern.IsMatch(category) || string.Equals(category, "Backups", StringComparison.OrdinalIgnoreCase))
            throw new ViewerApiException(400, "bad_category", "category must be one plain name of letters, digits, '_' or '-'");
        return category;
    }

    /// <summary>Every .img below <c>imgRoot/category</c>, sorted by path (ordinal). Throws <see cref="PackerJobException"/>.</summary>
    public static List<PackerImage> Enumerate(string imgRoot, string category, PosixViewerFileSystem files, PackerOptions options, CancellationToken cancellationToken)
    {
        var categoryDir = Path.Combine(imgRoot, category);
        var found = new List<PackerImage>();
        long bytes = 0;
        var pending = new Stack<(string Dir, string Relative, int Depth)>();
        pending.Push((categoryDir, category, 1));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, relative, depth) = pending.Pop();
            if (depth > MaxDepth)
                throw new PackerJobException("unsupported_name", "the category has folders nested too deeply");

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PackerJobException("source_unavailable", "a folder of the category cannot be read");
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                var rel = relative + "/" + name;
                if (new FileInfo(entry).LinkTarget != null)
                    throw new PackerJobException("unsafe_tree", "the category contains a symbolic link");

                if (files.IsRegularFile(entry))
                {
                    if (depth == 1 && name == ".imgcase.json")
                        continue;   // the extractor's casing map: the library reads it, it is not an image
                    if (!name.EndsWith(".img", StringComparison.Ordinal))
                        throw new PackerJobException("unsupported_file", "the category contains a file that is not an .img");
                    RequireAddressable(rel);
                    var size = new FileInfo(entry).Length;
                    bytes += size;
                    if (found.Count + 1 > options.MaxImages)
                        throw new PackerJobException("too_many_images", "the category has more images than the packer accepts");
                    if (bytes > options.MaxInputBytes)
                        throw new PackerJobException("input_too_large", "the category is larger than the packer accepts");
                    found.Add(new PackerImage(rel, size));
                }
                else if (Directory.Exists(entry))
                {
                    if (string.Equals(name, "Backups", StringComparison.OrdinalIgnoreCase))
                        continue;   // the library skips these too
                    RequireAddressable(rel + "/x.img");
                    pending.Push((entry, rel, depth + 1));
                }
                else
                {
                    throw new PackerJobException("unsafe_tree", "the category contains an entry that is neither a folder nor a regular file");
                }
            }
        }
        found.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return found;
    }

    private static void RequireAddressable(string relativePath)
    {
        try
        {
            ViewerPathResolver.ValidatePattern(relativePath);
        }
        catch (ViewerApiException)
        {
            throw new PackerJobException("unsupported_name", "the category contains a name the packer does not accept (only ASCII letters, digits, space, '_', '-' and '.')");
        }
    }
}
