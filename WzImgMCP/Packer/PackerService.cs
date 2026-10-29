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

    /// <summary>Test seam: called with "start" (before the work folder is made), "packing" (just before the library packs) and "verify" with the job id.</summary>
    public Action<string, string>? Probe { get; set; }

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
        using (lease)   // the slot is given back when the work has really ended, whatever the request did
        {
            using var cts = new CancellationTokenSource(_options.TimeBudget);
            var jobDir = _jobs.JobDirectory(job.Id);
            var work = Path.Combine(jobDir, ".work");
            _logger.LogInformation("pack {JobId} running", job.Id);
            try
            {
                job.MarkRunning();
                _jobs.Persist(job);
                Probe?.Invoke("start", job.Id);

                var images = PackerTree.Enumerate(imgRoot, job.Category, _files, _options, cts.Token);
                if (images.Count == 0)
                    throw new PackerJobException("empty_category", "the category holds no .img files");

                MakeFreshFolder(work, jobDir);
                Probe?.Invoke("packing", job.Id);
                cts.Token.ThrowIfCancellationRequested();

                var packer = new WzPackingService();
                var result = packer.PackCategoriesAsync(imgRoot, work, new[] { job.Category }, saveAs64Bit: false, cts.Token, progress: null,
                    overridePatchVersion: WzVersion, separateCanvas: false, overrideEncryption: WzMapleVersion.GMS).GetAwaiter().GetResult();
                cts.Token.ThrowIfCancellationRequested();

                result.CategoriesPacked.TryGetValue(job.Category, out var packed);
                var problems = new List<string>();
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage)) problems.Add(result.ErrorMessage);
                if (packed != null) problems.AddRange(packed.Errors);
                if (!result.Success || packed == null || !packed.Success || problems.Count > 0)
                {
                    foreach (var problem in problems.Take(10))
                        job.AddWarning(Scrub(problem));
                    throw new PackerJobException("pack_failed", "packing failed; the warnings say why");
                }

                var produced = Path.Combine(work, job.Category + ".wz");
                if (!_files.IsRegularFile(produced))
                    throw new PackerJobException("pack_failed", "packing produced no .wz file");
                var size = new FileInfo(produced).Length;
                if (size > _options.MaxWzBytes)
                    throw new PackerJobException("output_too_large", "the packed .wz is larger than the packer allows");
                if (packed.ImagesPacked != images.Count)
                    job.AddWarning($"{images.Count} image file(s) found, {packed.ImagesPacked} packed");

                var final = Path.Combine(jobDir, job.Category + ".wz");
                File.Move(produced, final, overwrite: false);   // a rename inside the job folder; it never writes through a link
                var sha = Sha256Of(final);
                job.Complete(new PackerWz(job.Category + ".wz", size, sha), packed.ImagesPacked);
                _logger.LogInformation("pack {JobId} done", job.Id);
            }
            catch (OperationCanceledException)
            {
                job.Fail("timeout", "the pack took longer than the time budget");
                _logger.LogWarning("pack {JobId} failed: timeout", job.Id);
            }
            catch (PackerJobException ex)
            {
                job.Fail(ex.Code, ex.Message);
                _logger.LogWarning("pack {JobId} failed: {Code}", job.Id, ex.Code);
            }
            catch (Exception ex)
            {
                job.Fail("internal_error", "the pack failed unexpectedly");
                _logger.LogError("pack {JobId} failed: unhandled exception of type {ExceptionType}", job.Id, ex.GetType().Name);
            }
            finally
            {
                ErrorLogger.ClearErrors();   // MapleLib keeps every logged problem in a list that only grows
                RemoveWorkFolder(work);
                _jobs.Persist(job);
            }
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

    // -------------------------------------------------------------- verify

    /// <summary>
    /// Extracts the job's .wz again and compares every .img of it with the person's copy by value, picture and link text
    /// (the viewer's diff engine). Synchronous; the caller holds the work gate and runs it on its own thread.
    /// </summary>
    public PackerVerifyResult Verify(string jobId, string person, CancellationToken cancellationToken)
    {
        var view = _jobs.Find(jobId) ?? throw new ViewerApiException(404, "not_found", "no such job");
        if (view.State != PackerJob.Done || view.Wz == null)
            throw new ViewerApiException(409, "job_not_done", "the job has not produced a .wz");

        var category = view.Category;
        var wzPath = Path.Combine(_jobs.JobDirectory(jobId), view.Wz.Name);
        if (new DirectoryInfo(_jobs.JobDirectory(jobId)).LinkTarget != null
            || new FileInfo(wzPath).LinkTarget != null
            || !_files.IsRegularFile(wzPath))
            throw new ViewerApiException(409, "output_changed", "the .wz of the job is not the file the pack made");
        // The file that is verified must be the file the pack recorded (and so the file the publisher will take).
        if (new FileInfo(wzPath).Length != view.Wz.SizeBytes || !string.Equals(Sha256Of(wzPath), view.Wz.Sha256, StringComparison.Ordinal))
            throw new ViewerApiException(409, "output_changed", "the .wz of the job is not the file the pack made");

        var imgRoot = ImgRoot(person);
        if (!Directory.Exists(imgRoot) || new DirectoryInfo(imgRoot).LinkTarget != null)
            throw SourceUnavailable();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.TimeBudget);
        var token = budget.Token;
        var diffs = new List<PackerDiff>();
        var tempBase = Path.Combine(_options.TempRoot, "wzpacker-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());
        try
        {
            Directory.CreateDirectory(tempBase);
            Probe?.Invoke("verify", jobId);

            // 1. extract the .wz again, into <temp>/img
            var wzImgRoot = Path.Combine(tempBase, "img");
            bool extracted;
            try
            {
                var extractor = new WzExtractionService();
                var result = extractor.ExtractAsync(Path.GetDirectoryName(wzPath)!, wzImgRoot, "verify", "verify", WzMapleVersion.GMS,
                    new List<string> { category }, resolveLinks: false, token).GetAwaiter().GetResult();
                extracted = result.Success && result.CategoriesExtracted.Values.All(c => c.Success && c.Errors.Count == 0)
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
            if (!extracted)
            {
                diffs.Add(new PackerDiff(view.Wz.Name, "unreadable", "wz"));
                return new PackerVerifyResult(false, 0, diffs, false);
            }

            // 2. list both sides
            List<PackerImage> mine, theirs;
            try
            {
                mine = PackerTree.Enumerate(imgRoot, category, _files, _options, token);
            }
            catch (PackerJobException ex)
            {
                throw new ViewerApiException(409, ex.Code, ex.Message);
            }
            try
            {
                theirs = PackerTree.Enumerate(wzImgRoot, category, _files, _options, token);
            }
            catch (PackerJobException)
            {
                diffs.Add(new PackerDiff(view.Wz.Name, "unreadable", "wz"));
                return new PackerVerifyResult(false, 0, diffs, false);
            }

            // 3. compare one .img at a time with the viewer's engine; "canon" is the person's copy (before), "owner" the re-extracted .wz (now)
            var roots = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["canon"] = _options.Roots[person], ["owner"] = tempBase, ["friend"] = tempBase,
            };
            var viewerOptions = new ViewerOptions("v" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), roots)
            {
                MaxFileBytes = _options.MaxImageBytes,
                CacheBytes = 128L * 1024 * 1024,
                MaxChanges = Math.Min(_options.MaxDiffs, ViewerOptions.DefaultMaxChanges),
                DiffBudget = _options.TimeBudget,
            };
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
        finally
        {
            try { Directory.Delete(tempBase, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
