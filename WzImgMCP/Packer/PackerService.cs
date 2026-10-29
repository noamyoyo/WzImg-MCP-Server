using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MapleLib.Helpers;
using MapleLib.Img;
using MapleLib.WzLib;
using Microsoft.Extensions.Logging;
using WzImgMCP.Viewer;
using WzImgMCP.Viewer.Diff;

namespace WzImgMCP.Packer;

/// <summary>One difference found by verify. <c>Path</c> is the .img path (<c>String/Pet.img</c>), or, for a value inside it, the .img path, a colon and the path of the value.</summary>
public sealed record PackerDiff(string Path, string Kind, string Type);

/// <summary>The answer of <c>POST /v1/verify</c>.</summary>
public sealed record PackerVerifyResult(bool Identical, int FilesCompared, IReadOnlyList<PackerDiff> Diffs, bool Truncated);

/// <summary>
/// Packs a category of a person's copy into a .wz in the outbox, and checks a packed .wz by extracting it again and comparing
/// it with the person's copy. Packing calls the library (<c>WzPackingService</c>, the code behind the <c>pack_to_wz</c> tool, not
/// the tool class), with wz version 83 and the key GMS.
///
/// Writes: only below <c>&lt;outbox&gt;/&lt;jobId&gt;</c> (a fresh folder per job, the pack itself in a fresh <c>.work</c> folder below it,
/// the finished file moved up in one rename) and, for verify, in a folder of its own in the temp root that is removed again.
/// The trees are only read. Responses, job texts and logs hold no absolute path: texts from the library are scrubbed.
/// </summary>
public sealed class PackerService
{
    public const int WzVersion = 83;
    public const string Key = "GMS";

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

    /// <summary>Test seam: called with "start" (before the work folder is made), "packing" (just before the library packs), "verify" (before the .wz is copied) and "copied" (after it was copied and hashed), with the job id.</summary>
    public Action<string, string>? Probe { get; set; }

    /// <summary>Test seam: sees (and may change) the library's result for the category before the packer judges it.</summary>
    public Action<CategoryPackingResult>? PackedProbe { get; set; }

    // ---------------------------------------------------------------- pack

    /// <summary>Checks the request, takes the one pack slot, and starts the job on its own thread. Throws a typed <see cref="ViewerApiException"/>.</summary>
    public PackerJobView StartPack(string person, string category)
    {
        PackerTree.RequireCategory(category);
        var imgRoot = ImgRoot(person);
        var categoryDir = Path.Combine(imgRoot, category);

        if (new DirectoryInfo(imgRoot).LinkTarget != null)
            throw new ViewerApiException(400, "bad_path", "the source folder is a symbolic link");
        if (!Directory.Exists(imgRoot) || !ViewerPathResolver.IsReadableDirectory(imgRoot))
            throw SourceUnavailable();
        switch (_files.Probe(categoryDir))
        {
            case PathState.Unavailable: throw SourceUnavailable();
            case PathState.Absent: throw new ViewerApiException(404, "not_found", "category not found");
        }
        if (new DirectoryInfo(categoryDir).LinkTarget != null)
            throw new ViewerApiException(400, "bad_path", "the category folder is a symbolic link");
        if (!Directory.Exists(categoryDir))
            throw new ViewerApiException(404, "not_found", "category not found");

        if (CountJobFolders() >= _options.MaxJobFolders)
            throw new ViewerApiException(503, "outbox_full", "the outbox holds too many job folders; the publisher must clean it up");

        var lease = _gate.TryEnter() ?? throw Busy();
        PackerJob job;
        try
        {
            job = _jobs.Create(person, category);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        var view = job.View();   // "queued", as the caller sees it
        _ = Task.Run(() => RunPack(job, imgRoot, lease));
        return view;
    }

    private void RunPack(PackerJob job, string imgRoot, PackerWorkGate.Lease lease)
    {
        using var cts = new CancellationTokenSource(_options.TimeBudget);
        var jobDir = _jobs.JobDirectory(job.Id);
        var work = Path.Combine(jobDir, ".work");
        _logger.LogInformation("pack {JobId} running", job.Id);
        try
        {
            try
            {
                job.MarkRunning();
                _jobs.Persist(job);
                Probe?.Invoke("start", job.Id);

                var images = PackerTree.Enumerate(imgRoot, job.Category, _files, _options, cts.Token);
                if (images.Count == 0)
                    throw new PackerJobException("empty_category", "the category holds no .img files");
                // The .wz will be about as big as the images; the outbox disk must have room for it before the library starts writing.
                var expected = Math.Min(images.Sum(i => i.SizeBytes), _options.MaxWzBytes);
                if (FreeSpace(_options.Outbox) < expected)
                    throw new PackerJobException("insufficient_space", "the outbox does not have room for the .wz");

                MakeFreshFolder(work, jobDir);
                Probe?.Invoke("packing", job.Id);
                cts.Token.ThrowIfCancellationRequested();

                var packer = new WzPackingService();
                var result = packer.PackCategoriesAsync(imgRoot, work, new[] { job.Category }, saveAs64Bit: false, cts.Token, progress: null,
                    overridePatchVersion: WzVersion, separateCanvas: false, overrideEncryption: WzMapleVersion.GMS).GetAwaiter().GetResult();
                cts.Token.ThrowIfCancellationRequested();

                result.CategoriesPacked.TryGetValue(job.Category, out var packed);
                if (packed != null) PackedProbe?.Invoke(packed);
                var problems = new List<string>();
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage)) problems.Add(result.ErrorMessage);
                if (packed != null) problems.AddRange(packed.Errors);
                if (!result.Success || packed == null || !packed.Success || problems.Count > 0)
                {
                    foreach (var problem in problems.Take(10))
                        job.AddWarning(Scrub(problem));
                    throw new PackerJobException("pack_failed", "packing failed; the warnings say why");
                }
                // A .wz with fewer images than the category has must never read as done: a caller that looks only at the state would publish it.
                if (packed.ImagesPacked != images.Count)
                {
                    job.AddWarning($"{images.Count} image file(s) found, {packed.ImagesPacked} packed");
                    throw new PackerJobException("pack_failed", "packing did not pack every image; the warnings say how many");
                }

                var produced = Path.Combine(work, job.Category + ".wz");
                if (!_files.IsRegularFile(produced))
                    throw new PackerJobException("pack_failed", "packing produced no .wz file");
                var size = new FileInfo(produced).Length;
                if (size > _options.MaxWzBytes)
                    throw new PackerJobException("output_too_large", "the packed .wz is larger than the packer allows");

                var final = Path.Combine(jobDir, job.Category + ".wz");
                File.Move(produced, final, overwrite: false);   // a rename inside the job folder; it never writes through a link
                var sha = Sha256Of(final);
                job.StageDone(new PackerWz(job.Category + ".wz", size, sha), packed.ImagesPacked);
            }
            catch (OperationCanceledException)
            {
                job.StageFailed("timeout", "the pack took longer than the time budget");
            }
            catch (PackerJobException ex)
            {
                job.StageFailed(ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                job.StageFailed("internal_error", "the pack failed unexpectedly");
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
            _jobs.Persist(final);
            lease.Dispose();
            job.Publish();
            if (final.State == PackerJob.Done) _logger.LogInformation("pack {JobId} done", job.Id);
            else _logger.LogWarning("pack {JobId} failed: {Code}", job.Id, final.Error?.Code);
        }
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

    private static string Sha256Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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

    /// <summary>True when the file opens with wz version 83 and the key GMS fixed (the extractor on its own detects any version).</summary>
    private static bool OpensAsVersion83Gms(string path)
    {
        try
        {
            using var wz = new WzFile(path, (short)WzVersion, WzMapleVersion.GMS);
            return wz.ParseWzFile() == WzFileParseStatus.Success && wz.Version == WzVersion;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            ErrorLogger.ClearErrors();
        }
    }

    // -------------------------------------------------------------- verify

    /// <summary>
    /// Checks <c>&lt;outbox&gt;/&lt;jobId&gt;/&lt;Category&gt;.wz</c> (the file the publisher will take; the name is never read from the record):
    /// it is copied once into the temp folder, hashed while it is copied (the hash must be the one the pack recorded), opened with
    /// version 83 and key GMS fixed, extracted again, and every .img of it is compared with the person's copy by value, picture and link
    /// text (the viewer's diff engine). Synchronous; the caller holds the work gate and runs it on its own thread.
    /// </summary>
    public PackerVerifyResult Verify(string jobId, string person, CancellationToken cancellationToken)
    {
        var view = _jobs.Find(jobId) ?? throw new ViewerApiException(404, "not_found", "no such job");
        if (view.State != PackerJob.Done || view.Wz == null)
            throw new ViewerApiException(409, "job_not_done", "the job has not produced a .wz");

        var category = view.Category;   // checked as a plain name when the record was loaded
        var wzName = category + ".wz";
        var jobDir = _jobs.JobDirectory(jobId);
        var wzPath = Path.Combine(jobDir, wzName);
        if (new DirectoryInfo(jobDir).LinkTarget != null
            || new FileInfo(wzPath).LinkTarget != null
            || !_files.IsRegularFile(wzPath))
            throw ChangedOutput();

        var imgRoot = ImgRoot(person);
        if (!Directory.Exists(imgRoot) || new DirectoryInfo(imgRoot).LinkTarget != null)
            throw SourceUnavailable();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.TimeBudget);
        var token = budget.Token;
        var diffs = new List<PackerDiff>();
        var tempBase = Path.Combine(_options.TempRoot, TempPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());
        try
        {
            // 1. the person's copy, listed (a failure here is about the copy, not the .wz)
            List<PackerImage> mine;
            try
            {
                mine = PackerTree.Enumerate(imgRoot, category, _files, _options, token);
            }
            catch (PackerJobException ex)
            {
                throw new ViewerApiException(409, ex.Code, ex.Message);
            }

            // 2. room in the temp folder for the copy of the .wz and for what it extracts to (about the size of the copy of the person)
            var needed = view.Wz.SizeBytes + mine.Sum(i => i.SizeBytes);
            Directory.CreateDirectory(_options.TempRoot);
            if (FreeSpace(_options.TempRoot) < needed)
                throw new ViewerApiException(503, "insufficient_space", "the temp folder does not have room to verify this .wz");

            // 3. copy the .wz once; everything below works on the copy, so a writer of the outbox cannot swap the file in between
            var copyDir = Path.Combine(tempBase, "src");
            Directory.CreateDirectory(copyDir);
            var copy = Path.Combine(copyDir, wzName);
            Probe?.Invoke("verify", jobId);
            var (copySize, copySha) = CopyAndHash(wzPath, copy, token);
            if (copySize != view.Wz.SizeBytes || !string.Equals(copySha, view.Wz.Sha256, StringComparison.Ordinal))
                throw ChangedOutput();   // not the file the pack recorded (and so not the file that would be published)
            Probe?.Invoke("copied", jobId);

            // 4. it must open as version 83 with the key GMS, then be extracted again into <temp>/img
            if (!OpensAsVersion83Gms(copy))
            {
                diffs.Add(new PackerDiff(wzName, "unreadable", "wz"));
                return new PackerVerifyResult(false, 0, diffs, false);
            }
            var wzImgRoot = Path.Combine(tempBase, "img");
            bool extracted;
            try
            {
                var extractor = new WzExtractionService();
                var result = extractor.ExtractAsync(copyDir, wzImgRoot, "verify", "verify", WzMapleVersion.GMS,
                    new List<string> { category }, resolveLinks: false, token).GetAwaiter().GetResult();
                extracted = result.Success && result.CategoriesExtracted.Values.All(c => c.Success && c.Errors.Count == 0 && c.DetectedPatchVersion == WzVersion)
                    && Directory.Exists(Path.Combine(wzImgRoot, category));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                extracted = false;
            }
            finally
            {
                ErrorLogger.ClearErrors();
            }
            token.ThrowIfCancellationRequested();   // out of time while extracting: not a statement about the .wz
            if (!extracted)
            {
                diffs.Add(new PackerDiff(wzName, "unreadable", "wz"));
                return new PackerVerifyResult(false, 0, diffs, false);
            }

            // 5. list the extracted side
            List<PackerImage> theirs;
            try
            {
                theirs = PackerTree.Enumerate(wzImgRoot, category, _files, _options, token);
            }
            catch (PackerJobException)
            {
                diffs.Add(new PackerDiff(wzName, "unreadable", "wz"));
                return new PackerVerifyResult(false, 0, diffs, false);
            }

            // 6. compare one .img at a time with the viewer's engine; "canon" is the person's copy (before), "owner" the re-extracted .wz (now)
            var roots = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["canon"] = _options.Roots[person], ["owner"] = tempBase, ["friend"] = tempBase,
            };
            var viewerOptions = ViewerOptionsFor(roots);
            var resolver = new ViewerPathResolver(roots, _files);
            var cache = new ViewerImageCache(viewerOptions.CacheBytes);
            try
            {
                var loader = new ViewerImageLoader(viewerOptions, _files, cache);
                var diff = new ViewerDiffService(loader, new ViewerBaseMap(viewerOptions, _files), viewerOptions);
                return Compare(mine, theirs, resolver, diff, diffs, token);
            }
            finally
            {
                cache.Clear();
                ErrorLogger.ClearErrors();
            }
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

    private PackerVerifyResult Compare(List<PackerImage> mine, List<PackerImage> theirs, ViewerPathResolver resolver, ViewerDiffService diff, List<PackerDiff> diffs, CancellationToken token)
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

            if (!inWz.ContainsKey(path)) { diffs.Add(new PackerDiff(path, "removed", "image")); continue; }
            if (!inMine.ContainsKey(path)) { diffs.Add(new PackerDiff(path, "added", "image")); continue; }

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

        return new PackerVerifyResult(diffs.Count == 0 && !truncated, compared, diffs, truncated);
    }

    // ------------------------------------------------------------- helpers

    private string ImgRoot(string person)
        => Path.Combine(_options.Roots[person], "img");

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
