using System.IO.Compression;
using System.Text.RegularExpressions;
using WzImgMCP.Imaging;

namespace WzImgMCP.Import;

/// <summary>
/// Turns a downloaded file of kind "files" (one PNG, or a flat zip of PNGs) into plain PNG files in a folder of the import.
/// Nothing here is ever written into the tree. Checks: name rules (no path separators, no "..", no absolute paths, no links),
/// file count, total unpacked size, compression ratio (zip bomb guard), and PNG only (signature, structure, the same size and
/// dimension limits as import_png).
/// </summary>
internal static class ImportFilesUnpacker
{
    private static readonly Regex NameRule = new(@"^[A-Za-z0-9][A-Za-z0-9 _().+\-]{0,95}\.[Pp][Nn][Gg]$", RegexOptions.Compiled);
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] ZipSignature = { 0x50, 0x4B, 0x03, 0x04 };
    private const long RatioFloorBytes = 64 * 1024;   // smaller entries cannot hurt, and tiny files compress oddly

    public static bool LooksLikeZip(byte[] head) => head.Length >= 4 && head.AsSpan(0, 4).SequenceEqual(ZipSignature);
    public static bool LooksLikePng(byte[] head) => head.Length >= 8 && head.AsSpan(0, 8).SequenceEqual(PngSignature);

    public static bool IsAllowedName(string? name) => name != null && NameRule.IsMatch(name) && !name.Contains("..");

    /// <summary>Unpacks the downloaded file into <paramref name="targetDirectory"/> (which must exist and be empty).</summary>
    public static List<StagedFile> Unpack(string downloaded, string? suggestedName, string targetDirectory, ImportOptions options)
    {
        var head = new byte[8];
        using (var stream = new FileStream(downloaded, FileMode.Open, FileAccess.Read, FileShare.Read))
            _ = stream.Read(head, 0, head.Length);

        if (LooksLikePng(head))
        {
            var name = IsAllowedName(suggestedName) ? suggestedName! : "image.png";
            var bytes = ReadLimited(File.OpenRead(downloaded), CanvasImaging.MaxImportBytes, name);
            return new List<StagedFile> { WriteChecked(targetDirectory, name, bytes) };
        }

        if (LooksLikeZip(head))
            return UnpackZip(downloaded, targetDirectory, options);

        throw new ImportException("unsupported_type", "The file is neither a PNG image nor a zip of PNG images. Other file types (sound files) are not supported yet.");
    }

    private static List<StagedFile> UnpackZip(string zipPath, string targetDirectory, ImportOptions options)
    {
        var archiveSize = new FileInfo(zipPath).Length;
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            throw new ImportException("unsafe_zip", "The zip file is damaged and could not be read.");
        }

        using (archive)
        {
            if (archive.Entries.Count == 0)
                throw new ImportException("unsafe_zip", "The zip file is empty.");
            if (archive.Entries.Count > options.FilesMaxCount)
                throw new ImportException("unsafe_zip", $"The zip file holds {archive.Entries.Count} entries; the limit is {options.FilesMaxCount}.");

            // Pass 1: look only at the declared values. Nothing is read or written yet.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long declaredTotal = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (name.Length == 0 || name.StartsWith('/') || name.StartsWith('\\') || (name.Length > 1 && name[1] == ':'))
                    throw new ImportException("unsafe_zip", "The zip file holds an entry with an absolute path; it was refused.");
                if (name.Contains('/') || name.Contains('\\'))
                    throw new ImportException("unsafe_zip", "The zip file holds an entry in a folder or with a path separator in its name; only a flat zip of PNG files is accepted.");
                if (name.Contains(".."))
                    throw new ImportException("unsafe_zip", "The zip file holds an entry with \"..\" in its name; it was refused.");
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new ImportException("unsafe_zip", "The zip file holds a symbolic link; it was refused.");
                if (!IsAllowedName(name))
                    throw new ImportException("unsafe_zip", "The zip file holds an entry that is not a plainly named PNG file (letters, digits, space, _ - . ( ) + and ending in .png).");
                if (!seen.Add(name))
                    throw new ImportException("unsafe_zip", "The zip file holds two entries whose names differ only in capitals.");
                if (entry.Length > CanvasImaging.MaxImportBytes)
                    throw new ImportException("unsafe_zip", $"A PNG in the zip is larger than {CanvasImaging.MaxImportBytes / (1024 * 1024)} MB.");
                if (entry.Length > RatioFloorBytes && entry.Length / (double)Math.Max(1, entry.CompressedLength) > options.FilesMaxRatio)
                    throw new ImportException("unsafe_zip", $"A file in the zip is packed more than {options.FilesMaxRatio} to 1; this looks like a zip bomb and was refused.");
                declaredTotal += entry.Length;
                if (declaredTotal > options.FilesMaxUnpackedBytes)
                    throw new ImportException("unsafe_zip", $"The zip file would unpack to more than {options.FilesMaxUnpackedBytes / (1024 * 1024)} MB.");
            }
            if (declaredTotal > Math.Max(archiveSize, 1) * options.FilesMaxRatio && declaredTotal > RatioFloorBytes)
                throw new ImportException("unsafe_zip", $"The zip file is packed more than {options.FilesMaxRatio} to 1 as a whole; this looks like a zip bomb and was refused.");

            // Pass 2: read each entry with a hard limit (the declared size may lie), check it, then write it.
            var result = new List<StagedFile>();
            long actualTotal = 0;
            foreach (var entry in archive.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                var limit = Math.Min(CanvasImaging.MaxImportBytes, options.FilesMaxUnpackedBytes - actualTotal);
                byte[] bytes;
                using (var entryStream = entry.Open())
                    bytes = ReadLimited(entryStream, limit, entry.FullName);
                actualTotal += bytes.Length;
                if (bytes.Length > RatioFloorBytes && bytes.Length / (double)Math.Max(1, entry.CompressedLength) > options.FilesMaxRatio)
                    throw new ImportException("unsafe_zip", $"A file in the zip is packed more than {options.FilesMaxRatio} to 1; this looks like a zip bomb and was refused.");
                result.Add(WriteChecked(targetDirectory, entry.FullName, bytes));
            }
            return result;
        }
    }

    private static byte[] ReadLimited(Stream stream, long limit, string name)
    {
        using (stream)
        {
            try
            {
                return CanvasImaging.ReadAtMost(stream, limit, "A file");
            }
            catch (ImageRejectedException)
            {
                throw new ImportException("unsafe_zip", $"A file ({name}) is larger than the limit.");
            }
            catch (InvalidDataException)
            {
                throw new ImportException("unsafe_zip", "The zip file is damaged and could not be read.");
            }
        }
    }

    private static StagedFile WriteChecked(string directory, string name, byte[] bytes)
    {
        int width, height;
        try
        {
            (width, height) = CanvasImaging.ReadAndCheckPngHeader(bytes);
        }
        catch (ImageRejectedException ex)
        {
            throw new ImportException("not_a_png", $"{name}: {ex.Message}");
        }
        RequirePngStructure(bytes, name);

        var path = Path.Combine(directory, name);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(bytes, 0, bytes.Length);
        return new StagedFile { Name = name, Path = path, Bytes = bytes.Length, Width = width, Height = height, Sha256 = ImportFs.Sha256Bytes(bytes) };
    }

    /// <summary>Walks the chunk list: each chunk fits in the file, the first is IHDR, the last is IEND with nothing after it.</summary>
    private static void RequirePngStructure(byte[] data, string name)
    {
        int pos = 8;
        bool first = true;
        while (true)
        {
            if (pos + 12 > data.Length)
                throw new ImportException("not_a_png", $"{name} is not a complete PNG image.");
            long length = ((long)data[pos] << 24) | ((long)data[pos + 1] << 16) | ((long)data[pos + 2] << 8) | data[pos + 3];
            var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            if (first && type != "IHDR")
                throw new ImportException("not_a_png", $"{name} is not a valid PNG image.");
            first = false;
            long next = pos + 12 + length;
            if (next > data.Length)
                throw new ImportException("not_a_png", $"{name} is not a complete PNG image.");
            pos = (int)next;
            if (type == "IEND")
            {
                if (pos != data.Length)
                    throw new ImportException("not_a_png", $"{name} has extra data after the end of the PNG image.");
                return;
            }
        }
    }
}
