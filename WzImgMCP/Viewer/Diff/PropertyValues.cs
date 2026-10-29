using System.Globalization;
using System.Security.Cryptography;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace WzImgMCP.Viewer.Diff;

/// <summary>
/// What a property is called, how it is shown and when two are equal. Type-strict: a short that became an int is a
/// change. Floats are equal when their bits are equal (so -0 differs from +0) or both are NaN. A UOL is its link text,
/// never resolved. Data behind the shared reader (sound bytes, raw data) is read under the image's
/// <see cref="LoadedImage.Sync"/>.
/// </summary>
public static class PropertyValues
{
    public static string TypeOf(WzImageProperty p) => p switch
    {
        WzSubProperty => "sub",
        WzCanvasProperty => "canvas",
        WzStringProperty => "string",
        WzIntProperty => "int",
        WzShortProperty => "short",
        WzLongProperty => "long",
        WzFloatProperty => "float",
        WzDoubleProperty => "double",
        WzVectorProperty => "vector",
        WzUOLProperty => "uol",
        WzConvexProperty => "convex",
        WzBinaryProperty => "sound",
        WzNullProperty => "null",
        WzLuaProperty => "lua",
        WzRawDataProperty => "raw",
        WzVideoProperty => "video",
        _ => p.GetType().Name.Replace("Wz", "").Replace("Property", "").ToLowerInvariant(),
    };

    /// <summary>The children to compare one by one: those of a sub property and of a canvas. A vector and a convex are values.</summary>
    public static IReadOnlyList<WzImageProperty> Children(WzImageProperty p)
    {
        if (p is WzVectorProperty or WzConvexProperty or WzBinaryProperty)
            return Array.Empty<WzImageProperty>();
        return (IReadOnlyList<WzImageProperty>?)p.WzProperties ?? Array.Empty<WzImageProperty>();
    }

    /// <summary>The value as a short invariant string (null for a type without one), cut at 200 characters.</summary>
    public static (string? Text, bool Truncated) Describe(WzImageProperty p, LoadedImage owner)
    {
        string? text = p switch
        {
            WzStringProperty s => s.Value ?? "",
            WzIntProperty i => i.Value.ToString(CultureInfo.InvariantCulture),
            WzShortProperty sh => sh.Value.ToString(CultureInfo.InvariantCulture),
            WzLongProperty l => l.Value.ToString(CultureInfo.InvariantCulture),
            WzFloatProperty f => f.Value.ToString("R", CultureInfo.InvariantCulture),
            WzDoubleProperty d => d.Value.ToString("R", CultureInfo.InvariantCulture),
            WzVectorProperty v => $"({v.X?.Value ?? 0}, {v.Y?.Value ?? 0})",
            WzUOLProperty u => u.Value ?? "",
            WzConvexProperty c => "[" + string.Join(" ", Points(c).Select(pt => $"({pt.X}, {pt.Y})")) + "]",
            WzBinaryProperty snd => SoundText(snd, owner),
            WzCanvasProperty cv => CanvasText(cv),
            WzLuaProperty lua => "lua " + (lua.Value?.Length ?? 0) + " bytes sha256:" + Short(Hash(lua.Value)),
            WzRawDataProperty raw => RawText(raw, owner),
            WzNullProperty => null,
            _ => null,
        };
        return Cut(text);
    }

    public static (string? Text, bool Truncated) Cut(string? text)
    {
        if (text == null || text.Length <= DiffOptions.MaxValueChars)
            return (text, false);
        int length = DiffOptions.MaxValueChars;
        if (char.IsHighSurrogate(text[length - 1])) length--;   // never split a surrogate pair
        return (text.Substring(0, length), true);
    }

    /// <summary>Equality of two properties of the SAME type tag (canvas pixels are decided elsewhere).</summary>
    public static bool ValueEquals(WzImageProperty a, WzImageProperty b, LoadedImage ownerA, LoadedImage ownerB)
    {
        switch (a)
        {
            case WzStringProperty x: return string.Equals(x.Value, ((WzStringProperty)b).Value, StringComparison.Ordinal);
            case WzIntProperty x: return x.Value == ((WzIntProperty)b).Value;
            case WzShortProperty x: return x.Value == ((WzShortProperty)b).Value;
            case WzLongProperty x: return x.Value == ((WzLongProperty)b).Value;
            case WzFloatProperty x: return FloatEquals(x.Value, ((WzFloatProperty)b).Value);
            case WzDoubleProperty x: return DoubleEquals(x.Value, ((WzDoubleProperty)b).Value);
            case WzVectorProperty x:
                var y = (WzVectorProperty)b;
                return (x.X?.Value ?? 0) == (y.X?.Value ?? 0) && (x.Y?.Value ?? 0) == (y.Y?.Value ?? 0);
            case WzUOLProperty x: return string.Equals(x.Value, ((WzUOLProperty)b).Value, StringComparison.Ordinal);
            case WzConvexProperty x: return Points(x).SequenceEqual(Points((WzConvexProperty)b));
            case WzBinaryProperty x:
                var s = (WzBinaryProperty)b;
                return x.Length == s.Length && x.SoundDataLength == s.SoundDataLength && SoundHash(x, ownerA) == SoundHash(s, ownerB);
            case WzLuaProperty x: return (x.Value ?? Array.Empty<byte>()).AsSpan().SequenceEqual(((WzLuaProperty)b).Value ?? Array.Empty<byte>());
            case WzRawDataProperty x: return RawHash(x, ownerA) == RawHash((WzRawDataProperty)b, ownerB);
            default: return true;   // sub, canvas (decided by the canvas stages), null, video
        }
    }

    public static bool FloatEquals(float a, float b)
        => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b) || (float.IsNaN(a) && float.IsNaN(b));

    public static bool DoubleEquals(double a, double b)
        => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b) || (double.IsNaN(a) && double.IsNaN(b));

    private static IEnumerable<(int X, int Y)> Points(WzConvexProperty c)
        => (c.WzProperties ?? new WzPropertyCollection(null!)).OfType<WzVectorProperty>().Select(v => (v.X?.Value ?? 0, v.Y?.Value ?? 0));

    private static string CanvasText(WzCanvasProperty c)
    {
        var png = c.PngProperty;
        return png == null ? "canvas (no image)" : $"canvas {png.Width}x{png.Height} format {(int)png.Format}";
    }

    private static string SoundText(WzBinaryProperty s, LoadedImage owner)
        => $"sound {s.Length} ms, {s.SoundDataLength} bytes, sha256:{Short(SoundHash(s, owner))}";

    private static string RawText(WzRawDataProperty r, LoadedImage owner)
    {
        byte[]? bytes;
        lock (owner.Sync) bytes = r.GetBytes(false);
        return $"raw {bytes?.Length ?? 0} bytes sha256:{Short(Hash(bytes))}";
    }

    private static string SoundHash(WzBinaryProperty s, LoadedImage owner)
    {
        byte[]? bytes;
        lock (owner.Sync) bytes = s.GetBytes(false);
        return Hash(bytes);
    }

    private static string RawHash(WzRawDataProperty r, LoadedImage owner)
    {
        byte[]? bytes;
        lock (owner.Sync) bytes = r.GetBytes(false);
        return Hash(bytes);
    }

    public static string Hash(byte[]? bytes) => bytes == null ? "" : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Short(string hash) => hash.Length > 12 ? hash.Substring(0, 12) : hash;
}
