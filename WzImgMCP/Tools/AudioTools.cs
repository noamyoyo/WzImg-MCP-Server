using System.ComponentModel;
using ModelContextProtocol.Server;
using WzImgMCP.Core;
using WzImgMCP.Server;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace WzImgMCP.Tools;

/// <summary>
/// MCP tools for audio/sound operations
/// </summary>
[McpServerToolType]
public class AudioTools
{
    private readonly WzSessionManager _session;

    public AudioTools(WzSessionManager session)
    {
        _session = session;
    }

    [McpServerTool(Name = "get_sound_info"), Description("Get sound/audio metadata")]
    public string GetSoundInfo(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Property path to the sound")] string path)
    {
        if (!_session.IsInitialized)
        {
            return new SoundInfoResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var img = _session.GetImage(category, image);
            var prop = img.GetFromPath(path) as WzBinaryProperty;

            if (prop == null)
            {
                return new SoundInfoResult { Success = false, Error = $"Sound property not found: {path}" };
            }

            return new SoundInfoResult
            {
                Success = true,
                Length = prop.Length,
                Frequency = prop.Frequency,
                HeaderType = prop.Header?.Length > 0 ? "MP3" : "Unknown",
                DataSize = prop.GetBytes(false)?.Length ?? 0
            };
        }
        catch (Exception ex)
        {
            return new SoundInfoResult { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "get_sound_data"), Description("Get raw audio data as base64 (omitted with a note when the base64 text would exceed the response limit, 8 MiB by default; use export_mp3 then)")]
    public string GetSoundData(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Property path to the sound")] string path)
    {
        if (!_session.IsInitialized)
        {
            return new SoundDataResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var img = _session.GetImage(category, image);
            var prop = img.GetFromPath(path) as WzBinaryProperty;

            if (prop == null)
            {
                return new SoundDataResult { Success = false, Error = $"Sound property not found: {path}" };
            }

            var data = prop.GetBytes(false);
            if (data == null || data.Length == 0)
            {
                return new SoundDataResult { Success = false, Error = "Failed to extract sound data" };
            }

            var base64 = EncodeCapped(data, out var note);
            return new SoundDataResult
            {
                Success = true,
                Format = "mp3",
                Length = prop.Length,
                Frequency = prop.Frequency,
                DataSize = data.Length,
                Base64Data = base64,
                Note = note
            };
        }
        catch (Exception ex)
        {
            return new SoundDataResult { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "list_sounds_in_image"), Description("List all sound properties in an image")]
    public string ListSoundsInImage(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Maximum depth to search")] int maxDepth = 10)
    {
        if (!_session.IsInitialized)
        {
            return new SoundListResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var img = _session.GetImage(category, image);
            var sounds = new List<SoundEntry>();

            FindSounds(img, "", sounds, 0, maxDepth);

            return new SoundListResult
            {
                Success = true,
                Category = category,
                Image = image,
                Count = sounds.Count,
                Sounds = sounds
            };
        }
        catch (Exception ex)
        {
            return new SoundListResult { Success = false, Error = ex.Message };
        }
    }

    [McpServerTool(Name = "resolve_sound_link"), Description("Resolve UOL link to actual sound property (the audio data is omitted with a note when its base64 text would exceed the response limit, 8 MiB by default)")]
    public string ResolveSoundLink(
        [Description("Category name")] string category,
        [Description("Image name")] string image,
        [Description("Property path to the UOL or sound")] string path)
    {
        if (!_session.IsInitialized)
        {
            return new ResolveSoundResult { Success = false, Error = "No data source initialized" };
        }

        try
        {
            var img = _session.GetImage(category, image);
            var prop = img.GetFromPath(path);

            if (prop == null)
            {
                return new ResolveSoundResult { Success = false, Error = $"Property not found: {path}" };
            }

            // If it's a UOL, resolve it
            if (prop is WzUOLProperty uol)
            {
                var target = uol.LinkValue as WzBinaryProperty;
                if (target == null)
                {
                    return new ResolveSoundResult
                    {
                        Success = false,
                        Error = $"UOL target is not a sound: {uol.Value}",
                        UolPath = uol.Value
                    };
                }

                var data = target.GetBytes(false);
                var base64 = EncodeCapped(data, out var note);
                return new ResolveSoundResult
                {
                    Success = true,
                    WasUol = true,
                    UolPath = uol.Value,
                    TargetPath = target.FullPath,
                    Length = target.Length,
                    Frequency = target.Frequency,
                    DataSize = data?.Length ?? 0,
                    Base64Data = base64,
                    Note = note
                };
            }

            // If it's already a sound
            if (prop is WzBinaryProperty sound)
            {
                var data = sound.GetBytes(false);
                var base64 = EncodeCapped(data, out var note);
                return new ResolveSoundResult
                {
                    Success = true,
                    WasUol = false,
                    Length = sound.Length,
                    Frequency = sound.Frequency,
                    DataSize = data?.Length ?? 0,
                    Base64Data = base64,
                    Note = note
                };
            }

            return new ResolveSoundResult
            {
                Success = false,
                Error = $"Property is not a sound or UOL: {prop.PropertyType}"
            };
        }
        catch (Exception ex)
        {
            return new ResolveSoundResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Base64 text of the sound data, or null with a "Truncated" note when the text would exceed WZIMG_MAX_RESPONSE_BYTES
    /// (the same limit and wording style as the image tools). Null without a note when there is no data.
    /// </summary>
    internal static string? EncodeCapped(byte[]? data, out string? note)
    {
        note = null;
        if (data == null)
            return null;
        long chars = 4L * ((data.Length + 2L) / 3);
        long limit = Utils.ServerSettings.MaxResponseBytes;
        if (chars > limit)
        {
            note = $"Sound data omitted: the base64 text would be {chars:N0} characters, above the limit of {limit:N0} " +
                   "(WZIMG_MAX_RESPONSE_BYTES). Truncated: use export_mp3 to write the sound to a file.";
            return null;
        }
        return Convert.ToBase64String(data);
    }

    private void FindSounds(WzObject obj, string path, List<SoundEntry> results, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        IEnumerable<WzImageProperty>? children = null;

        if (obj is WzImage img)
        {
            children = img.WzProperties;
        }
        else if (obj is WzImageProperty prop)
        {
            if (prop is WzBinaryProperty sound)
            {
                results.Add(new SoundEntry
                {
                    Path = path,
                    Length = sound.Length,
                    Frequency = sound.Frequency
                });
            }

            children = prop.WzProperties;
        }

        if (children != null)
        {
            foreach (var child in children)
            {
                var childPath = string.IsNullOrEmpty(path) ? child.Name : $"{path}/{child.Name}";
                FindSounds(child, childPath, results, depth + 1, maxDepth);
            }
        }
    }
}

// Result types

public class SoundInfoResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int Length { get; set; }
    public int Frequency { get; set; }
    public string? HeaderType { get; set; }
    public int DataSize { get; set; }
}

public class SoundDataResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Format { get; set; }
    public int Length { get; set; }
    public int Frequency { get; set; }
    public int DataSize { get; set; }
    public string? Base64Data { get; set; }
    public string? Note { get; set; }
}

public class SoundListResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Category { get; set; }
    public string? Image { get; set; }
    public int Count { get; set; }
    public List<SoundEntry>? Sounds { get; set; }
}

public class SoundEntry
{
    public required string Path { get; set; }
    public int Length { get; set; }
    public int Frequency { get; set; }
}

public class ResolveSoundResult : MarkdownResultBase
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public bool WasUol { get; set; }
    public string? UolPath { get; set; }
    public string? TargetPath { get; set; }
    public int Length { get; set; }
    public int Frequency { get; set; }
    public int DataSize { get; set; }
    public string? Base64Data { get; set; }
    public string? Note { get; set; }
}



