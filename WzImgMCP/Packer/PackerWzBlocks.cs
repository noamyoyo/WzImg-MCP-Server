using System.Security.Cryptography;
using MapleLib.WzLib;

namespace WzImgMCP.Packer;

/// <summary>One image of a .wz as it is stored: where its block is, how big, the checksum of the directory entry and the sha256 of the raw block.</summary>
public sealed record PackerBlock(string Path, long Offset, int Size, int Checksum, string Sha256);

/// <summary>
/// Lists the images of a parsed .wz by path (below the .wz root: <c>Sub/Deep.img</c>) with the sha256 of each image's raw stored block.
/// Nothing is parsed: the block is read from the file as it lies there, so two files agree on an image exactly when the bytes the
/// game would read are the same. Names are matched without regard to case, as the library and the game do; two images that
/// would share a path get a <c>#2</c>, <c>#3</c> suffix in the order they are stored.
/// </summary>
public static class PackerWzBlocks
{
    public static Dictionary<string, PackerBlock> Index(WzFile wz, string wzPath, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, PackerBlock>(StringComparer.OrdinalIgnoreCase);
        using var stream = new FileStream(wzPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1);
        var buffer = new byte[1024 * 1024];
        Walk(wz.WzDirectory, "", stream, buffer, found, cancellationToken);
        return found;
    }

    private static void Walk(WzDirectory dir, string prefix, FileStream stream, byte[] buffer, Dictionary<string, PackerBlock> found, CancellationToken cancellationToken)
    {
        foreach (var image in dir.WzImages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = prefix + image.Name;
            for (int n = 2; found.ContainsKey(path); n++)
                path = prefix + image.Name + "#" + n;
            found[path] = new PackerBlock(path, image.Offset, image.BlockSize, image.Checksum, HashBlock(stream, image.Offset, image.BlockSize, buffer));
        }
        foreach (var sub in dir.WzDirectories)
            Walk(sub, prefix + sub.Name + "/", stream, buffer, found, cancellationToken);
    }

    private static string HashBlock(FileStream stream, long offset, int size, byte[] buffer)
    {
        if (offset < 0 || size < 0 || offset + size > stream.Length)
            throw new InvalidDataException("an image block lies outside the file");
        stream.Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long left = size;
        while (left > 0)
        {
            int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n <= 0) throw new InvalidDataException("an image block lies outside the file");
            hash.AppendData(buffer, 0, n);
            left -= n;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
