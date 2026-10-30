using WzImgMCP.Viewer;

namespace WzImgMCP.Packer;

/// <summary>One file of a pack request, as the caller wrote it: <c>img</c> is the path below the person's <c>img</c> folder (<c>String/Pet.img</c>).</summary>
public sealed record PackRequestFile(string? Img, string? From, string? Op);

/// <summary>One file of a pack, checked: <c>Img</c> starts with the category, <c>From</c> is owner or friend, <c>Op</c> is set or remove.</summary>
public sealed record PackerFile(string Img, string From, string Op)
{
    public const string Set = "set", Remove = "remove";

    /// <summary>The path of the image inside the .wz: the request path without the category (<c>Sub/Deep.img</c>).</summary>
    public string WzPath(string category) => Img[(category.Length + 1)..];
}

/// <summary>What became of one file: the checked request plus the result (<c>replaced</c>, <c>added</c> or <c>removed</c>) and, for a set, the sha256 and size of the input image the pack used.</summary>
public sealed record PackerFileResult(string Img, string From, string Op, string? Result, string? Sha256, long? SizeBytes)
{
    public const string Replaced = "replaced", Added = "added", Removed = "removed";

    /// <summary>The path of the image inside the .wz: the request path without the category (<c>Sub/Deep.img</c>).</summary>
    public string WzPath(string category) => Img[(category.Length + 1)..];
}

/// <summary>Checks the file list of a pack request. The same rules read a list back from a job.json.</summary>
internal static class PackerFiles
{
    public const int MaxDepth = 32;

    /// <summary>Throws a typed 400 for a list that breaks a rule. Returns the files in the order given.</summary>
    public static List<PackerFile> Validate(string category, IReadOnlyList<PackRequestFile?>? files, int maxFiles)
    {
        if (files == null || files.Count == 0)
            throw new ViewerApiException(400, "bad_request", "files must list at least one image");
        if (files.Count > maxFiles)
            throw new ViewerApiException(400, "too_many_files", $"a pack takes at most {maxFiles} files");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // a .wz does not tell names apart by case
        var result = new List<PackerFile>(files.Count);
        foreach (var file in files)
        {
            if (file == null)
                throw new ViewerApiException(400, "bad_request", "a file entry is empty");
            var img = RequireImg(category, file.Img);
            if (file.From is not ("owner" or "friend"))
                throw new ViewerApiException(400, "unknown_source", "from must be owner or friend");
            if (file.Op is not (PackerFile.Set or PackerFile.Remove))
                throw new ViewerApiException(400, "bad_request", "op must be set or remove");
            if (!seen.Add(img))
                throw new ViewerApiException(400, "bad_request", "a file is listed twice");
            result.Add(new PackerFile(img, file.From, file.Op));
        }
        return result;
    }

    /// <summary>True when every entry of a list read back from disk would have been accepted as a request.</summary>
    public static bool IsPlausible(string category, IReadOnlyList<PackerFileResult>? files, int maxFiles)
    {
        if (files == null) return false;
        try
        {
            var checkedFiles = Validate(category, files.Select(f => new PackRequestFile(f.Img, f.From, f.Op)).ToList(), maxFiles);
            return checkedFiles.Count == files.Count;
        }
        catch (ViewerApiException)
        {
            return false;
        }
    }

    private static string RequireImg(string category, string? img)
    {
        var segments = ViewerPathResolver.ValidatePattern(img);   // letters, digits, space, '_', '-', '.', '/', ends with .img, no hidden or Backups segment
        if (segments.Length < 2 || !string.Equals(segments[0], category, StringComparison.Ordinal))
            throw new ViewerApiException(400, "bad_path", "img must start with the category and a folder separator");
        if (segments.Length > MaxDepth)
            throw new ViewerApiException(400, "bad_path", "img is nested too deeply");
        return img!;
    }
}
