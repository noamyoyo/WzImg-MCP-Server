using MapleLib.WzLib.WzProperties;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>get_sound_data and resolve_sound_link keep the base64 audio data within WZIMG_MAX_RESPONSE_BYTES, like the image tools.</summary>
[Collection("AllowedRootEnv")]
public sealed class AudioResponseCapTests : IClassFixture<TestFixture>, IDisposable
{
    private const string Cat = "Sound";
    private const string Img = "TestSound.img";

    private readonly TestFixture _fixture;
    private readonly AudioTools _audio;
    private readonly string? _previous = Environment.GetEnvironmentVariable(ServerSettings.MaxResponseBytesEnvVar);

    public AudioResponseCapTests(TestFixture fixture)
    {
        _fixture = fixture;
        fixture.InitializeDataSource();
        _audio = new AudioTools(fixture.Session);
        Environment.SetEnvironmentVariable(ServerSettings.MaxResponseBytesEnvVar, null);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(ServerSettings.MaxResponseBytesEnvVar, _previous);

    /// <summary>A PCM WAV file (8 bit, mono, 8 kHz) with <paramref name="dataBytes"/> bytes of sample data.</summary>
    private static byte[] WavFile(int dataBytes)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataBytes); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(8000); w.Write(8000); w.Write((short)1); w.Write((short)8);
        w.Write("data"u8.ToArray()); w.Write(dataBytes);
        var data = new byte[dataBytes];
        new Random(dataBytes).NextBytes(data);
        w.Write(data);
        return ms.ToArray();
    }

    private void AddSound(string name, int dataBytes)
    {
        var file = Path.Combine(Path.GetTempPath(), $"audio-cap-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(file, WavFile(dataBytes));
            var sound = new WzBinaryProperty(name, file);
            Assert.Equal(dataBytes, sound.GetBytes(false).Length);
            var image = _fixture.Session.GetImage(Cat, Img);
            image.AddProperty(sound);
            image.AddProperty(new WzUOLProperty(name + "_uol", name));
        }
        finally { try { File.Delete(file); } catch { } }
    }

    private static void SetCap(long bytes) => Environment.SetEnvironmentVariable(ServerSettings.MaxResponseBytesEnvVar, bytes.ToString());

    [Fact]
    public void SmallSound_StillReturnsItsData_ThroughBothTools()
    {
        AddSound("small", 300);
        var direct = _audio.GetSoundData(Cat, Img, "small");
        MarkdownTestHelper.AssertSuccess(direct);
        Assert.NotNull(MarkdownTestHelper.GetFirstValue(direct, "base64_data"));
        Assert.DoesNotContain("Truncated", direct);

        foreach (var path in new[] { "small", "small_uol" })
        {
            var resolved = _audio.ResolveSoundLink(Cat, Img, path);
            MarkdownTestHelper.AssertSuccess(resolved);
            Assert.NotNull(MarkdownTestHelper.GetFirstValue(resolved, "base64_data"));
            Assert.DoesNotContain("Truncated", resolved);
        }
    }

    [Fact]
    public void SoundAboveTheCap_IsOmittedWithATruncatedNote_ThroughBothTools()
    {
        AddSound("big", 3000);                 // 4000 base64 characters
        SetCap(3999);

        var direct = _audio.GetSoundData(Cat, Img, "big");
        MarkdownTestHelper.AssertSuccess(direct);
        Assert.Null(MarkdownTestHelper.GetFirstValue(direct, "base64_data"));
        Assert.Contains("Truncated", direct);
        Assert.Contains("export_mp3", direct);
        Assert.Contains("3000", direct);       // the data size is still reported
        Assert.True(direct.Length < 2000, $"the response must stay small, was {direct.Length} characters");

        foreach (var path in new[] { "big", "big_uol" })
        {
            var resolved = _audio.ResolveSoundLink(Cat, Img, path);
            MarkdownTestHelper.AssertSuccess(resolved);
            Assert.Null(MarkdownTestHelper.GetFirstValue(resolved, "base64_data"));
            Assert.Contains("Truncated", resolved);
            Assert.True(resolved.Length < 2000);
        }
    }

    [Fact]
    public void SoundExactlyAtTheCap_IsStillReturned()
    {
        AddSound("edge", 3000);                // exactly 4000 base64 characters
        SetCap(4000);
        var r = _audio.GetSoundData(Cat, Img, "edge");
        MarkdownTestHelper.AssertSuccess(r);
        Assert.NotNull(MarkdownTestHelper.GetFirstValue(r, "base64_data"));
        Assert.DoesNotContain("Truncated", r);
    }

    [Fact]
    public void SoundAboveTheDefaultCap_IsOmitted()
    {
        AddSound("huge", 7 * 1024 * 1024);     // about 9.8 million base64 characters, above the 8 MiB default
        var r = _audio.GetSoundData(Cat, Img, "huge");
        MarkdownTestHelper.AssertSuccess(r);
        Assert.Null(MarkdownTestHelper.GetFirstValue(r, "base64_data"));
        Assert.Contains("Truncated", r);
        Assert.True(r.Length < 2000);
    }
}
