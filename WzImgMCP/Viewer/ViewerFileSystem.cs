using System.Runtime.InteropServices;

namespace WzImgMCP.Viewer;

/// <summary>Size and modification time of a file; two equal stamps mean "the same content" for the viewer.</summary>
public readonly record struct FileStamp(long Size, long MtimeTicks);

/// <summary>Device and inode: which file it really is, whatever path was used to reach it.</summary>
public readonly record struct FileIdentity(ulong Device, ulong Inode);

/// <summary>
/// What a read returned: the bytes, and the stamp of the OPEN file before and after the read. When the two stamps
/// differ, the file changed while it was read.
/// </summary>
public sealed record FileRead(byte[] Bytes, FileStamp Before, FileStamp After);

/// <summary>
/// The only way the viewer reads image bytes. A seam so tests can fake a file that changes while it is read.
/// </summary>
public interface IViewerFileSystem
{
    /// <summary>lstat: the stamp of the file at <paramref name="path"/> (a symbolic link is not followed), or null when there is none.</summary>
    FileStamp? Stat(string path);

    /// <summary>
    /// Reads the whole file safely. Throws a <see cref="ViewerApiException"/>: <c>not_found</c> (404) when it cannot be opened,
    /// <c>bad_path</c> (400) when it is not a regular file or is not the file the resolver checked, <c>too_large</c> (413) when it
    /// is bigger than <paramref name="maxBytes"/>.
    /// </summary>
    /// <param name="expected">
    /// The identity (and, because it is given, the fully resolved path) that the resolver checked. When null, only the
    /// regular-file and no-final-link checks are made (used for the manifest, whose path comes from configuration).
    /// </param>
    FileRead Read(string path, long maxBytes, FileIdentity? expected);

    /// <summary>The identity of the file at <paramref name="path"/> without following a final link, or null.</summary>
    FileIdentity? Identity(string path);
}

/// <summary>
/// Linux implementation. A path that was checked a moment ago can be swapped for a symbolic link or a FIFO before it is
/// opened, so the open is made as defensively as the kernel allows:
/// <list type="bullet">
/// <item>lstat first: anything that is not a regular file is refused before any open (an open of a device file can have side effects);</item>
/// <item>open with O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC, so a final symbolic link fails and a FIFO cannot block;</item>
/// <item>fstat on the OPEN handle: must be S_IFREG, and its device and inode must equal the lstat result and what the resolver checked;</item>
/// <item>the real path of the open handle (/proc/self/fd) must equal the resolved path, which catches a parent folder swapped for a link in between;</item>
/// <item>size cap before reading; the stamp of the handle is taken before and after the read.</item>
/// </list>
/// Uses statx (one struct layout on every CPU) and libc open/read/close. Linux only.
/// </summary>
public sealed unsafe class PosixViewerFileSystem : IViewerFileSystem
{
    private const int O_RDONLY = 0;
    private const int O_NONBLOCK = 0x800;
    private const int O_CLOEXEC = 0x80000;
    private const int AT_FDCWD = -100;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const int AT_EMPTY_PATH = 0x1000;
    private const uint STATX_BASIC_STATS = 0x7ff;
    private const int S_IFMT = 0xF000;
    private const int S_IFREG = 0x8000;
    private const int EINTR = 4;
    private const int ELOOP = 40;

    /// <summary>O_NOFOLLOW differs between CPU families (x86: 0x20000, arm64: 0x8000).</summary>
    private static int NoFollowFlag => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 or Architecture.X86 => 0x20000,
        Architecture.Arm64 or Architecture.Arm => 0x8000,
        _ => throw new PlatformNotSupportedException("the viewer file reader supports x86-64 and arm64 only"),
    };

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int close(int fd);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint read(int fd, byte* buffer, nint count);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte* buffer);

    private readonly record struct StatInfo(int Mode, ulong Inode, ulong Device, long Size, long MtimeTicks)
    {
        public bool IsRegular => (Mode & S_IFMT) == S_IFREG;
        public FileStamp Stamp => new(Size, MtimeTicks);
        public FileIdentity Identity => new(Device, Inode);
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("the viewer file reader runs on Linux only");
    }

    // struct statx: the layout is the same on every architecture.
    private static StatInfo? StatPath(string path, bool followLinks)
    {
        RequireLinux();
        byte* buffer = stackalloc byte[256];
        if (statx(AT_FDCWD, path, followLinks ? 0 : AT_SYMLINK_NOFOLLOW, STATX_BASIC_STATS, buffer) != 0)
            return null;
        return Parse(buffer);
    }

    private static StatInfo? StatHandle(int fd)
    {
        byte* buffer = stackalloc byte[256];
        if (statx(fd, "", AT_EMPTY_PATH, STATX_BASIC_STATS, buffer) != 0)
            return null;
        return Parse(buffer);
    }

    private static StatInfo Parse(byte* b)
    {
        int mode = *(ushort*)(b + 28);
        ulong inode = *(ulong*)(b + 32);
        long size = *(long*)(b + 40);
        long mtimeSec = *(long*)(b + 112);
        uint mtimeNsec = *(uint*)(b + 120);
        ulong devMajor = *(uint*)(b + 136);
        ulong devMinor = *(uint*)(b + 140);
        return new StatInfo(mode, inode, (devMajor << 32) | devMinor, size, mtimeSec * TimeSpan.TicksPerSecond + mtimeNsec / 100);
    }

    public FileStamp? Stat(string path) => StatPath(path, followLinks: false)?.Stamp;

    public FileIdentity? Identity(string path) => StatPath(path, followLinks: false)?.Identity;

    public FileRead Read(string path, long maxBytes, FileIdentity? expected)
    {
        var first = StatPath(path, followLinks: false) ?? throw NotFound();
        if (!first.IsRegular)
            throw new ViewerApiException(400, "bad_path", "not a regular file");   // also a symbolic link, FIFO, socket, device

        int fd = open(path, O_RDONLY | NoFollowFlag | O_NONBLOCK | O_CLOEXEC);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw errno == ELOOP ? new ViewerApiException(400, "bad_path", "symbolic links are not allowed") : NotFound();
        }

        try
        {
            var opened = StatHandle(fd) ?? throw NotFound();
            if (!opened.IsRegular)
                throw new ViewerApiException(400, "bad_path", "not a regular file");
            if (opened.Identity != first.Identity || (expected.HasValue && opened.Identity != expected.Value))
                throw new ViewerApiException(400, "bad_path", "the file is not the one that was checked");
            if (expected.HasValue && !HandleIsAt(fd, path))
                throw new ViewerApiException(400, "bad_path", "the file is not the one that was checked");
            if (opened.Size > maxBytes)
                throw new ViewerApiException(413, "too_large", "the image is larger than the viewer accepts");

            var bytes = new byte[opened.Size];
            int total = 0;
            fixed (byte* p = bytes)
            {
                while (total < bytes.Length)
                {
                    nint n = read(fd, p + total, bytes.Length - total);
                    if (n < 0)
                    {
                        if (Marshal.GetLastPInvokeError() == EINTR) continue;
                        throw NotFound();
                    }
                    if (n == 0) break;   // the file got shorter: the stamps below will differ
                    total += (int)n;
                }
            }
            if (total < bytes.Length)
                Array.Resize(ref bytes, total);

            var after = StatHandle(fd) ?? throw NotFound();
            return new FileRead(bytes, opened.Stamp, after.Stamp);
        }
        finally
        {
            close(fd);
        }
    }

    /// <summary>True when /proc/self/fd/N points at <paramref name="path"/>; true when /proc cannot be read (nothing to compare).</summary>
    private static bool HandleIsAt(int fd, string path)
    {
        try
        {
            var target = new FileInfo("/proc/self/fd/" + fd).LinkTarget;
            return target == null || string.Equals(target, path, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static ViewerApiException NotFound() => new(404, "not_found", "image not found");
}
