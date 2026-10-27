using System.ComponentModel;
using ModelContextProtocol.Server;
using WzImgMCP.Core;
using WzImgMCP.Server;
using WzImgMCP.Utils;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace WzImgMCP.Tools;

/// <summary>
/// MCP tools for exporting WZ data to various formats
/// </summary>
[McpServerToolType]
public class ExportTools
{
    private readonly WzSessionManager _session;

    public ExportTools(WzSessionManager session)
    {
        _session = session;
    }

    [McpServerTool(Name = "export_to_md"), Description("Export a property tree to Markdown format. For large exports, outputPath is required. Max inline response is 100KB.")]
    public string ExportToMd(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Property path (empty for entire image)")] string? path = null,
        [Description("Maximum depth to export (default: 5, max: 10)")] int maxDepth = 5,
        [Description("Output file path (REQUIRED for exports > 100KB)")] string? outputPath = null)
    {
        if (!_session.IsInitialized)
        {
            return new ExportMarkdownResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            outputPath = PathGuard.ResolveOptional(outputPath);
            // Clamp maxDepth
            maxDepth = Math.Clamp(maxDepth, 1, 10);

            var img = _session.GetImage(category, image);
            WzObject target;

            if (string.IsNullOrEmpty(path))
            {
                target = img;
            }
            else
            {
                var prop = img.GetFromPath(path);
                if (prop == null)
                {
                    return new ExportMarkdownResult { Success = false, Error = $"Property not found: {path}" };
                }
                target = prop;
            }

            int nodeCount = 0;
            const int maxNodes = 5000;
            var exportData = ConvertToMarkdownModel(target, 0, maxDepth, ref nodeCount, maxNodes);
            var markdownString = MarkdownResultFormatter.Format(exportData);

            const int maxInlineBytes = 100 * 1024; // 100KB max inline

            if (!string.IsNullOrEmpty(outputPath))
            {
                outputPath = EnsureMarkdownOutputPath(outputPath);
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                PathGuard.WriteFileNoFollow(outputPath, new System.Text.UTF8Encoding(false).GetBytes(markdownString));

                return new ExportMarkdownResult
                {
                    Success = true,
                    OutputPath = outputPath,
                    Size = markdownString.Length,
                    NodeCount = nodeCount,
                    Truncated = nodeCount >= maxNodes
                };
            }

            // Check size for inline response
            if (markdownString.Length > maxInlineBytes)
            {
                return new ExportMarkdownResult
                {
                    Success = false,
                    Error = $"Response too large ({markdownString.Length / 1024}KB). Please provide outputPath for exports > 100KB.",
                    Size = markdownString.Length,
                    NodeCount = nodeCount
                };
            }

            return new ExportMarkdownResult
            {
                Success = true,
                MarkdownData = markdownString,
                Size = markdownString.Length,
                NodeCount = nodeCount,
                Truncated = nodeCount >= maxNodes
            };
        }
        catch (Exception ex)
        {
            return new ExportMarkdownResult { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "export_png"), Description("Export a canvas property to a PNG file at its full size (the output path must be inside the allowed root when WZIMG_ALLOWED_ROOT is set). To look at an image use view_canvas.")]
    public string ExportPng(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Property path to the canvas")] string path,
        [Description("Output file path")] string outputPath)
    {
        if (!_session.IsInitialized)
        {
            return new ExportPngResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            outputPath = PathGuard.Resolve(outputPath);
            var img = _session.GetImage(category, image);
            var prop = img.GetFromPath(path) as WzCanvasProperty;

            if (prop == null)
            {
                return new ExportPngResult { Success = false, Error = $"Canvas property not found: {path}" };
            }

            var png = WzImgMCP.Imaging.CanvasImaging.EncodeCanvas(prop, WzImgMCP.Imaging.ExportImageFormat.Png, out var pngWidth, out var pngHeight);
            if (png == null)
            {
                return new ExportPngResult { Success = false, Error = "Failed to extract bitmap" };
            }

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            PathGuard.WriteFileNoFollow(outputPath, png);

            return new ExportPngResult
            {
                Success = true,
                OutputPath = outputPath,
                Width = pngWidth,
                Height = pngHeight,
                Size = new FileInfo(outputPath).Length
            };
        }
        catch (Exception ex)
        {
            return new ExportPngResult { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "export_mp3"), Description("Export a sound property to MP3 file")]
    public string ExportMp3(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Property path to the sound")] string path,
        [Description("Output file path")] string outputPath)
    {
        if (!_session.IsInitialized)
        {
            return new ExportMp3Result { Success = false, Error = "No data source initialized" };
        }

        try
        {
            outputPath = PathGuard.Resolve(outputPath);
            var img = _session.GetImage(category, image);
            var prop = img.GetFromPath(path) as WzBinaryProperty;

            if (prop == null)
            {
                return new ExportMp3Result { Success = false, Error = $"Sound property not found: {path}" };
            }

            var data = prop.GetBytes(false);
            if (data == null || data.Length == 0)
            {
                return new ExportMp3Result { Success = false, Error = "Failed to extract sound data" };
            }

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            PathGuard.WriteFileNoFollow(outputPath, data);

            return new ExportMp3Result
            {
                Success = true,
                OutputPath = outputPath,
                Duration = prop.Length,
                Frequency = prop.Frequency,
                Size = data.Length
            };
        }
        catch (Exception ex)
        {
            return new ExportMp3Result { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "export_all_images"), Description("Export all canvas properties from a path to PNG files")]
    public string ExportAllImages(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Output directory")] string outputDir,
        [Description("Property path (empty for entire image)")] string? path = null,
        [Description("Maximum depth to search")] int maxDepth = 10)
    {
        if (!_session.IsInitialized)
        {
            return new BatchExportResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            outputDir = PathGuard.Resolve(outputDir);
            var img = _session.GetImage(category, image);
            WzObject target;

            if (string.IsNullOrEmpty(path))
            {
                target = img;
            }
            else
            {
                var prop = img.GetFromPath(path);
                if (prop == null)
                {
                    return new BatchExportResult { Success = false, Error = $"Property not found: {path}" };
                }
                target = prop;
            }

            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            var exported = new List<ExportedItem>();
            var failed = new List<FailedItem>();

            ExportCanvasesRecursive(target, "", outputDir, exported, failed, 0, maxDepth, PathGuard.NewNameSet());

            return new BatchExportResult
            {
                Success = true,
                OutputDirectory = outputDir,
                ExportedCount = exported.Count,
                FailedCount = failed.Count,
                Exported = exported,
                Failed = failed
            };
        }
        catch (Exception ex)
        {
            return new BatchExportResult { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "export_all_sounds"), Description("Export all sound properties from a path to MP3 files")]
    public string ExportAllSounds(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Output directory")] string outputDir,
        [Description("Property path (empty for entire image)")] string? path = null,
        [Description("Maximum depth to search")] int maxDepth = 10)
    {
        if (!_session.IsInitialized)
        {
            return new BatchExportResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            outputDir = PathGuard.Resolve(outputDir);
            var img = _session.GetImage(category, image);
            WzObject target;

            if (string.IsNullOrEmpty(path))
            {
                target = img;
            }
            else
            {
                var prop = img.GetFromPath(path);
                if (prop == null)
                {
                    return new BatchExportResult { Success = false, Error = $"Property not found: {path}" };
                }
                target = prop;
            }

            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            var exported = new List<ExportedItem>();
            var failed = new List<FailedItem>();

            ExportSoundsRecursive(target, "", outputDir, exported, failed, 0, maxDepth, PathGuard.NewNameSet());

            return new BatchExportResult
            {
                Success = true,
                OutputDirectory = outputDir,
                ExportedCount = exported.Count,
                FailedCount = failed.Count,
                Exported = exported,
                Failed = failed
            };
        }
        catch (Exception ex)
        {
            return new BatchExportResult { Success = false, Error = ex.Message };
        }
    }

    private void ExportCanvasesRecursive(WzObject obj, string path, string outputDir,
        List<ExportedItem> exported, List<FailedItem> failed, int depth, int maxDepth, HashSet<string> used)
    {
        if (depth > maxDepth) return;

        IEnumerable<WzImageProperty>? children = null;

        if (obj is WzImage img)
        {
            children = img.WzProperties;
        }
        else if (obj is WzImageProperty prop)
        {
            if (prop is WzCanvasProperty canvas)
            {
                try
                {
                    var pngBytes = WzImgMCP.Imaging.CanvasImaging.EncodeCanvas(canvas, WzImgMCP.Imaging.ExportImageFormat.Png, out _, out _);
                    if (pngBytes != null)
                    {
                        var fileName = PathGuard.UniqueName(PathGuard.SanitizeFileName(path, "root"), used);
                        var filePath = PathGuard.WriteFileGuarded(outputDir, $"{fileName}.png", pngBytes);
                        exported.Add(new ExportedItem
                        {
                            SourcePath = path,
                            OutputPath = filePath,
                            Size = new FileInfo(filePath).Length
                        });
                    }
                }
                catch (PathNotAllowedException) { throw; }
                catch (Exception ex)
                {
                    failed.Add(new FailedItem { Path = path, Error = ex.Message });
                }
            }

            children = prop.WzProperties;
        }

        if (children != null)
        {
            foreach (var child in children)
            {
                var childPath = string.IsNullOrEmpty(path) ? child.Name : $"{path}/{child.Name}";
                ExportCanvasesRecursive(child, childPath, outputDir, exported, failed, depth + 1, maxDepth, used);
            }
        }
    }

    private void ExportSoundsRecursive(WzObject obj, string path, string outputDir,
        List<ExportedItem> exported, List<FailedItem> failed, int depth, int maxDepth, HashSet<string> used)
    {
        if (depth > maxDepth) return;

        IEnumerable<WzImageProperty>? children = null;

        if (obj is WzImage img)
        {
            children = img.WzProperties;
        }
        else if (obj is WzImageProperty prop)
        {
            if (prop is WzBinaryProperty sound)
            {
                try
                {
                    var data = sound.GetBytes(false);
                    if (data != null && data.Length > 0)
                    {
                        var fileName = PathGuard.UniqueName(PathGuard.SanitizeFileName(path, "root"), used);
                        var filePath = PathGuard.WriteFileGuarded(outputDir, $"{fileName}.mp3", data);
                        exported.Add(new ExportedItem
                        {
                            SourcePath = path,
                            OutputPath = filePath,
                            Size = data.Length
                        });
                    }
                }
                catch (PathNotAllowedException) { throw; }
                catch (Exception ex)
                {
                    failed.Add(new FailedItem { Path = path, Error = ex.Message });
                }
            }

            children = prop.WzProperties;
        }

        if (children != null)
        {
            foreach (var child in children)
            {
                var childPath = string.IsNullOrEmpty(path) ? child.Name : $"{path}/{child.Name}";
                ExportSoundsRecursive(child, childPath, outputDir, exported, failed, depth + 1, maxDepth, used);
            }
        }
    }

    private Dictionary<string, object?> ConvertToMarkdownModel(WzObject obj, int depth, int maxDepth, ref int nodeCount, int maxNodes)
    {
        nodeCount++;

        var result = new Dictionary<string, object?>
        {
            ["_name"] = obj.Name,
            ["_type"] = obj is WzImageProperty typeProp ? typeProp.PropertyType.ToString() : obj.GetType().Name
        };

        if (obj is WzImageProperty imgProp)
        {
            var value = WzDataConverter.GetPropertyValue(imgProp);
            if (value != null)
            {
                result["_value"] = value;
            }
        }

        if (depth < maxDepth && nodeCount < maxNodes)
        {
            IEnumerable<WzImageProperty>? children = null;

            if (obj is WzImage img)
            {
                children = img.WzProperties;
            }
            else if (obj is WzImageProperty childProp)
            {
                children = childProp.WzProperties;
            }

            if (children != null && children.Any())
            {
                var childDict = new Dictionary<string, object?>();
                foreach (var child in children)
                {
                    if (nodeCount >= maxNodes) break;
                    childDict[child.Name] = ConvertToMarkdownModel(child, depth + 1, maxDepth, ref nodeCount, maxNodes);
                }
                result["_children"] = childDict;
            }
        }

        return result;
    }

    private static string EnsureMarkdownOutputPath(string outputPath)
    {
        return string.Equals(Path.GetExtension(outputPath), ".md", StringComparison.OrdinalIgnoreCase)
            ? outputPath
            : Path.ChangeExtension(outputPath, ".md");
    }

}

// Result types

public class ExportMarkdownResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? OutputPath { get; set; }
    public string? MarkdownData { get; set; }
    public long Size { get; set; }
    public int NodeCount { get; set; }
    public bool Truncated { get; set; }
}

public class ExportPngResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? OutputPath { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long Size { get; set; }
}

public class ExportMp3Result : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? OutputPath { get; set; }
    public int Duration { get; set; }
    public int Frequency { get; set; }
    public long Size { get; set; }
}

public class BatchExportResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? OutputDirectory { get; set; }
    public int ExportedCount { get; set; }
    public int FailedCount { get; set; }
    public List<ExportedItem>? Exported { get; set; }
    public List<FailedItem>? Failed { get; set; }
}

public class ExportedItem
{
    public required string SourcePath { get; set; }
    public required string OutputPath { get; set; }
    public long Size { get; set; }
}

public class FailedItem
{
    public required string Path { get; set; }
    public required string Error { get; set; }
}
