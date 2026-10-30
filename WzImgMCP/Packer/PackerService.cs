using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MapleLib.Helpers;
using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;
using MapleLib.WzLib.WzProperties;
using Microsoft.Extensions.Logging;
using WzImgMCP.Viewer;
using WzImgMCP.Viewer.Diff;

namespace WzImgMCP.Packer;

/// <summary>One difference found by verify. <c>Path</c> is the .img path (<c>String/Pet.img</c>), or, for a value inside it, the .img path, a colon and the path of the value.</summary>
public sealed record PackerDiff(string Path, string Kind, string Type);

/// <summary>
/// The answer of <c>POST /v1/verify</c>. <c>UnchangedIdentical</c>: every image of the published file that the pack did not list is in the
/// new file with the same raw stored block. <c>OnlyListedDiffer</c>: no image outside the list was added or removed, every listed set is
/// in the new file and every listed remove is gone. <c>Version</c> and <c>Key</c> are what the new file opens as (0 and "" when it does not open).
/// <c>FilesCompared</c> counts the listed images compared with the input snapshot by value, picture and link text.
/// </summary>
public sealed record PackerVerifyResult(
    bool Identical, bool UnchangedIdentical, bool OnlyListedDiffer, int Version, string Key,
    int UnchangedCompared, int FilesCompared, IReadOnlyList<PackerDiff> Diffs, bool Truncated)
{
    /// <summary>The version the new file opens as: the same number as <c>Version</c>, under the name the publisher reads.</summary>
    public int WzVersion => Version;

    /// <summary>Check 4: listed images (<c>String/Pet.img</c>) that do not open in the new file.</summary>
    public IReadOnlyList<string> OpenErrors { get; init; } = Array.Empty<string>();

    /// <summary>Check 4: links (UOL) inside the listed images that lead nowhere in the new file.</summary>
    public int BrokenLinks { get; init; }

    /// <summary>Check 5: pictures of the listed images in a format the v83 client cannot show (<c>String/Pet.img: format 1026</c>).</summary>
    public IReadOnlyList<string> UnsupportedFormats { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Packs a change set into the published <c>&lt;Category&gt;.wz</c>: it opens that file (read-only mount), replaces, adds or removes only
/// the listed images and keeps every other image as stored (not parsed, not re-serialized: the library copies the raw block). The
/// listed images come from a person's extracted tree (zero-IV <c>.img</c> files); each is parsed with that key, copied into the pack's
/// input snapshot (<c>&lt;jobId&gt;/input/img</c>) and re-serialized into the file with the key GMS, version 83. Verify compares the
/// new file with that snapshot and with the published file.
///
/// Writes: only below <c>&lt;outbox&gt;/&lt;jobId&gt;</c> (a fresh folder per job, the work in a fresh <c>.work</c> folder below it,
/// the finished file and the snapshot moved up in one rename each) and, for verify, in a folder of its own in the temp root that is
/// removed again. The trees and the published folder are only read. Responses, job texts and logs hold no absolute path: texts from
/// the library are scrubbed.
/// </summary>
public sealed class PackerService
{
    public const int WzVersion = 83;
    public const string Key = "GMS";
    public const string InputFolder = "input";
    private const long SaveMargin = 16L * 1024 * 1024;

    private static readonly Regex PathLike = new(@"(?<![A-Za-z0-9_.\-])(?:[A-Za-z]:)?(?:[\\/][^\s\\/'""<>|:;,()\[\]{}]+){2,}[\\/]?", RegexOptions.CultureInvariant);

    private readonly PackerOptions _options;
    private readonly PosixViewerFileSystem _files;
    private readonly PackerWorkGate _gate;
    private readonly PackerJobStore _jobs;
    private readonly ILogger _logger;

    public PackerService(PackerOptions options, PosixViewerFileSystem files, PackerWorkGate gate, PackerJobStore jobs, ILoggerFactory loggers)
    {
        _options = options;
        _files = files;
        _gate = gate;
        _jobs = jobs;
        _logger = loggers.CreateLogger("WzImgMCP.Packer");
    }

    /// <summary>Test seam: called with "start" (before the work folder is made), "packing" (just before the published file is opened), "verify" (before the .wz is copied) and "copied" (after it was copied and hashed), with the job id.</summary>
    public Action<string, string>? Probe { get; set; }

    // ------------------------------------------------------ published file

    /// <summary>The sha256 and size of the packer's read-only copy of the published <c>&lt;Category&gt;.wz</c>. The publisher compares it with the target manifest and its own client folder (<c>published_out_of_sync</c>).</summary>
    public (string Sha256, long SizeBytes) PublishedInfo(string category, CancellationToken cancellationToken)
    {
        PackerTree.RequireCategory(category);
        var path = PublishedPath(category);
        var stamp = _files.Stat(path) ?? throw new ViewerApiException(503, "published_unavailable", "the published file cannot be read");
        var sha = Sha256Of(path, cancellationToken);
        if (_files.Stat(path) != stamp)
            throw new ViewerApiException(409, "published_changed", "the published file changed while it was read; retry");
        return (sha, stamp.Size);
    }

    // ---------------------------------------------------------------- pack

    /// <summary>Checks the request, takes the one pack slot, and starts the job on its own thread. Throws a typed <see cref="ViewerApiException"/>.</summary>
    public PackerJobView StartPack(string category, IReadOnlyList<PackerFile> files)
    {
        PackerTree.RequireCategory(category);
        PublishedPath(category);   // the published file must be there, a plain file

        // Every set must name a plain file of the person's copy (the resolver walks the path and refuses links). A remove names nothing on disk.
        var resolver = new ViewerPathResolver(_options.Roots, _files);
        foreach (var file in files.Where(f => f.Op == PackerFile.Set))
            resolver.Resolve(file.From, file.Img);

        if (CountJobFolders() >= _options.MaxJobFolders)
            throw new ViewerApiException(503, "outbox_full", "the outbox holds too many job folders; the publisher must clean it up");

        var lease = _gate.TryEnter() ?? throw Busy();
        PackerJob job;
        try
        {
            job = _jobs.Create(category, files);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        var view = job.View();   // "queued", as the caller sees it
        _ = Task.Run(() => RunPack(job, files, lease));
        return view;
    }

    private void RunPack(PackerJob job, IReadOnlyList<PackerFile> files, PackerWorkGate.Lease lease)
    {
        using var cts = new CancellationTokenSource(_options.TimeBudget);
        var jobDir = _jobs.JobDirectory(job.Id);
        var work = Path.Combine(jobDir, ".work");
        PackerWz? published = null;
        _logger.LogInformation("pack {JobId} running", job.Id);
        try
        {
            try
            {
                job.MarkRunning();
                _jobs.Persist(job);
                Probe?.Invoke("start", job.Id);

                var category = job.Category;
                var publishedPath = PublishedPath(category);
                var stampBefore = _files.Stat(publishedPath) ?? throw new PackerJobException("published_unavailable", "the published file cannot be read");
                if (stampBefore.Size > _options.MaxWzBytes)
                    throw new PackerJobException("output_too_large", "the published .wz is larger than the packer allows");

                // The listed images are read into memory one by one while the file is changed; their total is capped.
                var resolver = new ViewerPathResolver(_options.Roots, _files);
                var sets = files.Where(f => f.Op == PackerFile.Set).ToList();
                long inputBytes = 0;
                foreach (var file in sets)
                {
                    var size = resolver.Resolve(file.From, file.Img).SizeBytes;
                    if (size > _options.MaxImageBytes)
                        throw new PackerJobException("image_too_large", $"{file.Img} is larger than the packer accepts");
                    inputBytes += size;
                    if (inputBytes > _options.MaxInputBytes)
                        throw new PackerJobException("input_too_large", "the listed images are larger than the packer accepts");
                }
                // The saved file is about the size of the published one; the library also writes the changed images once more as a scratch file next to it.
                if (FreeSpace(_options.Outbox) < stampBefore.Size + 2 * inputBytes + SaveMargin)
                    throw new PackerJobException("insufficient_space", "the outbox does not have room for the .wz");

                MakeFreshFolder(work, jobDir);
                var snapshot = Path.Combine(work, InputFolder);
                MakeFreshFolder(snapshot, work);
                Directory.CreateDirectory(Path.Combine(snapshot, "img"));
                Probe?.Invoke("packing", job.Id);
                cts.Token.ThrowIfCancellationRequested();

                var publishedSha = Sha256Of(publishedPath, cts.Token);
                published = new PackerWz(category + ".wz", stampBefore.Size, publishedSha);
                var produced = Path.Combine(work, category + ".wz");
                PackerFileResult[] results;
                try
                {
                    // The published .wz stays open while the new one is saved: unchanged images are copied from it, as stored.
                    using var wz = new WzFile(publishedPath, (short)WzVersion, WzMapleVersion.GMS);
                    if (!ParsesAs83Gms(wz))
                        throw new PackerJobException("published_unreadable", "the published file does not open as version 83 with the key GMS");
                    results = ApplyFiles(wz, category, files, resolver, snapshot, job, cts.Token);
                    cts.Token.ThrowIfCancellationRequested();
                    wz.SaveToDisk(produced);
                }
                finally
                {
                    ErrorLogger.ClearErrors();
                }
                cts.Token.ThrowIfCancellationRequested();

                if (_files.Stat(publishedPath) != stampBefore)
                    throw new PackerJobException("published_changed", "the published file changed while the pack was made; pack again");
                if (!_files.IsRegularFile(produced))
                    throw new PackerJobException("pack_failed", "packing produced no .wz file");
                var outputSize = new FileInfo(produced).Length;
                if (outputSize > _options.MaxWzBytes)
                    throw new PackerJobException("output_too_large", "the packed .wz is larger than the packer allows");

                var final = Path.Combine(jobDir, category + ".wz");
                File.Move(produced, final, overwrite: false);   // a rename inside the job folder; it never writes through a link
                Directory.Move(snapshot, Path.Combine(jobDir, InputFolder));
                var sha = Sha256Of(final, cts.Token);
                job.StageDone(new PackerWz(category + ".wz", outputSize, sha), published, results);
            }
            catch (OperationCanceledException)
            {
                job.StageFailed("timeout", "the pack took longer than the time budget", published);
            }
            catch (PackerJobException ex)
            {
                job.StageFailed(ex.Code, ex.Message, published);
            }
            catch (ViewerApiException ex)
            {
                job.StageFailed(ex.Code, ex.Message, published);   // a source that went bad after the request was accepted: the resolver's and reader's texts hold no path
            }
            catch (Exception ex)
            {
                job.StageFailed("internal_error", "the pack failed unexpectedly", published);
                _logger.LogError("pack {JobId} failed: unhandled exception of type {ExceptionType}", job.Id, ex.GetType().Name);
            }
        }
        finally
        {
            // Order matters. First the folder is cleaned and the final record written, then the work slot is given back, and only then
            // does the job read "done" or "failed": whoever reacts to that state finds the packer free and the record complete.
            ErrorLogger.ClearErrors();   // MapleLib keeps every logged problem in a list that only grows
            RemoveWorkFolder(work);
            var final = job.FinalView();
            if (final.State != PackerJob.Done)
                RemoveFinishedOutput(jobDir, job.Category);   // a failed job keeps its record and nothing else
            _jobs.Persist(final);
            lease.Dispose();
            job.Publish();
            if (final.State == PackerJob.Done) _logger.LogInformation("pack {JobId} done", job.Id);
            else _logger.LogWarning("pack {JobId} failed: {Code}", job.Id, final.Error?.Code);
        }
    }

    /// <summary>
    /// Changes the opened published file: only the listed images are touched. A replaced image is changed in place (parsed first, then
    /// cleared, filled with copies of the new properties and marked changed), so its place in the file is kept; an added image is
    /// appended to its folder (the folders are made when missing); a removed image is taken out. Every other image is never parsed
    /// and is copied as stored when the file is saved. Returns the result of every file, in the order of the request.
    /// </summary>
    private PackerFileResult[] ApplyFiles(WzFile wz, string category, IReadOnlyList<PackerFile> files, ViewerPathResolver resolver, string snapshot, PackerJob job, CancellationToken token)
    {
        var results = new PackerFileResult[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var file = files[i];
            var parts = file.WzPath(category).Split('/');
            var (parent, existing) = FindImage(wz.WzDirectory, parts, file.Img);

            if (file.Op == PackerFile.Remove)
            {
                if (existing == null)
                    throw new PackerJobException("image_not_found", $"{file.Img} cannot be removed: the published file does not hold it");
                parent!.RemoveImage(existing);
                results[i] = new PackerFileResult(file.Img, file.From, file.Op, PackerFileResult.Removed, null, null);
                continue;
            }

            var resolved = resolver.Resolve(file.From, file.Img);
            var read = _files.Read(resolved.FullPath, _options.MaxImageBytes, resolved.Identity);
            if (read.Before != read.After || read.Bytes.Length != read.After.Size)
                throw new PackerJobException("input_changed", $"{file.Img} changed while it was read; pack again");
            var bytes = read.Bytes;
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            WriteSnapshot(snapshot, file.Img, bytes);
            var incoming = ParseInput(file.Img, bytes);

            if (existing != null)
            {
                bool parsed;
                try { parsed = existing.ParseImage(); }   // BEFORE it is marked changed: marking an unparsed image changed would empty it
                catch (Exception ex) when (ex is not OperationCanceledException) { parsed = false; }
                if (!parsed)
                    job.AddWarning($"{file.Img}: the old image could not be read; it was replaced anyway");
                existing.ClearProperties();
                foreach (var property in incoming.WzProperties)
                    existing.WzProperties.Add(property.DeepClone());   // the collection keeps duplicate names, as the file had them
                existing.Changed = true;
                results[i] = new PackerFileResult(file.Img, file.From, file.Op, PackerFileResult.Replaced, sha, bytes.Length);
            }
            else
            {
                var dir = wz.WzDirectory;
                foreach (var part in parts[..^1])
                {
                    var next = dir.GetDirectoryByName(part);
                    if (next == null)
                    {
                        next = new WzDirectory(part, wz);
                        dir.AddDirectory(next);
                    }
                    dir = next;
                }
                var added = new WzImage(parts[^1]) { Changed = true };
                foreach (var property in incoming.WzProperties)
                    added.WzProperties.Add(property.DeepClone());
                dir.AddImage(added);   // at the end of its folder
                results[i] = new PackerFileResult(file.Img, file.From, file.Op, PackerFileResult.Added, sha, bytes.Length);
            }
            incoming.Dispose();
        }
        return results;
    }

    /// <summary>
    /// The image of a path in the opened file (null when it is not there), with the folder that holds it (null when the folder is not there
    /// either). Names are matched without regard to case, as the file does; two images or folders that differ only in case are refused.
    /// </summary>
    private static (WzDirectory? Parent, WzImage? Image) FindImage(WzDirectory root, string[] parts, string img)
    {
        var dir = root;
        foreach (var part in parts[..^1])
        {
            var matches = dir.WzDirectories.Where(d => string.Equals(d.Name, part, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (matches.Count > 1)
                throw new PackerJobException("ambiguous_name", $"{img}: the published file holds two folders that differ only in case");
            if (matches.Count == 0)
                return (null, null);
            dir = matches[0];
        }
        var images = dir.WzImages.Where(i => string.Equals(i.Name, parts[^1], StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        if (images.Count > 1)
            throw new PackerJobException("ambiguous_name", $"{img}: the published file holds two images that differ only in case");
        return (dir, images.Count == 1 ? images[0] : null);
    }

    /// <summary>Parses an extracted .img (zero key, BMS). A file that does not parse with that key is refused: it is never copied as it is.</summary>
    private static WzImage ParseInput(string img, byte[] bytes)
    {
        WzImage? image = null;
        try
        {
            image = new WzImgDeserializer(freeResources: false, calculateChecksum: false)
                .WzImageFromIMGBytes(bytes, WzMapleVersion.BMS, Path.GetFileName(img), freeResources: false);
            if (image.ParseImage())
                return image;
        }
        catch (OperationCanceledException)
        {
            image?.Dispose();
            throw;
        }
        catch (Exception)
        {
            // a key that does not fit or a damaged file: the same answer below
        }
        finally
        {
            ErrorLogger.ClearErrors();
        }
        image?.Dispose();
        throw new PackerJobException("bad_image", $"{img} cannot be read as an extracted .img (key BMS)");
    }

    /// <summary>Stores the exact bytes of an input image below the snapshot's <c>img</c> folder, as a new file, inside the work folder.</summary>
    private static void WriteSnapshot(string snapshot, string img, byte[] bytes)
    {
        var imgRoot = Path.Combine(snapshot, "img");
        var target = Path.Combine(imgRoot, img);
        try
        {
            var dir = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(dir);
            var realRoot = Utils.PathGuard.ResolveRealPath(Path.GetFullPath(imgRoot));
            var realDir = Utils.PathGuard.ResolveRealPath(Path.GetFullPath(dir));
            if (!(realDir + Path.DirectorySeparatorChar).StartsWith(realRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new PackerJobException("outbox_unsafe", "the job folder is not in the expected state");
            using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);   // CreateNew never opens through a link
            stream.Write(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PackerJobException("outbox_unavailable", "the input snapshot cannot be written");
        }
    }

    /// <summary>The published file of a category, checked: a plain file in the read-only published folder. Throws a typed 400, 404 or 503.</summary>
    private string PublishedPath(string category)
    {
        if (_options.PublishedDir == null)
            throw new ViewerApiException(503, "published_unavailable", "no published folder is configured");
        var path = _options.PublishedFile(category);
        try
        {
            if (new DirectoryInfo(_options.PublishedDir).LinkTarget != null)
                throw new ViewerApiException(400, "bad_path", "the published folder is a symbolic link");
            if (!Directory.Exists(_options.PublishedDir))
                throw new ViewerApiException(503, "published_unavailable", "the published folder is not available; retry later");
            switch (_files.Probe(path))
            {
                case PathState.Unavailable: throw new ViewerApiException(503, "published_unavailable", "the published folder is not available; retry later");
                case PathState.Absent: throw new ViewerApiException(404, "not_found", "the published file of this category was not found");
            }
            if (new FileInfo(path).LinkTarget != null)
                throw new ViewerApiException(400, "bad_path", "the published file is a symbolic link");
            if (!_files.IsRegularFile(path))
                throw new ViewerApiException(400, "bad_path", "the published file is not a regular file");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ViewerApiException(503, "published_unavailable", "the published folder is not available; retry later");
        }
        return path;
    }

    /// <summary>Creates <paramref name="path"/> as a new empty folder directly inside <paramref name="parent"/> and checks it is not a link and is where it should be.</summary>
    private void MakeFreshFolder(string path, string parent)
    {
        if (_files.Probe(path) != PathState.Absent)
            throw new PackerJobException("outbox_unsafe", "the job folder is not in the expected state");
        Directory.CreateDirectory(path);
        var info = new DirectoryInfo(path);
        if (info.LinkTarget != null || !info.Exists || info.EnumerateFileSystemInfos().Any())
            throw new PackerJobException("outbox_unsafe", "the job folder is not in the expected state");
        var realParent = Utils.PathGuard.ResolveRealPath(Path.GetFullPath(parent));
        var real = Utils.PathGuard.ResolveRealPath(Path.GetFullPath(path));
        if (!string.Equals(Path.GetDirectoryName(real), realParent, StringComparison.Ordinal))
            throw new PackerJobException("outbox_unsafe", "the job folder is not in the expected state");
    }

    /// <summary>Removes what a job that failed late may have moved into its folder: the .wz and the snapshot, when they are plain entries (a link is left alone).</summary>
    private static void RemoveFinishedOutput(string jobDir, string category)
    {
        try
        {
            var wz = new FileInfo(Path.Combine(jobDir, category + ".wz"));
            if (wz.Exists && wz.LinkTarget == null)
                wz.Delete();
            var input = new DirectoryInfo(Path.Combine(jobDir, InputFolder));
            if (input.Exists && input.LinkTarget == null)
                input.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a leftover in a failed job's folder is harmless: only a done job's file is ever taken
        }
    }

    private static void RemoveWorkFolder(string work)
    {
        try
        {
            if (new DirectoryInfo(work).LinkTarget != null)
                File.Delete(work);   // a link planted there: remove the link, never what it points at
            else if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an empty or half-removed work folder is harmless; the .wz is only ever read from <jobId>/<Category>.wz
        }
    }

    private static string Sha256Of(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, n);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Copies <paramref name="source"/> to the new file <paramref name="target"/> and returns its size and sha256, computed on the bytes that were copied.</summary>
    private static (long Size, string Sha256) CopyAndHash(string source, string target, CancellationToken cancellationToken)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, n);
            output.Write(buffer, 0, n);
            total += n;
        }
        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>
    /// Parses a .wz that was made with version 83 and the key GMS fixed (the extractor on its own detects any version). False when it does
    /// not parse that way, whatever the library does about it. A file written with another key still parses (the library cannot tell), but
    /// its folder and image names come out as noise, so the key is checked by the names: every one must follow the viewer's name rules.
    /// </summary>
    private static bool ParsesAs83Gms(WzFile wz)
    {
        try
        {
            return wz.ParseWzFile() == WzFileParseStatus.Success && wz.Version == WzVersion && NamesAreSane(wz.WzDirectory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;   // not a .wz of this version and key
        }
        finally
        {
            ErrorLogger.ClearErrors();
        }
    }

    private static bool NamesAreSane(WzDirectory dir)
    {
        foreach (var image in dir.WzImages)
            if (!IsSaneName(image.Name)) return false;
        foreach (var sub in dir.WzDirectories)
            if (!IsSaneName(sub.Name) || !NamesAreSane(sub)) return false;
        return true;
    }

    // The names the viewer's path rules accept (the library decodes the noise of a wrong key as '?' and the like, so "printable" is not enough).
    private static bool IsSaneName(string? name)
        => !string.IsNullOrEmpty(name) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-' or '.');

    /// <summary>Opens a .wz with version 83 and the key GMS fixed. Null when it does not open that way.</summary>
    private static WzFile? OpenAsVersion83Gms(string path)
    {
        WzFile? wz = null;
        try
        {
            wz = new WzFile(path, (short)WzVersion, WzMapleVersion.GMS);
            if (ParsesAs83Gms(wz))
                return wz;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the file cannot be opened at all
        }
        wz?.Dispose();
        return null;
    }

    // -------------------------------------------------------------- verify

    /// <summary>
    /// Checks <c>&lt;outbox&gt;/&lt;jobId&gt;/&lt;Category&gt;.wz</c> (the file the publisher will take; the name is never read from the record).
    /// The file is copied once into the temp folder and hashed while it is copied (the hash must be the one the pack recorded); the
    /// published file must still hash to the one the pack was made from. Then, working on the copy:
    /// (1) it must open with version 83 and key GMS; (2) the raw stored block of every image is compared with the published file's,
    /// matched by path: an image the pack did not list must be there and equal, nothing may be added or removed outside the list, a
    /// listed set must be there and a listed remove gone; (3) every listed set is taken out of the new file and compared with the
    /// pack's input snapshot by value, picture and link text (the viewer's diff engine). Synchronous; the caller holds the work gate
    /// and runs it on its own thread.
    /// </summary>
    public PackerVerifyResult Verify(string jobId, CancellationToken cancellationToken)
    {
        var view = _jobs.Find(jobId) ?? throw new ViewerApiException(404, "not_found", "no such job");
        if (view.State != PackerJob.Done || view.Wz == null || view.Published == null)
            throw new ViewerApiException(409, "job_not_done", "the job has not produced a .wz");

        var category = view.Category;   // checked as a plain name when the record was loaded
        var wzName = category + ".wz";
        var jobDir = _jobs.JobDirectory(jobId);
        var wzPath = Path.Combine(jobDir, wzName);
        if (new DirectoryInfo(jobDir).LinkTarget != null
            || new FileInfo(wzPath).LinkTarget != null
            || !_files.IsRegularFile(wzPath))
            throw ChangedOutput();
        var publishedPath = PublishedPath(category);

        // The input snapshot: a plain file for every set, with the hash the pack recorded.
        var inputRoot = Path.Combine(jobDir, InputFolder);
        var sets = view.Files.Where(f => f.Op == PackerFile.Set).ToList();
        long snapshotBytes = 0;
        if (sets.Count > 0)
        {
            if (new DirectoryInfo(inputRoot).LinkTarget != null || !Directory.Exists(inputRoot))
                throw InputChanged();
            foreach (var file in sets)
            {
                var full = Path.Combine(inputRoot, "img", file.Img);
                if (!_files.IsRegularFile(full) || HasLinkBelow(Path.Combine(inputRoot, "img"), file.Img))
                    throw InputChanged();
                var length = new FileInfo(full).Length;
                snapshotBytes += length;
                if (length != file.SizeBytes || !string.Equals(Sha256Of(full, cancellationToken), file.Sha256, StringComparison.Ordinal))
                    throw InputChanged();
            }
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.TimeBudget);
        var token = budget.Token;
        var diffs = new List<PackerDiff>();
        var tempBase = Path.Combine(_options.TempRoot, TempPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());
        try
        {
            // 1. room in the temp folder for the copy of the .wz and for the listed images taken out of it
            var needed = view.Wz.SizeBytes + 2 * snapshotBytes;
            Directory.CreateDirectory(_options.TempRoot);
            if (FreeSpace(_options.TempRoot) < needed)
                throw new ViewerApiException(503, "insufficient_space", "the temp folder does not have room to verify this .wz");

            // 2. copy the .wz once; everything below works on the copy, so a writer of the outbox cannot swap the file in between
            var copyDir = Path.Combine(tempBase, "src");
            Directory.CreateDirectory(copyDir);
            var copy = Path.Combine(copyDir, wzName);
            Probe?.Invoke("verify", jobId);
            var (copySize, copySha) = CopyAndHash(wzPath, copy, token);
            if (copySize != view.Wz.SizeBytes || !string.Equals(copySha, view.Wz.Sha256, StringComparison.Ordinal))
                throw ChangedOutput();   // not the file the pack recorded (and so not the file that would be published)
            Probe?.Invoke("copied", jobId);

            // 3. the published file must be the one the pack was made from
            var publishedStamp = _files.Stat(publishedPath);
            if (publishedStamp == null || publishedStamp.Value.Size != view.Published.SizeBytes
                || !string.Equals(Sha256Of(publishedPath, token), view.Published.Sha256, StringComparison.Ordinal))
                throw new ViewerApiException(409, "published_changed", "the published file is not the one the pack was made from; pack again");

            // 4. it must open as version 83 with the key GMS
            using var newWz = OpenAsVersion83Gms(copy);
            if (newWz == null)
            {
                diffs.Add(new PackerDiff(wzName, "unreadable", "wz"));
                return new PackerVerifyResult(false, false, false, 0, "", 0, 0, diffs, false);
            }

            // 5. the raw stored blocks of both files, by path
            Dictionary<string, PackerBlock> inNew, inPublished;
            try
            {
                inNew = PackerWzBlocks.Index(newWz, copy, token);
                using var publishedWz = OpenAsVersion83Gms(publishedPath)
                    ?? throw new ViewerApiException(409, "published_changed", "the published file does not open as version 83 with the key GMS");
                inPublished = PackerWzBlocks.Index(publishedWz, publishedPath, token);
            }
            catch (InvalidDataException)
            {
                diffs.Add(new PackerDiff(wzName, "unreadable", "wz"));
                return new PackerVerifyResult(false, false, false, 0, "", 0, 0, diffs, false);
            }
            finally
            {
                ErrorLogger.ClearErrors();
            }
            bool truncated = false;
            var (unchangedIdentical, onlyListedDiffer, unchangedCompared) = CompareBlocks(category, view.Files, inNew, inPublished, diffs, ref truncated, token);

            // 6. every listed set, taken out of the new file as an extracted .img, against the input snapshot
            int filesCompared = 0;
            var openErrors = new List<string>();
            var unsupported = new List<string>();
            int brokenLinks = 0;
            foreach (var file in sets)
            {
                token.ThrowIfCancellationRequested();
                if (!inNew.ContainsKey(file.WzPath(category))) continue;   // already reported as missing
                var (opened, broken, formats) = Inspect(newWz, category, file.Img);
                if (!opened) openErrors.Add(file.Img);
                brokenLinks += broken;
                foreach (var format in formats) unsupported.Add(file.Img + ": format " + format);
            }
            if (sets.Count > 0 && !token.IsCancellationRequested)
            {
                var mine = new List<PackerImage>();
                var theirs = new List<PackerImage>();
                var extractedRoot = Path.Combine(tempBase, "img");
                foreach (var file in sets)
                {
                    token.ThrowIfCancellationRequested();
                    if (!inNew.ContainsKey(file.WzPath(category)))
                        continue;   // already reported as missing
                    var bytes = ExtractFromNew(newWz, category, file.Img);
                    if (bytes == null)
                    {
                        diffs.Add(new PackerDiff(file.Img, "unreadable", "image"));
                        continue;
                    }
                    var target = Path.Combine(extractedRoot, file.Img);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, bytes);
                    mine.Add(new PackerImage(file.Img, file.SizeBytes ?? 0));
                    theirs.Add(new PackerImage(file.Img, bytes.Length));
                }

                var roots = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["canon"] = inputRoot, ["owner"] = tempBase, ["friend"] = tempBase,
                };
                var viewerOptions = ViewerOptionsFor(roots);
                var resolver = new ViewerPathResolver(roots, _files);
                var cache = new ViewerImageCache(viewerOptions.CacheBytes);
                try
                {
                    var loader = new ViewerImageLoader(viewerOptions, _files, cache);
                    var diff = new ViewerDiffService(loader, new ViewerBaseMap(viewerOptions, _files), viewerOptions);
                    (filesCompared, var semanticTruncated) = Compare(mine, theirs, resolver, diff, diffs, token);
                    truncated |= semanticTruncated;
                }
                finally
                {
                    cache.Clear();
                    ErrorLogger.ClearErrors();
                }
            }

            bool identical = unchangedIdentical && onlyListedDiffer && diffs.Count == 0 && !truncated;
            return new PackerVerifyResult(identical, unchangedIdentical, onlyListedDiffer, newWz.Version, Key, unchangedCompared, filesCompared, diffs, truncated)
            {
                OpenErrors = openErrors.Take(_options.MaxDiffs).ToArray(),
                BrokenLinks = brokenLinks,
                UnsupportedFormats = unsupported.Take(_options.MaxDiffs).ToArray(),
            };
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ViewerApiException(504, "timeout", "the verify took longer than the time budget");
        }
        finally
        {
            try { Directory.Delete(tempBase, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Compares the raw blocks of the new file with the published file's. An image the pack did not list must be in both and equal
    /// (<c>unchangedIdentical</c>); outside the list nothing may appear or vanish, a listed set must be in the new file and a listed
    /// remove must be gone (<c>onlyListedDiffer</c>). What differs goes into <paramref name="diffs"/> (at most MaxDiffs).
    /// </summary>
    private (bool UnchangedIdentical, bool OnlyListedDiffer, int Compared) CompareBlocks(string category, IReadOnlyList<PackerFileResult> files,
        Dictionary<string, PackerBlock> inNew, Dictionary<string, PackerBlock> inPublished, List<PackerDiff> diffs, ref bool truncated, CancellationToken token)
    {
        var listed = new Dictionary<string, PackerFileResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            listed[file.WzPath(category)] = file;

        bool unchangedIdentical = true, onlyListedDiffer = true;
        int compared = 0;
        foreach (var path in inNew.Keys.Union(inPublished.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            bool here = inNew.TryGetValue(path, out var now), there = inPublished.TryGetValue(path, out var before);
            var name = category + "/" + path;
            string? kind = null;
            if (!listed.TryGetValue(path, out var file))
            {
                compared++;
                if (there && !here) { unchangedIdentical = false; onlyListedDiffer = false; kind = "removed"; }
                else if (here && !there) { onlyListedDiffer = false; kind = "added"; }
                else if (now!.Sha256 != before!.Sha256 || now.Size != before.Size || now.Checksum != before.Checksum) { unchangedIdentical = false; kind = "changed"; }
            }
            else if (file.Op == PackerFile.Set && !here)
            {
                onlyListedDiffer = false;
                kind = "missing";
            }
            else if (file.Op == PackerFile.Remove && here)
            {
                onlyListedDiffer = false;
                kind = "not_removed";
            }
            if (kind == null) continue;
            if (diffs.Count >= _options.MaxDiffs) { truncated = true; continue; }
            diffs.Add(new PackerDiff(name, kind, "image"));
        }
        return (unchangedIdentical, onlyListedDiffer, compared);
    }

    /// <summary>The picture formats the v83 client can show: 4444 (1), 8888 (2), 1555 (257), 565 (513, 517). Format 3 and the compressed ones (DXT 1026, 2050, 4098) are not among them.</summary>
    private static readonly HashSet<int> SupportedFormats = new() { 1, 2, 257, 513, 517 };

    /// <summary>
    /// Checks one listed image of the new file (check 4 and 5): does it open, how many of its links (UOL) lead nowhere, and which picture
    /// formats it holds that the client cannot show. An image that does not open reports (false, 0, empty).
    /// </summary>
    private static (bool Opened, int BrokenLinks, IReadOnlyList<int> UnsupportedFormats) Inspect(WzFile wz, string category, string img)
    {
        try
        {
            var (_, image) = FindImage(wz.WzDirectory, img[(category.Length + 1)..].Split('/'), img);
            if (image == null || !image.ParseImage())
                return (false, 0, Array.Empty<int>());
            int broken = 0;
            var formats = new SortedSet<int>();
            Walk(image.WzProperties, ref broken, formats);
            return (true, broken, formats.ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, 0, Array.Empty<int>());
        }
        finally
        {
            ErrorLogger.ClearErrors();
        }

        static void Walk(IEnumerable<WzImageProperty> properties, ref int broken, SortedSet<int> formats)
        {
            foreach (var property in properties)
            {
                switch (property)
                {
                    case WzUOLProperty uol when !UolResolves(uol): broken++; break;
                    case WzCanvasProperty canvas:
                        if (!SupportedFormats.Contains((int)canvas.PngProperty.Format)) formats.Add((int)canvas.PngProperty.Format);
                        break;
                }
                if (property.WzProperties is { Count: > 0 } children)
                    Walk(children, ref broken, formats);
            }
        }
    }

    /// <summary>Follows a UOL the way the library does (a path of names and "..", starting at the property's folder, or at the image when it does not begin with ".."). False when a step finds nothing.</summary>
    private static bool UolResolves(WzUOLProperty uol)
    {
        if (string.IsNullOrEmpty(uol.Value) || uol.Parent == null) return false;
        var paths = uol.Value.Split('/');
        WzObject? current = uol.Parent;
        if (paths[0] != "..")
            while (current != null && current is not WzImage) current = current.Parent;   // the image that holds the link
        foreach (var step in paths)
        {
            if (current == null) return false;
            if (step == "..") current = current.Parent;
            else if (current is WzImageProperty p) current = p[step];
            else if (current is WzImage i) current = i[step];
            else return false;
        }
        return current != null;
    }

    /// <summary>One image of the new file as an extracted .img (zero key, BMS), the form of the input snapshot. Null when it does not parse.</summary>
    private static byte[]? ExtractFromNew(WzFile wz, string category, string img)
    {
        try
        {
            var (_, image) = FindImage(wz.WzDirectory, img[(category.Length + 1)..].Split('/'), img);
            if (image == null || !image.ParseImage())
                return null;
            return WzImgSerializer.CreateForImgExtraction().SerializeImage(image);
        }
        catch (PackerJobException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            ErrorLogger.ClearErrors();
        }
    }

    private static bool HasLinkBelow(string imgRoot, string relative)
    {
        var current = imgRoot;
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            if (new FileInfo(current).LinkTarget != null) return true;
        }
        return false;
    }

    /// <summary>
    /// The viewer settings verify runs its diffs with. The viewer's own limits on how many canvases (200) and how many decoded bytes
    /// (256 MiB) one comparison may decode would make a correct repack of a big image read as <c>pixels_unverified</c> as soon as the
    /// compressed bytes differ, so they are lifted here; the time budget is what bounds the work.
    /// </summary>
    public ViewerOptions ViewerOptionsFor(IReadOnlyDictionary<string, string> roots) => new("v" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), roots)
    {
        MaxFileBytes = _options.MaxImageBytes,
        CacheBytes = 128L * 1024 * 1024,
        MaxChanges = Math.Min(_options.MaxDiffs, ViewerOptions.DefaultMaxChanges),
        DiffBudget = _options.TimeBudget,
        MaxCanvasDecodes = 1_000_000,
        MaxDecodedBytes = 256L * 1024 * 1024 * 1024,
    };

    private static ViewerApiException ChangedOutput() => new(409, "output_changed", "the .wz of the job is not the file the pack made");

    private static ViewerApiException InputChanged() => new(409, "input_changed", "the input snapshot of the job is not what the pack read");

    // ------------------------------------------------- start-up and space

    public const string TempPrefix = "wzpacker-";
    private static readonly Regex TempFolderPattern = new("^wzpacker-[0-9a-f]{16}$", RegexOptions.CultureInvariant);

    /// <summary>Test seam: free bytes on the volume that holds a path. The default asks the file system.</summary>
    public Func<string, long> FreeSpace { get; set; } = path => new DriveInfo(path).AvailableFreeSpace;

    private int CountJobFolders()
    {
        try { return Directory.EnumerateDirectories(_options.Outbox).Take(_options.MaxJobFolders).Count(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ViewerApiException(503, "outbox_unavailable", "the outbox cannot take a new job"); }
    }

    /// <summary>
    /// Run once at start, before any request: a process that died leaves a <c>.work</c> folder in a job folder and a
    /// <c>wzpacker-*</c> folder in the temp root. Nothing else is touched (job folders and their .wz files stay).
    /// A link with one of these names is removed as a link, never followed.
    /// </summary>
    public void CleanStale()
    {
        try
        {
            foreach (var job in Directory.EnumerateDirectories(_options.Outbox))
            {
                if (PackerJobStore.IsJobId(Path.GetFileName(job)) && new DirectoryInfo(job).LinkTarget == null)
                    RemoveWorkFolder(Path.Combine(job, ".work"));
            }
            if (Directory.Exists(_options.TempRoot))
                foreach (var dir in Directory.EnumerateFileSystemEntries(_options.TempRoot))
                {
                    if (!TempFolderPattern.IsMatch(Path.GetFileName(dir)))
                        continue;
                    if (new DirectoryInfo(dir).LinkTarget != null) File.Delete(dir);
                    else Directory.Delete(dir, recursive: true);
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("cleaning stale folders failed: {ExceptionType}", ex.GetType().Name);
        }
    }

    /// <summary>Compares each listed image of the snapshot (<c>canon</c>) with the one taken out of the new file (<c>owner</c>). Returns how many were compared and whether the list was cut short.</summary>
    private (int Compared, bool Truncated) Compare(List<PackerImage> mine, List<PackerImage> theirs, ViewerPathResolver resolver, ViewerDiffService diff, List<PackerDiff> diffs, CancellationToken token)
    {
        var inMine = mine.ToDictionary(i => i.RelativePath, StringComparer.Ordinal);
        var inWz = theirs.ToDictionary(i => i.RelativePath, StringComparer.Ordinal);
        var all = inMine.Keys.Union(inWz.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
        int compared = 0;
        bool truncated = false;

        foreach (var path in all)
        {
            if (token.IsCancellationRequested)
            {
                truncated = true;   // out of time or the caller went away: what was found so far, flagged as incomplete
                break;
            }
            if (diffs.Count >= _options.MaxDiffs) { truncated = true; break; }
            compared++;

            DiffResponse response;
            try
            {
                var before = resolver.Resolve("canon", path);
                var now = resolver.Resolve("owner", path);
                response = diff.Diff("owner", before, now, token);
            }
            catch (ViewerApiException)
            {
                diffs.Add(new PackerDiff(path, "unreadable", "image"));   // does not parse, too large, name refused: it cannot be shown to be equal
                continue;
            }
            catch (OperationCanceledException)
            {
                truncated = true;
                break;
            }

            if (response.Semantic == "identical")
                continue;

            int before0 = diffs.Count;
            foreach (var change in response.Changes)
            {
                if (diffs.Count >= _options.MaxDiffs) { truncated = true; break; }
                diffs.Add(new PackerDiff(path + ":" + change.Path, change.Kind, change.Type));
            }
            // "identical_visible" is a difference here unless it is only linked canvases (compared by their link text, which is
            // what a repack must keep). A canvas that differs only in the colour under fully transparent pixels is a real difference.
            foreach (var canvas in response.Canvases.Where(c => c.Change == "hidden_color"))
            {
                if (diffs.Count >= _options.MaxDiffs) { truncated = true; break; }
                diffs.Add(new PackerDiff(path + ":" + canvas.Path, "changed", "canvas"));
            }
            if (response.Truncated)
                truncated = true;
            if (response.Semantic == "different" && diffs.Count == before0)
                diffs.Add(new PackerDiff(path, "changed", "image"));   // different, but the list was cut before anything was named
        }

        return (compared, truncated);
    }

    // ------------------------------------------------------------- helpers

    private static ViewerApiException SourceUnavailable() => new(503, "source_unavailable", "the source is not available; retry later");

    private static ViewerApiException Busy() => new(503, "busy", "a pack or verify is already running; retry shortly");

    /// <summary>Removes anything that looks like an absolute path from a text that came from the library, and caps its length.</summary>
    private string Scrub(string text)
    {
        foreach (var path in _options.Roots.Values.Append(_options.Outbox).Append(_options.TempRoot).OrderByDescending(p => p.Length))
            text = text.Replace(path, "<path>", StringComparison.Ordinal);
        text = PathLike.Replace(text, "<path>");
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > 300 ? text[..300] : text;
    }
}
