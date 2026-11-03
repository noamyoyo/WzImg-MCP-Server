#if LINUX_BUILD
using System.IO.Compression;
using WzImgMCP.Import;
using WzImgMCP.Tools;

namespace WzImgMCP.Tests;

/// <summary>import_fetch kind files: a PNG or a zip of PNGs is unpacked into the inbox with strict checks and never written into the tree.</summary>
[Collection("AllowedRootEnv")]
public sealed class ImportFilesTests : IClassFixture<ImportWzData>
{
    private const string Direct = ImportEnv.DirectLink;
    private static readonly byte[] Png = PngMaker.Png(3, 5);
    private readonly ImportWzData _data;

    public ImportFilesTests(ImportWzData data) { _data = data; }

    private static async Task<ImportStatusResult> FetchZip(ImportEnv env, byte[] zip, string name = "sprites.zip")
    {
        env.Web.ServeFile(Direct, zip, name);
        return await env.FetchAndWait(Direct, "files");
    }

    private static void AssertNothingUnpacked(ImportEnv env, ImportStatusResult status)
    {
        Assert.Equal(ImportStatus.Failed, status.Status);
        Assert.False(Directory.Exists(Path.Combine(env.DirOf(status.Id!), "files")), "a refused archive must leave no unpacked files");
        // nothing anywhere else in the allowed root either
        Assert.Empty(Directory.EnumerateFileSystemEntries(env.Root, "*", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(env.Inbox) && p != env.Inbox));
    }

    [Fact]
    public async Task SinglePng_IsStaged_AndNothingIsWrittenIntoTheTree()
    {
        using var env = new ImportEnv(copyTreeFrom: _data.TreeV1);
        var before = ImportEnv.Snapshot(env.Tree);
        env.Web.ServeFile(Direct, Png, "hero.png", "image/png");

        var status = await env.FetchAndWait(ImportEnv.ViewLink, "files");

        Assert.Equal(ImportStatus.Ready, status.Status);
        var file = Assert.Single(status.Files!);
        Assert.Equal("hero.png", file.Name);
        Assert.Equal(3, file.Width);
        Assert.Equal(5, file.Height);
        Assert.Equal(Png, File.ReadAllBytes(file.Path));
        Assert.Equal(Path.Combine(env.Inbox, status.Id!, "files", "hero.png"), file.Path);
        Assert.Equal(before, ImportEnv.Snapshot(env.Tree));
        Assert.False(File.Exists(env.LockFile));
        Assert.Contains("import_png", status.NextStep);
    }

    [Fact]
    public async Task SinglePng_WithAnUnsafeOrMissingName_GetsAFixedName()
    {
        using var env = new ImportEnv();
        env.Web.ServeFile(Direct, Png, "../../evil.png");
        var status = await env.FetchAndWait(Direct, "files");
        Assert.Equal("image.png", Assert.Single(status.Files!).Name);
        env.Service.Undo(status.Id);
        env.Web.ServeFile(Direct + "&b=1", Png, null);
        Assert.Equal("image.png", Assert.Single((await env.FetchAndWait(Direct + "&b=1", "files")).Files!).Name);
    }

    [Fact]
    public async Task Zip_OfPngs_IsUnpackedFlat()
    {
        using var env = new ImportEnv();
        var zip = PngMaker.Zip(new[] { ("a.png", Png), ("b (2).PNG", PngMaker.Png(4, 4)), ("c-d_e.png", Png) });

        var status = await FetchZip(env, zip);

        Assert.Equal(ImportStatus.Ready, status.Status);
        Assert.Equal(new[] { "a.png", "b (2).PNG", "c-d_e.png" }, status.Files!.Select(f => f.Name).ToArray());
        Assert.All(status.Files!, f => Assert.StartsWith(Path.Combine(env.Inbox, status.Id!, "files") + Path.DirectorySeparatorChar, f.Path));
        Assert.All(status.Files!, f => Assert.Equal(f.Sha256, ImportFs256(f.Path)));
    }

    private static string ImportFs256(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Fact]
    public async Task TheStagedPaths_WorkWithImportPng()
    {
        using var env = new ImportEnv(copyTreeFrom: _data.TreeV1);
        var status = await FetchZip(env, PngMaker.Zip(new[] { ("sprite.png", Png) }));
        var path = status.Files!.Single().Path;

        var result = new ModifyTools(env.Session).ImportPng("Skill", "A.img", "", "newSprite", pngPath: path);

        MarkdownTestHelper.AssertSuccess(result);
        Assert.Equal("3", MarkdownTestHelper.GetFirstValue(result, "width"));
    }

    [Fact]
    public async Task ImportPng_CannotReachTheStagingOfAnotherPlace()
    {
        using var env = new ImportEnv(copyTreeFrom: _data.TreeV1);
        var result = new ModifyTools(env.Session).ImportPng("Skill", "A.img", "", "x", pngPath: Path.Combine(env.Temp, "outside.png"));
        MarkdownTestHelper.AssertFailure(result);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("../evil.png")]
    [InlineData("..\\evil.png")]
    [InlineData("/etc/evil.png")]
    [InlineData("\\evil.png")]
    [InlineData("C:\\evil.png")]
    [InlineData("C:evil.png")]
    [InlineData("sub/a.png")]
    [InlineData("sub\\a.png")]
    [InlineData("sub/")]
    [InlineData("..")]
    [InlineData("a..b.png")]
    [InlineData(".hidden.png")]
    [InlineData("a.txt")]
    [InlineData("a.png.exe")]
    public async Task Zip_WithAnUnsafeName_IsRefusedWhole(string name)
    {
        using var env = new ImportEnv();
        var zip = PngMaker.Zip(new[] { ("good.png", Png), (name, Png) });
        var status = await FetchZip(env, zip);
        AssertNothingUnpacked(env, status);
        Assert.Equal("unsafe_zip", status.JobErrorCode);
    }

    [Fact]
    public async Task Zip_WithADuplicateNameApartFromCapitals_IsRefused()
    {
        using var env = new ImportEnv();
        AssertNothingUnpacked(env, await FetchZip(env, PngMaker.Zip(new[] { ("a.png", Png), ("A.png", Png) })));
    }

    [Fact]
    public async Task Zip_WithASymbolicLinkEntry_IsRefused()
    {
        using var env = new ImportEnv();
        var zip = PngMaker.Zip(new[] { ("link.png", System.Text.Encoding.ASCII.GetBytes("/etc/passwd")) }, e => e.ExternalAttributes = (0xA000 | 0x1FF) << 16);
        var status = await FetchZip(env, zip);
        AssertNothingUnpacked(env, status);
        Assert.Contains("symbolic link", status.JobError);
    }

    [Fact]
    public async Task Zip_WithTooManyFiles_IsRefused()
    {
        using var env = new ImportEnv(o => new ImportOptions { FilesMaxCount = 3 });
        var ok = PngMaker.Zip(Enumerable.Range(0, 3).Select(i => ($"f{i}.png", Png)));
        Assert.Equal(ImportStatus.Ready, (await FetchZip(env, ok)).Status);
        env.Service.Undo(env.Service.GetStatus(Directory.GetDirectories(env.Inbox).Select(Path.GetFileName).Single()) is ImportStatusResult s ? s.Id : null);

        var tooMany = PngMaker.Zip(Enumerable.Range(0, 4).Select(i => ($"f{i}.png", Png)));
        var status = await FetchZip(env, tooMany);
        Assert.Equal(ImportStatus.Failed, status.Status);
        Assert.Equal("unsafe_zip", status.JobErrorCode);
        Assert.Contains("limit is 3", status.JobError);
    }

    [Fact]
    public async Task Zip_ThatUnpacksToTooMuch_IsRefused()
    {
        // 6 images of about 40 KB each (noise does not compress), limit 100 KB in total
        var rnd = new Random(7);
        byte[] Noise()
        {
            var b = new byte[40_000];
            rnd.NextBytes(b);
            return PngWithPayload(b);
        }
        using var env = new ImportEnv(o => new ImportOptions { FilesMaxUnpackedBytes = 100_000 });
        var zip = PngMaker.Zip(Enumerable.Range(0, 6).Select(i => ($"n{i}.png", Noise())), level: CompressionLevel.NoCompression);
        var status = await FetchZip(env, zip);
        AssertNothingUnpacked(env, status);
        Assert.Contains("unpack to more than", status.JobError);
    }

    /// <summary>A real PNG with extra bytes inside an ancillary chunk, so it stays valid and has a chosen size.</summary>
    private static byte[] PngWithPayload(byte[] payload)
    {
        var png = PngMaker.Png();
        // insert a private chunk "prVt" before IEND
        var iend = png.Length - 12;
        using var ms = new MemoryStream();
        ms.Write(png, 0, iend);
        var len = payload.Length;
        ms.Write(new[] { (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len });
        ms.Write("prVt"u8);
        ms.Write(payload);
        ms.Write(new byte[4]);   // the checksum is not checked by the unpacker
        ms.Write(png, iend, 12);
        return ms.ToArray();
    }

    [Fact]
    public async Task Zip_Bomb_IsRefusedByTheCompressionRatio()
    {
        using var env = new ImportEnv();
        // 15 MB of zeros under a PNG signature compresses to a few KB: ratio far above 50
        var bomb = PngMaker.PngLookalikeOfZeros(15 * 1024 * 1024);
        var zip = PngMaker.Zip(new[] { ("bomb.png", bomb) });
        Assert.True(zip.Length < 100_000, "test setup: the bomb must be small");
        var status = await FetchZip(env, zip);
        AssertNothingUnpacked(env, status);
        Assert.Contains("zip bomb", status.JobError);
    }

    [Fact]
    public async Task Zip_ManyEntriesThatTogetherAreTooRatioHeavy_IsRefused()
    {
        using var env = new ImportEnv(o => new ImportOptions { FilesMaxUnpackedBytes = 500L * 1024 * 1024 });
        // each entry is under the per-entry floor (so passes alone) but together the archive is packed more than 50:1
        var piece = PngMaker.PngLookalikeOfZeros(60 * 1024);
        var zip = PngMaker.Zip(Enumerable.Range(0, 150).Select(i => ($"z{i}.png", piece)));
        var status = await FetchZip(env, zip);
        Assert.Equal(ImportStatus.Failed, status.Status);
        Assert.Equal("unsafe_zip", status.JobErrorCode);
    }

    [Fact]
    public async Task Zip_EntryThatLiesAboutItsSize_IsStoppedWhileReading()
    {
        using var env = new ImportEnv();
        var zip = PngMaker.Zip(new[] { ("liar.png", Png) });
        // patch the uncompressed size in the central directory and local header to 3 bytes (the data is longer)
        PatchUncompressedSize(zip, 3);
        var status = await FetchZip(env, zip);
        Assert.Equal(ImportStatus.Failed, status.Status);
        AssertNothingUnpacked(env, status);
    }

    private static void PatchUncompressedSize(byte[] zip, int size)
    {
        for (int i = 0; i < zip.Length - 4; i++)
        {
            // local file header: PK\3\4 ... uncompressed size at offset 22; central header: PK\1\2 ... at offset 24
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x03 && zip[i + 3] == 0x04) BitConverter.GetBytes(size).CopyTo(zip, i + 22);
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x01 && zip[i + 3] == 0x02) BitConverter.GetBytes(size).CopyTo(zip, i + 24);
        }
    }

    [Fact]
    public async Task Zip_WithAnEntryThatIsNotAPng_IsRefused()
    {
        using var env = new ImportEnv();
        var status = await FetchZip(env, PngMaker.Zip(new[] { ("fake.png", System.Text.Encoding.ASCII.GetBytes("MZ this is not an image")) }));
        AssertNothingUnpacked(env, status);
        Assert.Equal("not_a_png", status.JobErrorCode);
    }

    [Fact]
    public async Task Zip_WithATruncatedPng_IsRefused()
    {
        using var env = new ImportEnv();
        var cut = Png.Take(Png.Length - 15).ToArray();
        var status = await FetchZip(env, PngMaker.Zip(new[] { ("cut.png", cut) }));
        Assert.Equal("not_a_png", status.JobErrorCode);
    }

    [Fact]
    public async Task Zip_WithAPngOverTheImportPngDimensionLimit_IsRefused()
    {
        using var env = new ImportEnv();
        var png = (byte[])Png.Clone();
        png[16] = 0; png[17] = 0; png[18] = 0x20; png[19] = 0;   // width 8192 in the IHDR
        var status = await FetchZip(env, PngMaker.Zip(new[] { ("wide.png", png) }));
        Assert.Equal("not_a_png", status.JobErrorCode);
        Assert.Contains("4096", status.JobError);
    }

    [Fact]
    public async Task Zip_Empty_OrDamaged_IsRefused()
    {
        using var env = new ImportEnv();
        var empty = await FetchZip(env, PngMaker.Zip(Array.Empty<(string, byte[])>()));
        Assert.Equal("unsupported_type", empty.JobErrorCode);   // an empty zip has no local header, so it is not even recognised as a zip

        var damaged = PngMaker.Zip(new[] { ("a.png", Png) });
        Array.Clear(damaged, damaged.Length - 22, 22);          // the end-of-directory record
        var status = await FetchZip(env, damaged);
        Assert.Equal("unsafe_zip", status.JobErrorCode);
    }

    [Fact]
    public async Task OtherFileTypes_AreRefused()
    {
        using var env = new ImportEnv();
        env.Web.ServeFile(Direct, System.Text.Encoding.ASCII.GetBytes("ID3\x03\0\0\0\0\0\0 mp3 data"), "song.mp3");
        var status = await env.FetchAndWait(Direct, "files");
        Assert.Equal("unsupported_type", status.JobErrorCode);
        Assert.Contains("not supported yet", status.JobError);
    }

    [Fact]
    public async Task Undo_OfAFilesImport_DeletesItsFiles_AndFreesThePlace()
    {
        using var env = new ImportEnv();
        env.Web.ServeFile(Direct, Png, "a.png");
        var status = await env.FetchAndWait(Direct, "files");
        var dir = env.DirOf(status.Id!);
        Assert.True(Directory.Exists(dir));
        var undo = Assert.IsType<ImportUndoResult>(env.Service.Undo(status.Id));
        Assert.Equal("discarded", undo.Outcome);
        Assert.False(Directory.Exists(dir));
    }
}
#endif
