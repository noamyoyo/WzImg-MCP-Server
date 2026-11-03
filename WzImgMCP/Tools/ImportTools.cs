using System.ComponentModel;
using ModelContextProtocol.Server;
using WzImgMCP.Import;

namespace WzImgMCP.Tools;

/// <summary>
/// MCP tools for "import by link": the editor downloads a file from an allowed link itself, so a .wz file or a folder of sprites
/// does not have to pass through the conversation. See <see cref="ImportService"/> for the rules.
/// </summary>
[McpServerToolType]
public class ImportTools
{
    private readonly ImportService _import;

    public ImportTools(ImportService import)
    {
        _import = import;
    }

    [McpServerTool(Name = "import_fetch"), Description(
        "Start downloading a file from a link, in the background, and return an id at once. Only secure Google Drive links are accepted (share the file with \"anyone with the link\"). " +
        "kind \"wz\" (default): a <Category>.wz file in GMS key for a category that already exists in the tree (not List.wz or Sound.wz); it is unpacked and compared with the tree, and changes nothing until import_apply. " +
        "kind \"files\": one PNG or a flat zip of PNGs; they are unpacked into a staging folder and the result lists the file paths to pass to import_png(... pngPath=...); the tree is not touched. " +
        "Check progress with import_status(id). One download at a time; at most 3 staged imports; staged files are deleted after 7 days.")]
    public string ImportFetch(
        [Description("Link to the file (https, Google Drive)")] string url,
        [Description("\"wz\" (a .wz file for the tree, default) or \"files\" (a PNG or a zip of PNGs)")] string kind = "wz",
        [Description("Optional SHA-256 of the file (64 hex characters); the import is refused when the download does not match")] string? expectedSha256 = null,
        [Description("Only for kind wz when the file is not named <Category>.wz: the category it is for, for example Skill")] string? category = null)
        => _import.Fetch(url, kind, expectedSha256, category);

    [McpServerTool(Name = "import_status"), Description("Show the progress and state of an import started with import_fetch. For kind files it lists the staged file paths.")]
    public string ImportStatus(
        [Description("The id import_fetch returned")] string id)
        => _import.GetStatus(id);

    [McpServerTool(Name = "import_preview"), Description(
        "Dry run for a wz import: lists images that are new, changed (with the properties added, removed and changed), unchanged and unreadable. " +
        "Images that are in the tree but not in the upload are left alone (an import never deletes). Changes nothing; returns a single-use confirmation token for exactly this preview.")]
    public string ImportPreview(
        [Description("The id import_fetch returned")] string id)
        => _import.Preview(id);

    [McpServerTool(Name = "import_apply"), Description(
        "Apply a previewed wz import: copies only the new and changed .img files into the tree after backing up what it replaces. Refuses if any affected file changed since the preview, " +
        "or if an affected image has unsaved edits in the editor. The token works once; preview again for a new one.")]
    public string ImportApply(
        [Description("The id import_fetch returned")] string id,
        [Description("The confirmation token import_preview returned")] string token)
        => _import.Apply(id, token);

    [McpServerTool(Name = "import_undo"), Description(
        "Undo an applied import: puts the backups back and removes the images the import added; files edited after the import are skipped and listed. " +
        "On an import that was never applied it just deletes the staged files (this also frees a place when 3 imports are staged), and on one still downloading it cancels it.")]
    public string ImportUndo(
        [Description("The id import_fetch returned")] string id)
        => _import.Undo(id);
}
