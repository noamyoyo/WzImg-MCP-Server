using System.Text.Json.Serialization;
using MapleLib.Helpers;
using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;
using WzImgMCP.Core;
using WzImgMCP.Viewer;

namespace WzImgMCP.Import;

public static class ImportStatus
{
    public const string Downloading = "downloading";
    public const string Extracting = "extracting";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Applying = "applying";
    public const string Applied = "applied";
    public const string ApplyInterrupted = "apply_interrupted";
    public const string UndonePartial = "undone_partial";
}

public static class ImportKind
{
    public const string Wz = "wz";
    public const string Files = "files";
}

/// <summary>The saved state of one staged import (<c>inbox/&lt;id&gt;/state.json</c>).</summary>
public sealed class ImportRecord
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = ImportKind.Wz;
    public string Status { get; set; } = ImportStatus.Downloading;
    public string? Phase { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? AppliedUtc { get; set; }
    /// <summary>Host of the link (never the whole address: it may hold a file id or a key).</summary>
    public string? Host { get; set; }
    public long BytesDownloaded { get; set; }
    public long? TotalBytes { get; set; }
    public string? Sha256 { get; set; }
    public string? FileName { get; set; }
    /// <summary>The category as the tree spells it (kind wz).</summary>
    public string? Category { get; set; }
    public string? ErrorCode { get; set; }
    public string? Error { get; set; }
    public int ImagesInSource { get; set; }
    public string? TokenHash { get; set; }
    public string? PreviewFingerprint { get; set; }
    public DateTime? PreviewedUtc { get; set; }
    public int AppliedAdded { get; set; }
    public int AppliedReplaced { get; set; }
    public List<StagedFile>? Files { get; set; }
    public List<string>? Warnings { get; set; }
}

/// <summary>A loose file unpacked by an import of kind files.</summary>
public sealed class StagedFile
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>One image the apply writes (kept in <c>plan.json</c>).</summary>
public sealed class PlanEntry
{
    /// <summary>"new" or "changed".</summary>
    public string Action { get; set; } = "";
    /// <summary>Path of the image below the extracted category folder, '/' separated.</summary>
    public string ImportPath { get; set; } = "";
    /// <summary>Path below the tree's category folder: the existing spelling for a changed image, the planned one for a new image.</summary>
    public string TreePath { get; set; } = "";
    public string ImportSha { get; set; } = "";
    /// <summary>Hash of the tree file at preview time; null for a new image.</summary>
    public string? TreeSha { get; set; }
    public long Size { get; set; }
}

/// <summary>One line of the write-ahead journal (<c>journal.jsonl</c>): what the apply is about to do to one tree file.</summary>
public sealed class JournalEntry
{
    /// <summary>"added" or "replaced".</summary>
    public string Action { get; set; } = "";
    public string TreePath { get; set; } = "";
    public string? OriginalSha { get; set; }
    public string NewSha { get; set; } = "";
    /// <summary>Path of the backup below <c>backup/</c> (replaced only).</summary>
    public string? Backup { get; set; }
    /// <summary>Folders (relative to the category folder) this entry created.</summary>
    public List<string>? CreatedDirs { get; set; }
}

internal static class ImgProbe
{
    private static readonly WzMapleVersion[] Keys = { WzMapleVersion.BMS, WzMapleVersion.GMS, WzMapleVersion.EMS };

    /// <summary>
    /// Reads and parses one .img file with each key in turn. Returns null and a short reason when it is too big, cannot be read or
    /// no key opens it. Release the result with <see cref="LoadedImage.Release"/>.
    /// </summary>
    public static LoadedImage? TryLoad(string path, string label, long maxBytes, out string? problem)
    {
        problem = null;
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > maxBytes)
            {
                problem = $"the image is larger than {maxBytes / (1024 * 1024)} MB";
                return null;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            bytes = new byte[info.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "the file could not be read";
            return null;
        }

        var sha = ImportFs.Sha256Bytes(bytes);
        foreach (var key in Keys)
        {
            WzImage? image = null;
            try
            {
                var deserializer = new WzImgDeserializer(freeResources: false, calculateChecksum: false);
                image = deserializer.WzImageFromIMGBytes(bytes, key, System.IO.Path.GetFileName(path), freeResources: false);
                if (image.ParseImage())
                    return new LoadedImage(new ImageKey("import", label, 0, bytes.Length), image, key.ToString(), 0, bytes.Length, 0, sha);
            }
            catch (OutOfMemoryException)
            {
                image?.Dispose();
                problem = "the image needs too much memory to read";
                return null;
            }
            catch (Exception)
            {
                // this key does not fit, or the file is damaged: try the next key
            }
            finally
            {
                ErrorLogger.ClearErrors();
            }
            image?.Dispose();
        }
        problem = "the image could not be opened with any known key (it is damaged)";
        return null;
    }
}

#region Results

public abstract class ImportResultBase : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? Error { get; set; }
}

public sealed class ImportFailure : ImportResultBase
{
    public static ImportFailure From(string code, string message) => new() { Success = false, ErrorCode = code, Error = message };
}

public sealed class ImportFetchResult : ImportResultBase
{
    public string? Id { get; set; }
    public string? Kind { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
}

public sealed class ImportStatusResult : ImportResultBase
{
    public string? Id { get; set; }
    public string? Kind { get; set; }
    public string? Status { get; set; }
    public string? Phase { get; set; }
    public string? Category { get; set; }
    public string? FileName { get; set; }
    public long BytesDownloaded { get; set; }
    public long? TotalBytes { get; set; }
    public string? Sha256 { get; set; }
    public int ImagesInSource { get; set; }
    public string? CreatedUtc { get; set; }
    public string? ExpiresUtc { get; set; }
    public string? JobErrorCode { get; set; }
    public string? JobError { get; set; }
    public List<string>? Warnings { get; set; }
    public List<StagedFile>? Files { get; set; }
    public int AppliedAdded { get; set; }
    public int AppliedReplaced { get; set; }
    public string? NextStep { get; set; }
}

public sealed class PreviewEntry
{
    public string? Path { get; set; }
    public string? Kind { get; set; }
    public string? Type { get; set; }
}

public sealed class PreviewChangedImage
{
    public string? Path { get; set; }
    public int Added { get; set; }
    public int Removed { get; set; }
    public int Changed { get; set; }
    public bool EntriesTruncated { get; set; }
    public string? Note { get; set; }
    public List<PreviewEntry>? Entries { get; set; }
}

public sealed class PreviewProblem
{
    public string? Path { get; set; }
    public string? Reason { get; set; }
}

public sealed class ImportPreviewResult : ImportResultBase
{
    public string? Id { get; set; }
    public string? Category { get; set; }
    public int NewCount { get; set; }
    public int ChangedCount { get; set; }
    public int UnchangedCount { get; set; }
    public int UnreadableCount { get; set; }
    /// <summary>Images that are in the tree but not in the upload; an import never deletes them.</summary>
    public int LeftAloneCount { get; set; }
    public List<string>? NewImages { get; set; }
    public List<PreviewChangedImage>? ChangedImages { get; set; }
    public List<PreviewProblem>? UnreadableImages { get; set; }
    public List<string>? LeftAloneImages { get; set; }
    public bool Truncated { get; set; }
    public List<string>? Warnings { get; set; }
    public string? Token { get; set; }
    public string? NextStep { get; set; }
}

public sealed class ImportApplyResult : ImportResultBase
{
    public string? Id { get; set; }
    public int AddedCount { get; set; }
    public int ReplacedCount { get; set; }
    public string? Message { get; set; }
    public List<string>? Warnings { get; set; }
}

public sealed class ImportUndoResult : ImportResultBase
{
    public string? Id { get; set; }
    public string? Outcome { get; set; }
    public int RestoredCount { get; set; }
    public int RemovedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<PreviewProblem>? Skipped { get; set; }
    public bool StagingDeleted { get; set; }
    public string? Message { get; set; }
}

#endregion
