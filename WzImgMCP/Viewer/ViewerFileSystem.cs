using System.Runtime.InteropServices;

namespace WzImgMCP.Viewer;

/// <summary>
/// Size, modification time, status-change time and inode of a file; two equal stamps mean "the same content" for the
/// viewer. mtime can be set to any value by the file's owner (<c>touch -r</c>, rsync -t); ctime cannot be set from user
/// space (any rewrite or metadata change moves it), and a replaced file has another inode.
/// </summary>
public readonly record struct FileStamp(long Size, long MtimeTicks, long CtimeTicks = 0, ulong Inode = 0);

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
    /// Reads the whole file safely. Throws a <see cref="ViewerApiException"/>: <c>not_found</c> (404) when the file is not there,
    /// <c>busy</c> (503) when the open failed for a transient reason (an interrupted call that kept being interrupted, no free
    /// file handles), <c>internal_error</c> (500) for any other failure to open or read (permission, I/O error),
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
    private readonly Func<int, string?> _procLinkReader;
    private readonly Func<string, int, (int Fd, int Errno)> _opener;

    /// <param name="procLinkReader">Test seam: where the open handle <c>fd</c> really points (null when that cannot be read). The default reads /proc/self/fd.</param>
    /// <param name="opener">Test seam for <c>open(path, flags)</c>: the descriptor (negative on failure) and errno. The default calls libc.</param>
    public PosixViewerFileSystem(Func<int, string?>? procLinkReader = null, Func<string, int, (int Fd, int Errno)>? opener = null)
    {
        _procLinkReader = procLinkReader ?? ReadProcLink;
        _opener = opener ?? LibcOpen;
    }

    private static (int Fd, int Errno) LibcOpen(string path, int flags)
    {
        int fd = open(path, flags);
        return (fd, fd < 0 ? Marshal.GetLastPInvokeError() : 0);
    }

    private const int O_RDONLY = 0;
    private const int O_NONBLOCK = 0x800;
    private const int O_CLOEXEC = 0x80000;
    private const int AT_FDCWD = -100;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const int AT_EMPTY_PATH = 0x1000;
    private const uint STATX_BASIC_STATS = 0x7ff;
    private const int S_IFMT = 0xF000;
    private const int S_IFREG = 0x8000;
    private const int ENOENT = 2;
    private const int EINTR = 4;
    private const int EAGAIN = 11;
    private const int ENOMEM = 12;
    private const int ENOTDIR = 20;
    private const int ENFILE = 23;
    private const int EMFILE = 24;
    private const int ELOOP = 40;
    private const int OpenAttempts = 8;

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

    private readonly record struct StatInfo(int Mode, ulong Inode, ulong Device, long Size, long MtimeTicks, long CtimeTicks)
    {
        public bool IsRegular => (Mode & S_IFMT) == S_IFREG;
        public FileStamp Stamp => new(Size, MtimeTicks, CtimeTicks, Inode);
        public FileIdentity Identity => new(Device, Inode);
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("the viewer file reader runs on Linux only");
    }

    // struct statx: the layout is the same on every architecture.
    private static StatInfo? StatPath(string path, bool followLinks) => StatPath(path, followLinks, out _);

    private static StatInfo? StatPath(string path, bool followLinks, out int errno)
    {
        RequireLinux();
        errno = 0;
        byte* buffer = stackalloc byte[256];
        if (statx(AT_FDCWD, path, followLinks ? 0 : AT_SYMLINK_NOFOLLOW, STATX_BASIC_STATS, buffer) != 0)
        {
            errno = Marshal.GetLastPInvokeError();
            return null;
        }
        return Parse(buffer);
    }

    private static StatInfo? StatHandle(int fd, out int errno)
    {
        errno = 0;
        byte* buffer = stackalloc byte[256];
        if (statx(fd, "", AT_EMPTY_PATH, STATX_BASIC_STATS, buffer) != 0)
        {
            errno = Marshal.GetLastPInvokeError();
            return null;
        }
        return Parse(buffer);
    }

    private static StatInfo Parse(byte* b)
    {
        int mode = *(ushort*)(b + 28);
        ulong inode = *(ulong*)(b + 32);
        long size = *(long*)(b + 40);
        long ctimeSec = *(long*)(b + 96);
        uint ctimeNsec = *(uint*)(b + 104);
        long mtimeSec = *(long*)(b + 112);
        uint mtimeNsec = *(uint*)(b + 120);
        ulong devMajor = *(uint*)(b + 136);
        ulong devMinor = *(uint*)(b + 140);
        return new StatInfo(mode, inode, (devMajor << 32) | devMinor, size,
            mtimeSec * TimeSpan.TicksPerSecond + mtimeNsec / 100, ctimeSec * TimeSpan.TicksPerSecond + ctimeNsec / 100);
    }

    public FileStamp? Stat(string path) => StatPath(path, followLinks: false)?.Stamp;

    public FileIdentity? Identity(string path) => StatPath(path, followLinks: false)?.Identity;

    public FileRead Read(string path, long maxBytes, FileIdentity? expected)
    {
        var first = StatPath(path, followLinks: false, out int statErrno) ?? throw Failure(statErrno);
        if (!first.IsRegular)
            throw new ViewerApiException(400, "bad_path", "not a regular file");   // also a symbolic link, FIFO, socket, device

        int fd;
        int errno;
        int attempts = 0;
        do
        {
            (fd, errno) = _opener(path, O_RDONLY | NoFollowFlag | O_NONBLOCK | O_CLOEXEC);
        }
        while (fd < 0 && errno == EINTR && ++attempts < OpenAttempts);   // a signal interrupted the call: try again
        if (fd < 0)
            throw errno == ELOOP ? new ViewerApiException(400, "bad_path", "symbolic links are not allowed") : Failure(errno);

        try
        {
            var opened = StatHandle(fd, out int openedErrno) ?? throw Failure(openedErrno);
            if (!opened.IsRegular)
                throw new ViewerApiException(400, "bad_path", "not a regular file");
            if (opened.Identity != first.Identity || (expected.HasValue && opened.Identity != expected.Value))
                throw new ViewerApiException(400, "bad_path", "the file is not the one that was checked");
            if (expected.HasValue && !HandleIsAt(fd, path, _procLinkReader))
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
                        int readErrno = Marshal.GetLastPInvokeError();
                        if (readErrno == EINTR) continue;
                        throw Failure(readErrno);
                    }
                    if (n == 0) break;   // the file got shorter: the stamps below will differ
                    total += (int)n;
                }
            }
            if (total < bytes.Length)
                Array.Resize(ref bytes, total);

            var after = StatHandle(fd, out int afterErrno) ?? throw Failure(afterErrno);
            return new FileRead(bytes, opened.Stamp, after.Stamp);
        }
        finally
        {
            close(fd);
        }
    }

    /// <summary>
    /// True only when the open handle really points at <paramref name="path"/>. Fails closed: when the link cannot be read
    /// (no /proc, no permission) the file is refused, because the parent-folder check could not be made.
    /// </summary>
    private static bool HandleIsAt(int fd, string path, Func<int, string?> reader)
    {
        string? target;
        try { target = reader(fd); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        return target != null && string.Equals(target, path, StringComparison.Ordinal);
    }

    private static string? ReadProcLink(int fd) => new FileInfo("/proc/self/fd/" + fd).LinkTarget;

    /// <summary>
    /// What an errno means to the client. Only "there is no such file" is 404: a transient failure (an open that stayed
    /// interrupted, no free handles or memory) is 503 so it is not read as "the file is missing", and anything else
    /// (permission, I/O error) is 500.
    /// </summary>
    private static ViewerApiException Failure(int errno) => errno switch
    {
        ENOENT or ENOTDIR => new(404, "not_found", "image not found"),
        EINTR or EAGAIN or EMFILE or ENFILE or ENOMEM => new(503, "busy", "the image could not be opened right now; retry shortly"),
        _ => new(500, "internal_error", "the image could not be read"),
    };
}
