using MapleLib.WzLib;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Server;

namespace WzImgMCP.Tests;

/// <summary>
/// An IMG tree with one category of many small images (every fifth in the sub directory "Sub"), written with MapleLib like
/// <see cref="TestFixture"/> does. Image i has the string info/name = "needle-i" and a UOL that points nowhere (i = 0 only).
/// </summary>
public sealed class ScanTestTree : IDisposable
{
    public const string Category = "Mob";

    /// <summary>The tree. It is the only child of a folder of its own (see <see cref="_sandbox"/>).</summary>
    public string Path { get; }

    private readonly string _sandbox = Directory.CreateTempSubdirectory("wzimg-scan-").FullName;
    public int ImageCount { get; }

    /// <summary>Relative paths of the images as the category listing returns them (with "/"), ordinal order.</summary>
    public List<string> RelativePaths { get; } = new();

    public ScanTestTree(int imageCount = 30, int propertiesPerImage = 4)
    {
        ImageCount = imageCount;
        Path = System.IO.Path.Combine(_sandbox, "tree");
        Directory.CreateDirectory(Path);
        File.WriteAllText(System.IO.Path.Combine(Path, "manifest.json"), "{\"version\":\"1\",\"displayName\":\"Scan test\"}");
        string root = System.IO.Path.Combine(Path, Category);
        Directory.CreateDirectory(System.IO.Path.Combine(root, "Sub"));

        for (int i = 0; i < imageCount; i++)
        {
            bool nested = i % 5 == 4;
            string relative = nested ? $"Sub/s{i:000}.img" : $"m{i:000}.img";
            RelativePaths.Add(relative);
            WriteImage(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)), i, propertiesPerImage);
        }
        RelativePaths.Sort(StringComparer.Ordinal);
    }

    private static void WriteImage(string file, int index, int propertiesPerImage)
    {
        var img = new WzImage(System.IO.Path.GetFileName(file)) { Changed = true };
        var info = new WzSubProperty("info");
        info.AddProperty(new WzStringProperty("name", $"needle-{index}"));
        info.AddProperty(new WzIntProperty("id", index));
        img.AddProperty(info);
        for (int p = 0; p < propertiesPerImage; p++)
        {
            var sub = new WzSubProperty($"p{p}");
            sub.AddProperty(new WzIntProperty("v", p));
            sub.AddProperty(new WzStringProperty("s", $"value-{index}-{p}"));
            sub.AddProperty(new WzVectorProperty("pos", new WzIntProperty("X", p), new WzIntProperty("Y", index)));
            img.AddProperty(sub);
        }
        if (index == 0)
            img.AddProperty(new WzUOLProperty("broken", "no/such/target"));

        using var fs = File.Create(file);
        using var writer = new WzBinaryWriter(fs, WzTool.GetIvByMapleVersion(WzMapleVersion.BMS));
        img.SaveImage(writer, true);
    }

    public long FileSize(string relative)
        => new FileInfo(System.IO.Path.Combine(Path, Category, relative.Replace('/', System.IO.Path.DirectorySeparatorChar))).Length;

    public WzSessionManager NewSession(ScanLimits? limits = null)
    {
        var session = new WzSessionManager();
        session.InitDataSource(Path);
        if (limits != null)
            session.ScanLimits = limits;
        return session;
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, true); } catch { }
    }
}
