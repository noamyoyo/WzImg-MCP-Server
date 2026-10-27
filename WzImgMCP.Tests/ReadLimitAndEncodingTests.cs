using WzImgMCP.Imaging;
using WzImgMCP.Server;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>ReadPngInput reads files with a hard byte limit; the tool sources have a single UTF-8 BOM.</summary>
[Collection("AllowedRootEnv")]
public sealed class ReadLimitAndEncodingTests : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
    private readonly string _dir = Directory.CreateTempSubdirectory("readlimit-").FullName;

    public ReadLimitAndEncodingTests() => Environment.SetEnvironmentVariable(PathGuard.EnvVar, _dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, _previousRoot);
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>A stream that never ends and counts what was read from it.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesServed;
        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)7, offset, count);
            BytesServed += count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void ReadAtMost_NeverReadsMoreThanLimitPlusOneByte_FromAnEndlessStream()
    {
        var stream = new EndlessStream();
        var ex = Assert.Throws<ImageRejectedException>(() => CanvasImaging.ReadAtMost(stream, 100_000, "test"));
        Assert.Contains("too large", ex.Message);
        Assert.Equal(100_001, stream.BytesServed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(81920)]
    [InlineData(100_000)]
    public void ReadAtMost_ReturnsAStreamThatFitsExactly(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        Assert.Equal(data, CanvasImaging.ReadAtMost(new MemoryStream(data), length, "test"));
        if (length > 0)
            Assert.Throws<ImageRejectedException>(() => CanvasImaging.ReadAtMost(new MemoryStream(data), length - 1, "test"));
    }

    [Fact]
    public void ReadPngInput_File_AtTheLimitIsRead_OneByteAboveIsRefused()
    {
        var path = Path.Combine(_dir, "limit.png");
        File.WriteAllBytes(path, new byte[CanvasImaging.MaxImportBytes]);
        Assert.Equal(CanvasImaging.MaxImportBytes, CanvasImaging.ReadPngInput(null, path).Length);

        File.WriteAllBytes(path, new byte[CanvasImaging.MaxImportBytes + 1]);
        Assert.Contains("too large", Assert.Throws<ImageRejectedException>(() => CanvasImaging.ReadPngInput(null, path)).Message);
    }

    [Theory]
    [InlineData("AnalysisTools")]
    [InlineData("BatchTools")]
    [InlineData("ExportTools")]
    [InlineData("ImageTools")]
    [InlineData("ModifyTools")]
    public void ToolSources_HaveNoDoubledByteOrderMark(string file)
    {
        // The tests run from <repo>/WzImgMCP.Tests/bin/<config>/<tfm>; walk up to the repository root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WzImgMCP", "Tools", file + ".cs")))
            dir = dir.Parent;
        if (dir == null) return;   // source tree not available (tests run from a copy)

        var head = new byte[6];
        using (var fs = File.OpenRead(Path.Combine(dir.FullName, "WzImgMCP", "Tools", file + ".cs")))
            fs.ReadExactly(head);
        Assert.False(head.AsSpan(0, 3).SequenceEqual(head.AsSpan(3, 3)) && head[0] == 0xEF, "doubled UTF-8 BOM");
    }
}
