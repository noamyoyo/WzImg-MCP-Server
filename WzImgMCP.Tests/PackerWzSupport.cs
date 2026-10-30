#if LINUX_BUILD
using System.Security.Cryptography;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Packer;

namespace WzImgMCP.Tests;

/// <summary>Builds small, deterministic .wz files (version 83, key GMS) with the library, and reads them back as raw blocks. Synthetic data only.</summary>
internal static class SyntheticWz
{
    /// <summary>The images of the default published String.wz, as paths below the .wz root.</summary>
    public static readonly string[] DefaultImages =
    {
        "Pet.img", "Eqp.img", "Cash.img", "Sub/Deep.img", "Sub/Other.img", "Sub/Deeper/Last.img", "Z/Only.img",
    };

    /// <summary>The content of one published image: strings, numbers, a sub property, a vector and two canvases whose size depends on the name.</summary>
    public static Action<WzImage> Content(string marker, int shade = 90)
        => img =>
        {
            img.AddProperty(new WzStringProperty("who", marker));
            img.AddProperty(new WzIntProperty("n", marker.Length));
            img.AddProperty(new WzDoubleProperty("ratio", 1.5));
            var info = new WzSubProperty("info");
            info.AddProperty(new WzStringProperty("desc", "synthetic " + marker));
            info.AddProperty(new WzVectorProperty("origin", new WzIntProperty("X", 3), new WzIntProperty("Y", 4)));
            img.AddProperty(info);
            int side = 8 + (Math.Abs(marker.GetHashCode()) % 5) * 8;
            for (int i = 0; i < 2; i++)
                img.AddProperty(ImgFixture.PixelCanvas(i.ToString(), side, side, (x, y) => ((byte)((x * 7 + y * 3 + shade + i) & 0xFF), (byte)(y * 5 & 0xFF), (byte)(x * 11 & 0xFF), 255)));
        };

    /// <summary>Writes a version 83 GMS .wz holding <paramref name="images"/> (path below the root, in this order) to <paramref name="path"/>.</summary>
    public static string Build(string path, IEnumerable<string>? images = null, Func<string, Action<WzImage>>? content = null)
    {
        using var wz = new WzFile(83, WzMapleVersion.GMS) { Name = Path.GetFileName(path) };
        foreach (var rel in images ?? DefaultImages)
        {
            var parts = rel.Split('/');
            var dir = wz.WzDirectory;
            foreach (var part in parts[..^1])
            {
                var next = dir.GetDirectoryByName(part);
                if (next == null)
                {
                    next = new WzDirectory(part, wz);
                    dir.AddDirectory(next);
                }
                dir = next;
            }
            var image = new WzImage(parts[^1]) { Changed = true };
            (content?.Invoke(rel) ?? Content(rel))(image);
            dir.AddImage(image);
        }
        wz.SaveToDisk(path);
        return path;
    }

    /// <summary>Opens a .wz with version 83 and key GMS fixed, as the packer does. Throws when it does not open.</summary>
    public static WzFile Open(string path)
    {
        var wz = new WzFile(path, 83, WzMapleVersion.GMS);
        Assert.Equal(WzFileParseStatus.Success, wz.ParseWzFile());
        return wz;
    }

    /// <summary>Every image of the file by path with the sha256 of its raw stored block.</summary>
    public static Dictionary<string, PackerBlock> Blocks(string path)
    {
        using var wz = Open(path);
        return PackerWzBlocks.Index(wz, path, CancellationToken.None);
    }

    /// <summary>The paths of the images in the order they are stored (directory by directory, as the file lists them).</summary>
    public static List<string> StoredOrder(string path)
    {
        using var wz = Open(path);
        var order = new List<string>();
        void Walk(WzDirectory dir, string prefix)
        {
            foreach (var image in dir.WzImages) order.Add(prefix + image.Name);
            foreach (var sub in dir.WzDirectories) Walk(sub, prefix + sub.Name + "/");
        }
        Walk(wz.WzDirectory, "");
        return order;
    }

    public static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>A zero-IV (BMS) .img with <paramref name="fill"/> in it: the form of an extracted file.</summary>
    public static byte[] ImgBytes(Action<WzImage> fill, WzMapleVersion key = WzMapleVersion.BMS) => ImgFixture.Build(fill, key);
}
#endif
