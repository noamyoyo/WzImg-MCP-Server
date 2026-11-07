using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using WzImgMCP.Core;
using WzImgMCP.Imaging;
using WzImgMCP.Import;

namespace WzImgMCP.Guides;

/// <summary>One guide: its topic name and the one line that says when to read it.</summary>
public sealed record GuideTopic(string Name, string UseWhen);

/// <summary>
/// The guides the editor tells the client about (tool get_guide). The text of each guide is a markdown file in the Guides folder,
/// embedded in the assembly, so no extra file has to be mounted. A few numbers in the text are written as {{name}} and filled in
/// with the live settings of the editor when the guide is asked for, so the guide never disagrees with the limits in force.
/// </summary>
public static class GuideCatalog
{
    private const string ResourcePrefix = "WzImgMCP.Guides.";

    public static IReadOnlyList<GuideTopic> Topics { get; } = new[]
    {
        new GuideTopic("start",
            "Use this first in any new session or when the task is unfamiliar: what this editor is, what reaches players, the tool families and the safety habits."),
        new GuideTopic("import-by-link",
            "Use this when you need to get a .wz file or PNG sprites into the tree from a link (import_fetch, import_png), or when an import fails or has to be undone."),
    };

    public static string ResourceName(string topic) => ResourcePrefix + topic + ".md";

    /// <summary>The list of topics, or the text of one topic, or a plain error that names the valid topics.</summary>
    public static string Get(string? topic, ImportOptions options)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return List();
        var wanted = topic.Trim().ToLowerInvariant().Replace('_', '-');
        var found = Topics.FirstOrDefault(t => t.Name == wanted);
        if (found == null)
            return Result.Fail($"There is no guide called \"{Shorten(topic.Trim())}\". Valid topics: {string.Join(", ", Topics.Select(t => t.Name))}. Call get_guide with no topic to see when to use each.");
        return Fill(ReadTemplate(found.Name), options);
    }

    public static string List()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Guides for the WZ editor");
        sb.AppendLine();
        sb.AppendLine("Call get_guide(topic) to read one. Read `start` first if this is a new session.");
        sb.AppendLine();
        foreach (var t in Topics)
            sb.AppendLine($"- `{t.Name}`: {t.UseWhen}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>The guide text as stored in the assembly, placeholders not yet filled in.</summary>
    public static string ReadTemplate(string topic)
    {
        var assembly = typeof(GuideCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName(topic))
            ?? throw new InvalidOperationException($"The guide {topic} is missing from the assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    private static readonly Regex Placeholder = new(@"\{\{([A-Za-z]+)\}\}", RegexOptions.Compiled);

    /// <summary>Replaces each {{name}} with the live value. A name that is not known is left as it is (a test catches that).</summary>
    public static string Fill(string template, ImportOptions o)
    {
        var values = Values(o);
        return Placeholder.Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    public static IReadOnlyDictionary<string, string> Values(ImportOptions o) => new Dictionary<string, string>
    {
        ["maxDownloadMb"] = (o.MaxDownloadBytes / (1024 * 1024)).ToString(),
        ["timeoutSeconds"] = ((long)o.DownloadTimeout.TotalSeconds).ToString(),
        ["stallSeconds"] = ((long)o.StallTimeout.TotalSeconds).ToString(),
        ["maxFiles"] = o.FilesMaxCount.ToString(),
        ["maxUnpackedMb"] = (o.FilesMaxUnpackedBytes / (1024 * 1024)).ToString(),
        ["maxRatio"] = o.FilesMaxRatio.ToString(),
        ["maxStaged"] = o.MaxStaged.ToString(),
        ["retentionDays"] = o.Retention.TotalDays.ToString("0"),
        ["allowedHosts"] = string.Join(", ", o.AllowedHosts),
        ["pngMaxSide"] = CanvasImaging.MaxImportDimension.ToString(),
        ["pngMaxMb"] = (CanvasImaging.MaxImportBytes / (1024 * 1024)).ToString(),
    };

    private static string Shorten(string s) => s.Length <= 40 ? s : s.Substring(0, 40) + "...";
}
