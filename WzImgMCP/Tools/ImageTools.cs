﻿using System.ComponentModel;
using ModelContextProtocol.Protocol.Types;
using ModelContextProtocol.Server;
using WzImgMCP.Imaging;
using WzImgMCP.Core;
using WzImgMCP.Server;
using WzImgMCP.Utils;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace WzImgMCP.Tools;

/// <summary>
/// MCP tools for canvas/image operations
/// </summary>
[McpServerToolType]
public class ImageTools : ToolBase
{
    public ImageTools(WzSessionManager session) : base(session) { }

    [McpServerTool(Name = "get_canvas_bitmap"), Description("Get canvas image as PNG (base64 encoded text, full size, omitted with a note when the base64 text would exceed the response limit, 8 MiB by default). To look at an image use view_canvas instead.")]
    public string GetCanvasBitmap(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Property path to the canvas")] string path)
    {
        return Execute(() =>
        {
            var prop = GetRequiredProperty<WzCanvasProperty>(category, image, path, "Canvas");
            var base64 = WzDataConverter.GetCanvasBase64Capped(prop, ServerSettings.MaxResponseBytes, out var tooLarge);
            if (base64 == null && tooLarge == null)
                throw new InvalidOperationException("Failed to extract bitmap");

            return new CanvasBitmapData
            {
                Width = prop.PngProperty?.Width ?? 0,
                Height = prop.PngProperty?.Height ?? 0,
                Base64Png = base64,
                ImageNote = tooLarge
            };
        });
    }

    [McpServerTool(Name = "get_canvas_info"), Description("Get canvas metadata without the actual image data")]
    public string GetCanvasInfo(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Property path to the canvas")] string path)
    {
        return Execute(() =>
        {
            var prop = GetRequiredProperty<WzCanvasProperty>(category, image, path, "Canvas");
            return new CanvasInfoData
            {
                Width = prop.PngProperty?.Width ?? 0,
                Height = prop.PngProperty?.Height ?? 0,
                Format = prop.PngProperty?.Format.ToString(),
                HasChildren = prop.WzProperties?.Count > 0,
                ChildCount = prop.WzProperties?.Count ?? 0,
                Origin = WzDataConverter.GetCanvasOrigin(prop),
                Delay = WzDataConverter.GetCanvasDelay(prop)
            };
        });
    }

    [McpServerTool(Name = "get_canvas_origin"), Description("Get the canvas origin point (draw offset)")]
    public string GetCanvasOrigin(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Property path to the canvas")] string path)
    {
        return Execute(() =>
        {
            var prop = GetRequiredProperty<WzCanvasProperty>(category, image, path, "Canvas");
            var origin = WzDataConverter.GetCanvasOrigin(prop)
                ?? throw new InvalidOperationException("No origin property found");
            return new Point2D(origin.X, origin.Y);
        });
    }

    [McpServerTool(Name = "get_canvas_delay"), Description("Get animation frame delay in milliseconds")]
    public string GetCanvasDelay(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Property path to the canvas")] string path)
    {
        return Execute(() =>
        {
            var prop = GetRequiredProperty<WzCanvasProperty>(category, image, path, "Canvas");
            return new DelayData { Delay = WzDataConverter.GetCanvasDelay(prop) };
        });
    }

    [McpServerTool(Name = "get_animation_frames"), Description("Get animation frames with metadata. Use metadataOnly=true to skip image data and reduce response size. Use offset/limit for pagination.")]
    public string GetAnimationFrames(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Property path to the animation container")] string path,
        [Description("Only return metadata without Base64 image data (default: true for smaller responses)")] bool metadataOnly = true,
        [Description("Frame offset for pagination (default: 0)")] int offset = 0,
        [Description("Maximum frames to return (default: 5, max: 50)")] int limit = 5)
    {
        return Execute(() =>
        {
            var img = GetImage(category, image);
            var prop = img.GetFromPath(path)
                ?? throw new InvalidOperationException($"Property not found: {path}");

            // Clamp limit to prevent huge responses
            limit = Math.Clamp(limit, 1, 50);

            // First pass: count total frames and calculate duration
            int totalFrames = 0;
            int totalDuration = 0;
            for (int i = 0; ; i++)
            {
                if (prop[i.ToString()] is not WzCanvasProperty frameProp) break;
                totalFrames++;
                totalDuration += WzDataConverter.GetCanvasDelay(frameProp);
            }

            // Second pass: get requested page of frames
            var frames = new List<FrameData>();
            long budget = ServerSettings.MaxResponseBytes;   // total base64 characters for all frames of this call
            bool omitted = false;
            for (int i = offset; i < totalFrames && frames.Count < limit; i++)
            {
                if (prop[i.ToString()] is not WzCanvasProperty frameProp) break;

                var origin = WzDataConverter.GetCanvasOrigin(frameProp);
                var delay = WzDataConverter.GetCanvasDelay(frameProp);

                string? frameBase64 = null;
                string? frameNote = null;
                if (!metadataOnly)
                {
                    frameBase64 = WzDataConverter.GetCanvasBase64Capped(frameProp, budget, out frameNote);
                    if (frameBase64 != null)
                        budget -= frameBase64.Length;
                    else if (frameNote != null)
                        omitted = true;
                }

                frames.Add(new FrameData
                {
                    Index = i,
                    Width = frameProp.PngProperty?.Width ?? 0,
                    Height = frameProp.PngProperty?.Height ?? 0,
                    Origin = origin,
                    Delay = delay,
                    Base64Png = frameBase64,
                    ImageNote = frameNote
                });
            }

            return new AnimationData
            {
                FrameCount = totalFrames,
                TotalDuration = totalDuration,
                Offset = offset,
                Limit = limit,
                HasMore = offset + frames.Count < totalFrames,
                Note = omitted
                    ? $"Truncated: the image data of some frames was omitted because the response would exceed {ServerSettings.MaxResponseBytes:N0} base64 characters in total (WZIMG_MAX_RESPONSE_BYTES). Use view_canvas or export_png for single frames, or a smaller limit."
                    : null,
                Frames = frames
            };
        });
    }

    [McpServerTool(Name = "list_canvas_in_image"), Description("List all canvas properties in an image")]
    public string ListCanvasInImage(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Maximum depth to search")] int maxDepth = 10)
    {
        return Execute(() =>
        {
            var img = GetImage(category, image);
            var canvases = new List<CanvasEntry>();
            FindCanvases(img, "", canvases, 0, maxDepth);

            return new CanvasListData
            {
                Category = category,
                Image = image,
                Count = canvases.Count,
                Canvases = canvases
            };
        });
    }

    [McpServerTool(Name = "get_canvas_head"), Description("Get the canvas head position (used for character rendering)")]
    public string GetCanvasHead(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Property path to the canvas")] string path)
    {
        return Execute(() =>
        {
            var prop = GetRequiredProperty<WzCanvasProperty>(category, image, path, "Canvas");
            var head = prop["head"] as WzVectorProperty
                ?? throw new InvalidOperationException("No head property found");
            return new Point2D(head.X.Value, head.Y.Value);
        });
    }

    [McpServerTool(Name = "get_canvas_bounds"), Description("Get the canvas bounds (lt - left-top position)")]
    public string GetCanvasBounds(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Property path to the canvas")] string path)
    {
        return Execute(() =>
        {
            var prop = GetRequiredProperty<WzCanvasProperty>(category, image, path, "Canvas");
            var lt = prop["lt"] as WzVectorProperty;
            var rb = prop["rb"] as WzVectorProperty;

            return new BoundsData
            {
                HasLt = lt != null,
                LtX = lt?.X.Value ?? 0,
                LtY = lt?.Y.Value ?? 0,
                HasRb = rb != null,
                RbX = rb?.X.Value ?? 0,
                RbY = rb?.Y.Value ?? 0
            };
        });
    }

    [McpServerTool(Name = "resolve_canvas_link"), Description("Resolve _inlink or _outlink canvas references")]
    public string ResolveCanvasLink(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Property path to the canvas")] string path)
    {
        return Execute(() =>
        {
            var img = GetImage(category, image);
            var prop = GetRequiredProperty<WzCanvasProperty>(category, image, path, "Canvas");

            // Check for _inlink
            if (prop["_inlink"] is WzStringProperty inlink)
            {
                var targetPath = inlink.Value;
                var targetProp = img.GetFromPath(targetPath) as WzCanvasProperty;

                if (targetProp != null)
                {
                    var targetBase64 = WzDataConverter.GetCanvasBase64Capped(targetProp, ServerSettings.MaxResponseBytes, out var targetTooLarge);
                    return new ResolveCanvasData
                    {
                        LinkType = "inlink",
                        LinkPath = targetPath,
                        Width = targetProp.PngProperty?.Width ?? 0,
                        Height = targetProp.PngProperty?.Height ?? 0,
                        Base64Png = targetBase64,
                        ImageNote = targetBase64 == null ? targetTooLarge ?? ImageNoteUndecodable : null
                    };
                }
                throw new InvalidOperationException($"Inlink target not found: {targetPath}");
            }

            // Check for _outlink
            if (prop["_outlink"] is WzStringProperty outlink)
            {
                throw new InvalidOperationException($"Outlink resolution requires cross-image access. Path: {outlink.Value}");
            }

            // No link - return the canvas itself
            var ownBase64 = WzDataConverter.GetCanvasBase64Capped(prop, ServerSettings.MaxResponseBytes, out var ownTooLarge);
            return new ResolveCanvasData
            {
                LinkType = "none",
                Width = prop.PngProperty?.Width ?? 0,
                Height = prop.PngProperty?.Height ?? 0,
                Base64Png = ownBase64,
                ImageNote = ownBase64 == null ? ownTooLarge ?? ImageNoteUndecodable : null
            };
        });
    }

    private const string ImageNoteUndecodable = "The image data of this canvas could not be decoded.";

    [McpServerTool(Name = "view_canvas"), Description("View a canvas image: returns it as an image content block (PNG) that the client can display, plus a text line with the size, WZ pixel format and origin. If the larger side is above maxSize (default and maximum 2048), or the PNG would exceed the response size limit (WZIMG_MAX_RESPONSE_BYTES, 8 MiB of base64 by default), the image is scaled down to fit and the text says so. _inlink / _outlink are followed.")]
    public CallToolResponse ViewCanvas(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Property path to the canvas")] string path,
        [Description("Largest allowed width or height in pixels, 1-2048 (default 2048). Larger images are scaled down.")] int maxSize = CanvasImaging.MaxViewDimension)
    {
        if (!Session.IsInitialized)
            return TextResponse("Error: No data source initialized. Call init_data_source first.", isError: true);

        try
        {
            var prop = GetRequiredProperty<WzCanvasProperty>(category, image, path, "Canvas");
            var png = CanvasImaging.EncodeCanvasForView(prop, maxSize, out var originalWidth, out var originalHeight,
                out var width, out var height, out var scaled);
            if (png == null)
                return TextResponse($"Error: Canvas has no image data: {path}", isError: true);

            // The PNG goes out as base64 text: keep that within the response limit by scaling down further (noisy images
            // compress badly), and refuse when even a very small version does not fit.
            long maxPngBytes = ServerSettings.MaxResponseBytes / 4 * 3;
            bool scaledForSize = false;
            int effectiveMax = Math.Clamp(maxSize, 1, CanvasImaging.MaxViewDimension);
            effectiveMax = Math.Min(effectiveMax, Math.Max(width, height));
            while (png.Length > maxPngBytes)
            {
                if (effectiveMax <= 16)
                    return TextResponse($"Error: The image of {path} ({originalWidth}x{originalHeight} px) does not fit the response size limit of " +
                                        $"{ServerSettings.MaxResponseBytes:N0} bytes even when scaled down to {width}x{height} px. Use export_png to write it to a file.", isError: true);
                effectiveMax = Math.Max(16, effectiveMax * 7 / 10);
                png = CanvasImaging.EncodeCanvasForView(prop, effectiveMax, out originalWidth, out originalHeight,
                    out width, out height, out scaled)!;
                scaledForSize = true;
            }

            var origin = WzDataConverter.GetCanvasOrigin(prop);
            var text = $"{category}/{image}/{path}: {originalWidth}x{originalHeight} px, format {prop.PngProperty?.Format}";
            if (origin != null)
                text += $", origin ({origin.X}, {origin.Y})";
            if (scaledForSize)
                text += $". Scaled down to {width}x{height} px to fit the response size limit of {ServerSettings.MaxResponseBytes:N0} bytes (original size is {originalWidth}x{originalHeight}); use export_png for the full image";
            else if (scaled)
                text += $". Scaled down to {width}x{height} px to fit the maximum of {Math.Clamp(maxSize, 1, CanvasImaging.MaxViewDimension)} px (original size is {originalWidth}x{originalHeight})";
            text += ".";

            return new CallToolResponse
            {
                Content = new List<Content>
                {
                    new Content { Type = "text", Text = text },
                    new Content { Type = "image", Data = Convert.ToBase64String(png), MimeType = "image/png" }
                }
            };
        }
        catch (Exception ex)
        {
            return TextResponse("Error: " + ex.Message, isError: true);
        }
    }

    private static CallToolResponse TextResponse(string text, bool isError) => new()
    {
        IsError = isError,
        Content = new List<Content> { new Content { Type = "text", Text = text } }
    };

    private void FindCanvases(WzObject obj, string path, List<CanvasEntry> results, int depth, int maxDepth)
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
                results.Add(new CanvasEntry
                {
                    Path = path,
                    Width = canvas.PngProperty?.Width ?? 0,
                    Height = canvas.PngProperty?.Height ?? 0
                });
            }
            children = prop.WzProperties;
        }

        if (children != null)
        {
            foreach (var child in children)
            {
                var childPath = string.IsNullOrEmpty(path) ? child.Name : $"{path}/{child.Name}";
                FindCanvases(child, childPath, results, depth + 1, maxDepth);
            }
        }
    }
}

// Data types

public class CanvasBitmapData
{
    public int Width { get; init; }
    public int Height { get; init; }
    public string? Base64Png { get; init; }
    public string? ImageNote { get; init; }
}

public class CanvasInfoData
{
    public int Width { get; init; }
    public int Height { get; init; }
    public string? Format { get; init; }
    public bool HasChildren { get; init; }
    public int ChildCount { get; init; }
    public VectorValue? Origin { get; init; }
    public int Delay { get; init; }
}

public class DelayData
{
    public int Delay { get; init; }
}

public class AnimationData
{
    public int FrameCount { get; init; }
    public int TotalDuration { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
    public bool HasMore { get; init; }
    public string? Note { get; init; }
    public required List<FrameData> Frames { get; init; }
}

public class FrameData
{
    public int Index { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public VectorValue? Origin { get; init; }
    public int Delay { get; init; }
    public string? Base64Png { get; init; }
    public string? ImageNote { get; init; }
}

public class CanvasListData
{
    public string? Category { get; init; }
    public string? Image { get; init; }
    public int Count { get; init; }
    public List<CanvasEntry>? Canvases { get; init; }
}

public class CanvasEntry
{
    public required string Path { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

public class BoundsData
{
    public bool HasLt { get; init; }
    public int LtX { get; init; }
    public int LtY { get; init; }
    public bool HasRb { get; init; }
    public int RbX { get; init; }
    public int RbY { get; init; }
}

public class ResolveCanvasData
{
    public string? LinkType { get; init; }
    public string? LinkPath { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? Base64Png { get; init; }
    public string? ImageNote { get; init; }
}

