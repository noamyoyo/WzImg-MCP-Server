using MapleLib.WzLib.WzProperties;
using Microsoft.Xna.Framework.Graphics;
using WzImgMCP.Utils;
using System.Runtime.InteropServices;
#if LINUX_BUILD
using SkiaSharp;
#else
using System.Drawing;
using System.Drawing.Imaging;
#endif

namespace WzImgMCP.Imaging;

/// <summary>Thrown when an image is refused because of its size, its type or its content.</summary>
public sealed class ImageRejectedException : Exception
{
    public ImageRejectedException(string message) : base(message) { }
}

/// <summary>Output file format of an exported canvas.</summary>
public enum ExportImageFormat
{
    Png,
    Jpeg
}

/// <summary>
/// The only place in the server that touches an image library. On the Windows build it uses System.Drawing
/// (GDI+) exactly as before; on the Linux build (<c>LINUX_BUILD</c>) it uses SkiaSharp for PNG/JPEG decoding,
/// encoding and scaling, and MapleLib's raw BGRA32 pixel API (<see cref="WzPngProperty.GetPixelsBgra32"/> /
/// <see cref="WzPngProperty.SetPixelsBgra32"/>) for the WZ pixel formats.
/// </summary>
public static class CanvasImaging
{
    /// <summary>Largest width or height of a PNG that import_png / set_canvas_bitmap accept.</summary>
    public const int MaxImportDimension = 4096;

    /// <summary>Largest PNG file (or decoded base64 payload) that import_png / set_canvas_bitmap accept.</summary>
    public const long MaxImportBytes = 20L * 1024 * 1024;

    /// <summary>Largest width or height of the image that view_canvas returns.</summary>
    public const int MaxViewDimension = 2048;

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    #region Platform independent input checks

    /// <summary>
    /// Reads width and height from the PNG header without decoding anything, and refuses data that is not a PNG
    /// or is larger than <see cref="MaxImportDimension"/> in either direction.
    /// </summary>
    public static (int Width, int Height) ReadAndCheckPngHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxImportBytes)
            throw new ImageRejectedException($"PNG is too large: {data.Length} bytes, the limit is {MaxImportBytes} bytes.");
        // 8 byte signature, then the IHDR chunk: length (4), "IHDR" (4), width (4), height (4).
        if (data.Length < 24 || !data.Slice(0, 8).SequenceEqual(PngSignature))
            throw new ImageRejectedException("The data is not a PNG image (missing PNG signature).");
        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new ImageRejectedException("The data is not a valid PNG image (the first chunk is not IHDR).");

        uint width = ReadUInt32BigEndian(data.Slice(16, 4));
        uint height = ReadUInt32BigEndian(data.Slice(20, 4));
        if (width == 0 || height == 0)
            throw new ImageRejectedException("The PNG declares a zero width or height.");
        if (width > MaxImportDimension || height > MaxImportDimension)
            throw new ImageRejectedException(
                $"PNG is too large: {width}x{height} pixels, the limit is {MaxImportDimension}x{MaxImportDimension}.");
        return ((int)width, (int)height);
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> b) =>
        ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

    /// <summary>
    /// Reads a stream to its end but never more than <paramref name="limit"/> + 1 bytes; throws when the stream holds more
    /// than <paramref name="limit"/> bytes.
    /// </summary>
    public static byte[] ReadAtMost(Stream stream, long limit, string what)
    {
        using var ms = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        while (true)
        {
            int want = (int)Math.Min(chunk.Length, limit + 1 - total);
            if (want <= 0) break;
            int read = stream.Read(chunk, 0, want);
            if (read <= 0) break;
            ms.Write(chunk, 0, read);
            total += read;
        }
        if (total > limit)
            throw new ImageRejectedException($"{what} is too large: more than {limit} bytes, the limit is {limit} bytes.");
        return ms.ToArray();
    }

    /// <summary>
    /// Returns the PNG bytes given either as base64 or as a file path. A path must lie inside
    /// WZIMG_ALLOWED_ROOT (when set). Exactly one of the two must be given. Size limits are enforced before the
    /// data is read or decoded.
    /// </summary>
    public static byte[] ReadPngInput(string? base64Png, string? pngPath)
    {
        bool hasBase64 = !string.IsNullOrWhiteSpace(base64Png);
        bool hasPath = !string.IsNullOrWhiteSpace(pngPath);
        if (hasBase64 == hasPath)
            throw new ArgumentException("Provide exactly one of base64Png or pngPath.");

        if (hasPath)
        {
            string resolved = PathGuard.Resolve(pngPath!);
            var info = new FileInfo(resolved);
            if (!info.Exists)
                throw new FileNotFoundException($"PNG file not found: {pngPath}");
            if (info.Length > MaxImportBytes)
                throw new ImageRejectedException($"PNG file is too large: {info.Length} bytes, the limit is {MaxImportBytes} bytes.");
            // Read with a hard limit instead of trusting the length that was checked a moment ago: the file may have grown
            // since (or be a stream that never ends), so never take more than limit + 1 bytes.
            using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadAtMost(stream, MaxImportBytes, "PNG file");
        }

        // 4 base64 characters carry 3 bytes; refuse before allocating the decoded buffer.
        long maxChars = (MaxImportBytes + 2) / 3 * 4 + 4;
        if (base64Png!.Length > maxChars)
            throw new ImageRejectedException($"PNG is too large: the base64 text is {base64Png.Length} characters, the limit is {MaxImportBytes} bytes of PNG data.");
        try
        {
            return Convert.FromBase64String(base64Png);
        }
        catch (FormatException)
        {
            throw new ImageRejectedException("base64Png is not valid base64.");
        }
    }

    /// <summary>
    /// Maps an import format name to the WZ pixel format the encoder should use. Null, empty and "auto" return null
    /// (MapleLib picks the format from the image content, as for every other edit).
    /// </summary>
    public static SurfaceFormat? ParseFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;
        switch (format.Trim().ToLowerInvariant())
        {
            case "auto": return null;
            case "bgra8888": case "argb8888": case "8888": return SurfaceFormat.Bgra32;
            case "bgra4444": case "argb4444": case "4444": return SurfaceFormat.Bgra4444;
            case "bgra5551": case "argb1555": case "1555": return SurfaceFormat.Bgra5551;
            case "bgr565": case "rgb565": case "565": return SurfaceFormat.Bgr565;
            case "dxt3": return SurfaceFormat.Dxt3;
            case "dxt5": return SurfaceFormat.Dxt5;
            default:
                throw new ArgumentException(
                    $"Unknown format '{format}'. Use auto, bgra8888, bgra4444, bgra5551, bgr565, dxt3 or dxt5.");
        }
    }

    /// <summary>
    /// The surface format that re-encodes a canvas pixel property in the format it already has, or null when it has
    /// no pixels. BC7 (which MapleLib can decode but not encode) and unknown formats map to lossless BGRA8888.
    /// </summary>
    public static SurfaceFormat? SurfaceFormatOf(WzPngProperty? png)
    {
        if (png == null || png.Width <= 0 || png.Height <= 0)
            return null;
        return png.Format switch
        {
            WzPngFormat.Format1 => SurfaceFormat.Bgra4444,
            WzPngFormat.Format2 => SurfaceFormat.Bgra32,
            WzPngFormat.Format3 => SurfaceFormat.Dxt3,
            WzPngFormat.Format1026 => SurfaceFormat.Dxt3,
            WzPngFormat.Format257 => SurfaceFormat.Bgra5551,
            WzPngFormat.Format513 => SurfaceFormat.Bgr565,
            WzPngFormat.Format517 => SurfaceFormat.Bgr565,
            WzPngFormat.Format2050 => SurfaceFormat.Dxt5,
            _ => SurfaceFormat.Bgra32
        };
    }

    /// <summary>
    /// The format a PNG is stored in when it replaces the image of an existing canvas. An explicit
    /// <paramref name="format"/> wins ("auto" means lossless BGRA8888). Without one the canvas keeps the format it has,
    /// so export, edit and import does not change the format (and the 16 bit formats do not drift); a canvas without
    /// pixels gets BGRA8888. A DXT canvas cannot keep its format for an image whose size is not a multiple of four:
    /// that falls back to BGRA8888 and <paramref name="note"/> says so.
    /// </summary>
    public static SurfaceFormat ResolveReplacementFormat(string? format, WzPngProperty? current, int width, int height, out string? note)
    {
        var r = ResolveReplacement(format, current, width, height);
        note = r.Note;
        return r.Surface;
    }

    /// <summary>The storage chosen for a replacement image: the surface format, the DXT3 sub format to force, and a note for the caller.</summary>
    public readonly record struct ReplacementFormat(SurfaceFormat Surface, bool? Dxt3Grayscale, string? Note);

    /// <summary>
    /// Like <see cref="ResolveReplacementFormat"/>, and exact about the format tag. Without an explicit format:
    /// Format3 stays Format3 and Format1026 stays Format1026 (<see cref="ReplacementFormat.Dxt3Grayscale"/> forces the tag
    /// instead of letting the encoder guess it from the pixels); Format517 is requested as RGB565, which the encoder writes as
    /// Format517 when the image is a multiple of 16 in both directions and every 16x16 block is one colour, otherwise as Format513
    /// (<see cref="DescribeFormatChange"/> reports that); a DXT canvas becomes bgra8888 when WZIMG_PRE_BIG_BANG is on, because
    /// a pre Big Bang client cannot read DXT.
    /// </summary>
    public static ReplacementFormat ResolveReplacement(string? format, WzPngProperty? current, int width, int height)
    {
        if (!string.IsNullOrWhiteSpace(format))
            return new ReplacementFormat(ParseFormat(format) ?? SurfaceFormat.Bgra32, null, null);

        SurfaceFormat? kept = SurfaceFormatOf(current);
        if (kept == null)
            return new ReplacementFormat(SurfaceFormat.Bgra32, null, null);

        bool isDxt = kept == SurfaceFormat.Dxt3 || kept == SurfaceFormat.Dxt5;
        if (isDxt && MapleLib.Helpers.ImageFormatDetector.UsePreBigBangImageFormats)
        {
            return new ReplacementFormat(SurfaceFormat.Bgra32, null,
                $"WZIMG_PRE_BIG_BANG is on and a pre Big Bang client cannot read DXT: the canvas was stored as {current!.Format} (DXT), " +
                "the new image was stored as bgra8888 instead. Pass format=dxt3 or dxt5 to store DXT anyway.");
        }
        if (isDxt && (width % 4 != 0 || height % 4 != 0))
        {
            return new ReplacementFormat(SurfaceFormat.Bgra32, null,
                $"The canvas was stored as {current!.Format} (DXT), which needs a width and height that are multiples of 4; " +
                $"the {width}x{height} image was stored as bgra8888 instead. Pass format to choose another format.");
        }

        bool? dxt3Grayscale = current!.Format switch
        {
            WzPngFormat.Format3 => true,
            WzPngFormat.Format1026 => false,
            _ => null
        };
        return new ReplacementFormat(kept.Value, dxt3Grayscale, null);
    }

    /// <summary>
    /// A note for a replacement without an explicit format that did not end up in the format the canvas had (null when it
    /// did). The known case is Format517, which only stores images that are a multiple of 16 with one colour per 16x16 block.
    /// </summary>
    public static string? DescribeFormatChange(WzPngFormat? before, WzPngFormat after)
    {
        if (before == null || before == after)
            return null;
        if (before == WzPngFormat.Format517)
        {
            return $"The canvas was stored as {before} (one RGB565 colour per 16x16 block), which can only hold an image whose width and height " +
                   $"are multiples of 16 and where every 16x16 block is one colour; the new image was stored as {after} instead.";
        }
        return $"The canvas was stored as {before}; the new image was stored as {after} instead. Pass format to choose a format.";
    }

    #endregion

    #region Canvas -> image file bytes

    /// <summary>True when the canvas (following links) decodes to pixels. Decoding errors are thrown to the caller.</summary>
    public static bool HasDecodableImage(WzCanvasProperty canvas)
    {
#if LINUX_BUILD
        return canvas.GetLinkedPixelsBgra32(out _, out _) != null;
#else
        using Bitmap? bitmap = canvas.GetLinkedWzCanvasBitmap();
        return bitmap != null;
#endif
    }

    /// <summary>
    /// Encodes a canvas (following _inlink / _outlink) as PNG or JPEG at its full size.
    /// Returns null when the canvas has no image data.
    /// </summary>
    public static byte[]? EncodeCanvas(WzCanvasProperty canvas, ExportImageFormat format, out int width, out int height)
    {
#if LINUX_BUILD
        byte[]? bgra = canvas.GetLinkedPixelsBgra32(out width, out height);
        if (bgra == null)
            return null;
        return SkiaEncode(bgra, width, height, format);
#else
        using Bitmap? bitmap = canvas.GetLinkedWzCanvasBitmap();
        if (bitmap == null)
        {
            width = 0;
            height = 0;
            return null;
        }
        width = bitmap.Width;
        height = bitmap.Height;
        using var ms = new MemoryStream();
        bitmap.Save(ms, format == ExportImageFormat.Jpeg ? ImageFormat.Jpeg : ImageFormat.Png);
        return ms.ToArray();
#endif
    }

    /// <summary>
    /// Encodes a canvas as PNG for display. When the larger side exceeds <paramref name="maxSize"/> the image is
    /// scaled down (keeping the aspect ratio) so that it fits; <paramref name="scaled"/> tells whether that happened.
    /// </summary>
    public static byte[]? EncodeCanvasForView(WzCanvasProperty canvas, int maxSize, out int originalWidth, out int originalHeight,
        out int width, out int height, out bool scaled)
    {
        maxSize = Math.Clamp(maxSize, 1, MaxViewDimension);
#if LINUX_BUILD
        byte[]? bgra = canvas.GetLinkedPixelsBgra32(out originalWidth, out originalHeight);
        if (bgra == null)
        {
            width = height = 0;
            scaled = false;
            return null;
        }

        width = originalWidth;
        height = originalHeight;
        scaled = Math.Max(originalWidth, originalHeight) > maxSize;
        if (!scaled)
            return SkiaEncode(bgra, originalWidth, originalHeight, ExportImageFormat.Png);

        double factor = (double)maxSize / Math.Max(originalWidth, originalHeight);
        width = Math.Max(1, (int)Math.Round(originalWidth * factor));
        height = Math.Max(1, (int)Math.Round(originalHeight * factor));
        return SkiaScaleToPng(bgra, originalWidth, originalHeight, width, height);
#else
        using Bitmap? bitmap = canvas.GetLinkedWzCanvasBitmap();
        if (bitmap == null)
        {
            originalWidth = originalHeight = width = height = 0;
            scaled = false;
            return null;
        }

        originalWidth = bitmap.Width;
        originalHeight = bitmap.Height;
        width = originalWidth;
        height = originalHeight;
        scaled = Math.Max(originalWidth, originalHeight) > maxSize;
        using var ms = new MemoryStream();
        if (!scaled)
        {
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        double factor = (double)maxSize / Math.Max(originalWidth, originalHeight);
        width = Math.Max(1, (int)Math.Round(originalWidth * factor));
        height = Math.Max(1, (int)Math.Round(originalHeight * factor));
        using var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(bitmap, 0, 0, width, height);
        }
        resized.Save(ms, ImageFormat.Png);
        return ms.ToArray();
#endif
    }

    #endregion

    #region PNG bytes -> canvas

    /// <summary>
    /// Decodes a PNG and stores it in the canvas pixel property. Refuses non-PNG data and images larger than the
    /// import limits before decoding. <paramref name="format"/> names the WZ pixel format (see <see cref="ParseFormat"/>);
    /// null, empty and "auto" store lossless BGRA8888.
    /// </summary>
    public static void SetCanvasFromPng(WzPngProperty target, byte[] png, string? format, out int width, out int height)
        => SetCanvasFromPng(target, png, ParseFormat(format) ?? SurfaceFormat.Bgra32, out width, out height);

    /// <summary>
    /// Decodes a PNG and stores it in the canvas pixel property in the given WZ pixel format.
    /// <paramref name="dxt3Grayscale"/> (only for <see cref="SurfaceFormat.Dxt3"/>) forces Format3 (true) or Format1026 (false).
    /// </summary>
    public static void SetCanvasFromPng(WzPngProperty target, byte[] png, SurfaceFormat force, out int width, out int height, bool? dxt3Grayscale = null)
    {
        (int declaredWidth, int declaredHeight) = ReadAndCheckPngHeader(png);
#if LINUX_BUILD
        byte[] bgra = SkiaDecodePng(png, declaredWidth, declaredHeight, out width, out height);
        target.SetPixelsBgra32(bgra, width, height, force, dxt3Grayscale);
#else
        using var ms = new MemoryStream(png, writable: false);
        using var bitmap = new Bitmap(ms);
        width = bitmap.Width;
        height = bitmap.Height;
        if (width > MaxImportDimension || height > MaxImportDimension)
            throw new ImageRejectedException($"PNG is too large: {width}x{height} pixels, the limit is {MaxImportDimension}x{MaxImportDimension}.");

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] bgra = new byte[checked(width * height * 4)];
            for (int y = 0; y < height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * width * 4, width * 4);
            target.SetPixelsBgra32(bgra, width, height, force, dxt3Grayscale);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
#endif
    }

    #endregion

#if LINUX_BUILD
    #region SkiaSharp (Linux build)

    private static byte[] SkiaEncode(byte[] bgra, int width, int height, ExportImageFormat format)
    {
        if ((long)width * height * 4 != bgra.Length)
            throw new ArgumentException("The pixel buffer does not match the dimensions.");

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        GCHandle handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), width * 4);
            using var ms = new MemoryStream();
            using (var stream = new SKManagedWStream(ms))
            {
                bool ok = format == ExportImageFormat.Jpeg
                    ? pixmap.Encode(stream, new SKJpegEncoderOptions(90, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.BlendOnBlack))
                    : pixmap.Encode(stream, new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6));
                if (!ok)
                    throw new InvalidOperationException($"Encoding the image as {format} failed.");
            }
            return ms.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    private static byte[] SkiaScaleToPng(byte[] bgra, int width, int height, int newWidth, int newHeight)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var source = SKImage.FromPixelCopy(info, bgra, width * 4);
        using var surface = SKSurface.Create(new SKImageInfo(newWidth, newHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface == null)
            throw new InvalidOperationException("Could not allocate the scaled image.");
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(source, new SKRect(0, 0, newWidth, newHeight),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var scaled = surface.Snapshot();
        using var data = scaled.Encode(SKEncodedImageFormat.Png, 100);
        if (data == null)
            throw new InvalidOperationException("Encoding the scaled image failed.");
        return data.ToArray();
    }

    private static byte[] SkiaDecodePng(byte[] png, int declaredWidth, int declaredHeight, out int width, out int height)
    {
        using var data = SKData.CreateCopy(png);
        using var codec = SKCodec.Create(data);
        if (codec == null)
            throw new ImageRejectedException("The data could not be decoded as a PNG image.");
        if (codec.EncodedFormat != SKEncodedImageFormat.Png)
            throw new ImageRejectedException($"The data is not a PNG image (decoder detected {codec.EncodedFormat}).");

        width = codec.Info.Width;
        height = codec.Info.Height;
        if (width != declaredWidth || height != declaredHeight)
            throw new ImageRejectedException("The PNG header and the decoder disagree about the image size.");
        if (width > MaxImportDimension || height > MaxImportDimension)
            throw new ImageRejectedException($"PNG is too large: {width}x{height} pixels, the limit is {MaxImportDimension}x{MaxImportDimension}.");

        // Straight (unpremultiplied) alpha keeps every channel exact, which is what the WZ formats store.
        var target = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        byte[] bgra = new byte[checked(width * height * 4)];
        GCHandle handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            SKCodecResult result = codec.GetPixels(target, handle.AddrOfPinnedObject());
            if (result != SKCodecResult.Success)
                throw new ImageRejectedException($"The PNG could not be decoded ({result}).");
        }
        finally
        {
            handle.Free();
        }
        return bgra;
    }

    #endregion
#endif
}
