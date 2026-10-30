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

/// <summary>One .img of a pack: its path below the <c>img</c> folder (<c>String/Pet.img</c>) and its size.</summary>
public readonly record struct PackerImage(string RelativePath, long SizeBytes);

/// <summary>Checks a category name: one plain name, the <c>&lt;Category&gt;</c> of <c>&lt;Category&gt;.wz</c>.</summary>
internal static class PackerTree
{
    private static readonly Regex CategoryPattern = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);

    public static bool IsCategory(string? category)
        => category != null && CategoryPattern.IsMatch(category) && !string.Equals(category, "Backups", StringComparison.OrdinalIgnoreCase);

    /// <summary>One plain name: letters, digits, '_' and '-', at most 64 characters, not Backups. Throws 400 <c>bad_category</c>.</summary>
    public static string RequireCategory(string? category)
    {
        if (!IsCategory(category))
            throw new ViewerApiException(400, "bad_category", "category must be one plain name of letters, digits, '_' or '-'");
        return category!;
    }
}
