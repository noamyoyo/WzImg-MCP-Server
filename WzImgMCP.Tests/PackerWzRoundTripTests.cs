#if LINUX_BUILD
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Packer;

namespace WzImgMCP.Tests;

/// <summary>
/// The first task of the v2 packer: open a .wz and save it with no change, and prove the whole file and every image's raw stored
/// block are identical; then prove that one targeted change leaves every other block as it was, and guard the data-loss trap
/// (marking an image that was never parsed as changed empties it). Synthetic files only; the owner's server repeats this on real data.
/// </summary>
public sealed class PackerWzRoundTripTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wzrt-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Published() => SyntheticWz.Build(Path.Combine(_dir, "String.wz"));

    [Fact]
    public void OpenAndSaveWithNoChange_GivesAByteIdenticalFile_AndEveryRawBlockIsEqual()
    {
        var source = Published();
        var copy = Path.Combine(_dir, "copy", "String.wz");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);

        using (var wz = SyntheticWz.Open(source))
            wz.SaveToDisk(copy);   // a different path; the source stays open while it is saved

        Assert.Equal(SyntheticWz.Sha256(source), SyntheticWz.Sha256(copy));
        Assert.Equal(new FileInfo(source).Length, new FileInfo(copy).Length);

        var before = SyntheticWz.Blocks(source);
        var after = SyntheticWz.Blocks(copy);
        Assert.Equal(SyntheticWz.DefaultImages.Length, before.Count);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (path, block) in before)
        {
            Assert.Equal(block.Sha256, after[path].Sha256);
            Assert.Equal(block.Size, after[path].Size);
            Assert.Equal(block.Checksum, after[path].Checksum);
        }
        Assert.Equal(SyntheticWz.StoredOrder(source), SyntheticWz.StoredOrder(copy));
    }

    [Fact]
    public void TheNoChangeSave_DoesNotParseOrTouchAnImage()
    {
        var source = Published();
        using var wz = SyntheticWz.Open(source);
        var images = wz.WzDirectory.WzImages.Concat(wz.WzDirectories().SelectMany(d => d.WzImages)).ToList();
        Assert.NotEmpty(images);
        Assert.All(images, i => { Assert.False(i.Parsed); Assert.False(i.Changed); });
    }

    [Fact]
    public void ReplacingOneImageInPlace_KeepsEveryOtherBlockAndTheOrder()
    {
        var source = Published();
        var output = Path.Combine(_dir, "out.wz");

        using (var wz = SyntheticWz.Open(source))
        {
            var target = wz.WzDirectory.GetDirectoryByName("Sub")!.GetImageByName("Deep.img")!;
            Assert.True(target.ParseImage());                 // parse BEFORE marking it changed
            target.ClearProperties();
            var replacement = new WzImage("Deep.img");
            SyntheticWz.Content("REPLACED")(replacement);
            foreach (var property in replacement.WzProperties.ToList())
                target.WzProperties.Add(property.DeepClone());
            target.Changed = true;
            wz.SaveToDisk(output);
        }

        var before = SyntheticWz.Blocks(source);
        var after = SyntheticWz.Blocks(output);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (path, block) in before)
        {
            if (path == "Sub/Deep.img") Assert.NotEqual(block.Sha256, after[path].Sha256);
            else Assert.Equal(block.Sha256, after[path].Sha256);
        }
        Assert.Equal(SyntheticWz.StoredOrder(source), SyntheticWz.StoredOrder(output));

        using var reopened = SyntheticWz.Open(output);
        var deep = reopened.WzDirectory.GetDirectoryByName("Sub")!.GetImageByName("Deep.img")!;
        Assert.True(deep.ParseImage());
        Assert.Equal("REPLACED", ((WzStringProperty)deep["who"]!).Value);
    }

    [Fact]
    public void TheTrap_MarkingAnUnparsedImageChanged_EmptiesIt()
    {
        // The reason the packer parses an image before it marks it changed. If this ever stops being true the rule can be relaxed,
        // so the test documents the behaviour of the library rather than hiding it.
        var source = Published();
        var output = Path.Combine(_dir, "trap.wz");
        using (var wz = SyntheticWz.Open(source))
        {
            var victim = wz.WzDirectory.GetImageByName("Pet.img")!;
            Assert.False(victim.Parsed);
            victim.Changed = true;        // the mistake: no ParseImage first
            wz.SaveToDisk(output);
        }

        using var reopened = SyntheticWz.Open(output);
        var pet = reopened.WzDirectory.GetImageByName("Pet.img")!;
        pet.ParseImage();
        Assert.Empty(pet.WzProperties);   // the image lost everything it held
    }

    [Fact]
    public void TheGuard_ParsingFirstKeepsTheImage()
    {
        var source = Published();
        var output = Path.Combine(_dir, "guard.wz");
        using (var wz = SyntheticWz.Open(source))
        {
            var image = wz.WzDirectory.GetImageByName("Pet.img")!;
            Assert.True(image.ParseImage());
            image.Changed = true;
            wz.SaveToDisk(output);
        }

        using var reopened = SyntheticWz.Open(output);
        var pet = reopened.WzDirectory.GetImageByName("Pet.img")!;
        Assert.True(pet.ParseImage());
        Assert.Equal("Pet.img", ((WzStringProperty)pet["who"]!).Value);
        Assert.Equal(6, pet.WzProperties.Count);   // who, n, ratio, info and the two canvases
    }
}

internal static class WzFileExtensions
{
    /// <summary>Every directory below the root, depth first.</summary>
    public static IEnumerable<WzDirectory> WzDirectories(this WzFile wz)
    {
        var pending = new Stack<WzDirectory>(wz.WzDirectory.WzDirectories);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            yield return dir;
            foreach (var sub in dir.WzDirectories) pending.Push(sub);
        }
    }
}
#endif
