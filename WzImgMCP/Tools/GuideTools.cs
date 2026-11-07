using System.ComponentModel;
using ModelContextProtocol.Server;
using WzImgMCP.Guides;
using WzImgMCP.Import;

namespace WzImgMCP.Tools;

/// <summary>
/// The editor documents itself: get_guide lists the guides, or returns one. It changes nothing and needs no data source, so it works
/// at any moment and on every editor container. The guides are embedded markdown files, see <see cref="GuideCatalog"/>.
/// </summary>
[McpServerToolType]
public class GuideTools
{
    private readonly ImportService _import;

    public GuideTools(ImportService import)
    {
        _import = import;
    }

    [McpServerTool(Name = "get_guide", ReadOnly = true), Description(
        "Read the operating guide of this editor. CALL THIS FIRST for any import, or any task you are not sure about. " +
        "Without topic it lists the guides and when to use each. With topic it returns that guide as markdown. " +
        "Topics: \"start\" (what this editor is, what reaches players, the tool families, safety habits; read it at the start of a session) and " +
        "\"import-by-link\" (getting a .wz file or PNG sprites into the tree from a Google Drive link, the limits in force, undo, and what each error means). " +
        "Changes nothing and needs no data source.")]
    public string GetGuide(
        [Description("Guide topic: \"start\" or \"import-by-link\". Leave empty to list the topics.")] string? topic = null)
        => GuideCatalog.Get(topic, _import.Options);
}
