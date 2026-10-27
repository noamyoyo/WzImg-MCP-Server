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
            return File.ReadAllBytes(resolved);
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
    /// import limits before decoding. <paramref name="format"/> forces a WZ pixel format (see <see cref="ParseFormat"/>).
    /// </summary>
    public static void SetCanvasFromPng(WzPngProperty target, byte[] png, string? format, out int width, out int height)
    {
        (int declaredWidth, int declaredHeight) = ReadAndCheckPngHeader(png);
        SurfaceFormat? force = ParseFormat(format);
#if LINUX_BUILD
        byte[] bgra = SkiaDecodePng(png, declaredWidth, declaredHeight, out width, out height);
        target.SetPixelsBgra32(bgra, width, height, force);
#else
        using var ms = new MemoryStream(png, writable: false);
        using var bitmap = new Bitmap(ms);
        width = bitmap.Width;
        height = bitmap.Height;
        if (width > MaxImportDimension || height > MaxImportDimension)
            throw new ImageRejectedException($"PNG is too large: {width}x{height} pixels, the limit is {MaxImportDimension}x{MaxImportDimension}.");
        if (force == null)
        {
            target.PNG = bitmap; // unchanged Windows behaviour
            return;
        }

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] bgra = new byte[checked(width * height * 4)];
            for (int y = 0; y < height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * width * 4, width * 4);
            target.SetPixelsBgra32(bgra, width, height, force);
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
