using System.Text.Json;
using System.Text.RegularExpressions;
using MapleLib.Img;
using MapleLib.WzLib;
using WzImgMCP.Server;
using WzImgMCP.Utils;

namespace WzImgMCP.Import;

/// <summary>
/// Import by link (roadmap T21). The editor container downloads a file from an allowed link, stages it under
/// <c>&lt;allowed root&gt;/inbox/&lt;id&gt;/</c> and, for a WZ file, compares it with the tree, applies it after a confirmation and can undo it.
///
/// Rules that hold for every step:
/// <list type="bullet">
/// <item>ids are made by the server (128 random bits) and checked against a strict pattern on every call; no part of a path comes from the caller;</item>
/// <item>everything is confined to the allowed root with <see cref="PathGuard"/> and refused when a symbolic link is on the way;</item>
/// <item>one download at a time, at most <see cref="ImportOptions.MaxStaged"/> staged imports, deleted after <see cref="ImportOptions.Retention"/>;</item>
/// <item>nothing here starts a process; errors are stable codes with plain sentences, never paths, URLs with keys, or stack traces.</item>
/// </list>
/// State of each import is kept in <c>state.json</c> in its folder, so a restart of the server does not lose an applied import's undo.
/// </summary>
public sealed partial class ImportService : IDisposable
{
    private static readonly Regex IdPattern = new("^[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex HashPattern = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);
    private const long MaxStateBytes = 4 * 1024 * 1024;

    private readonly WzSessionManager _session;
    private readonly ImportOptions _options;
    private readonly TimeProvider _time;
    private readonly ImportDownloader _downloader;
    private readonly object _gate = new();
    private readonly Dictionary<string, ImportRecord> _records = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new();
    private readonly Dictionary<string, Task> _jobs = new();
    private readonly SemaphoreSlim _step = new(1, 1);
    private string? _activeDownloadId;
    private bool _loaded;
    private bool _disposed;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Used by the server (dependency injection): settings from the environment, the real network, the system clock.</summary>
    public ImportService(WzSessionManager session)
        : this(session, ImportOptions.FromEnvironment(), TimeProvider.System, ImportDownloader.SystemResolver, null)
    {
    }

    internal ImportService(WzSessionManager session, ImportOptions options, TimeProvider time, HostResolver resolver, Func<HttpMessageHandler>? handlerFactory)
    {
        _session = session;
        _options = options;
        _time = time;
        _downloader = new ImportDownloader(options, handlerFactory ?? (() => ImportDownloader.CreateGuardedHandler(resolver)), resolver);
    }

    public ImportOptions Options => _options;

    #region Paths and ids

    private string InboxRoot => Path.Combine(ImportFs.RequireRoot(), "inbox");
    private string LockPath => Path.Combine(ImportFs.RequireRoot(), ".import-lock");
    private string DirOf(string id) => Path.Combine(InboxRoot, id);
    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static string RequireId(string? id)
    {
        if (id == null || !IdPattern.IsMatch(id))
            throw new ImportException("bad_id", "That is not an import id. Use the id import_fetch returned.");
        return id;
    }

    private string EnsureInbox()
    {
        var root = ImportFs.RequireRoot();
        var inbox = Path.Combine(root, "inbox");
        ImportFs.RequireNoLinks(inbox);
        if (!Directory.Exists(inbox))
            Directory.CreateDirectory(inbox);
        return inbox;
    }

    #endregion

    #region Records

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            string inbox;
            try { inbox = InboxRoot; } catch (ImportException) { return; }
            if (!Directory.Exists(inbox)) return;
            foreach (var dir in Directory.EnumerateDirectories(inbox))
            {
                var id = Path.GetFileName(dir);
                if (!IdPattern.IsMatch(id)) continue;
                var statePath = Path.Combine(dir, "state.json");
                try
                {
                    var info = new FileInfo(statePath);
                    if (!info.Exists || info.LinkTarget != null || info.Length > MaxStateBytes) continue;
                    var record = JsonSerializer.Deserialize<ImportRecord>(File.ReadAllText(statePath), Json);
                    if (record == null || record.Id != id) continue;
                    // a step that was running when the server stopped did not finish
                    if (record.Status is ImportStatus.Downloading or ImportStatus.Extracting)
                    {
                        record.Status = ImportStatus.Failed;
                        record.ErrorCode = "interrupted";
                        record.Error = "The server was restarted while this import was running. Start it again.";
                    }
                    else if (record.Status == ImportStatus.Applying)
                    {
                        record.Status = ImportStatus.ApplyInterrupted;
                    }
                    _records[id] = record;
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // an unreadable state file: the folder is cleaned up by age
                }
            }
        }
    }

    private void Save(ImportRecord record)
    {
        string json;
        lock (_gate)
        {
            record.UpdatedUtc = Now;
            json = JsonSerializer.Serialize(record, Json);
        }
        var dir = DirOf(record.Id);
        if (!Directory.Exists(dir)) return;   // deleted meanwhile
        ImportFs.WriteTextAtomic(Path.Combine(dir, "state.json"), json);
    }

    private ImportRecord Require(string? id)
    {
        RequireId(id);
        EnsureLoaded();
        lock (_gate)
        {
            if (_records.TryGetValue(id!, out var record))
            {
                if (Directory.Exists(DirOf(id!)))
                    return record;
                _records.Remove(id!);
            }
        }
        throw new ImportException("not_found", "There is no import with that id (it may have expired after the retention time, or been undone).");
    }

    /// <summary>
    /// Whether an import holds one of the staging places. A failed import, a partly undone one and an applied one do not: an applied
    /// import keeps only its backups and journal (no unpacked images), so it cannot be the reason a new import is refused.
    /// An interrupted apply does count; it must be undone first.
    /// </summary>
    private static bool CountsAsStaged(ImportRecord r) => r.Status is not (ImportStatus.Failed or ImportStatus.UndonePartial or ImportStatus.Applied);

    private static bool IsBusyStatus(string status) => status is ImportStatus.Downloading or ImportStatus.Extracting or ImportStatus.Applying;

    #endregion

    #region Clean-up

    /// <summary>
    /// Deletes imports older than the retention time (measured from creation or from the apply, whichever is later) and old folders
    /// nobody knows. Takes the same lock as preview, apply and undo, so it can never delete an import while one of those runs; when
    /// one is running it does nothing (the next call cleans up). An interrupted apply is never deleted: its backups may be the only
    /// copy of the original files.
    /// </summary>
    public int CleanupExpired()
    {
        ImportFs.RequireRoot();
        if (!_step.Wait(0))
            return 0;
        try
        {
            return CleanupCore();
        }
        finally
        {
            _step.Release();
        }
    }

    private int CleanupCore()
    {
        EnsureLoaded();
        int removed = 0;
        var now = Now;
        List<ImportRecord> expired;
        lock (_gate)
        {
            expired = _records.Values
                .Where(r => !IsBusyStatus(r.Status) && r.Status != ImportStatus.ApplyInterrupted && now - (r.AppliedUtc.HasValue && r.AppliedUtc > r.CreatedUtc ? r.AppliedUtc.Value : r.CreatedUtc) > _options.Retention)
                .ToList();
        }
        foreach (var record in expired)
        {
            ImportFs.DeleteTree(DirOf(record.Id));
            lock (_gate) _records.Remove(record.Id);
            removed++;
        }

        // folders without a usable state file
        var inbox = InboxRoot;
        if (Directory.Exists(inbox))
        {
            foreach (var dir in Directory.EnumerateDirectories(inbox))
            {
                var id = Path.GetFileName(dir);
                bool known;
                lock (_gate) known = _records.ContainsKey(id);
                if (known) continue;
                try
                {
                    var age = now - Directory.GetLastWriteTimeUtc(dir);
                    if (age > _options.Retention && new DirectoryInfo(dir).LinkTarget == null)
                    {
                        ImportFs.DeleteTree(dir);
                        removed++;
                    }
                }
                catch (IOException) { }
            }
        }
        return removed;
    }

    #endregion

    #region import_fetch

    public ImportResultBase Fetch(string? url, string? kind, string? expectedSha256, string? category = null)
    {
        try
        {
            ImportFs.RequireRoot();
            CleanupExpired();

            kind = string.IsNullOrWhiteSpace(kind) ? ImportKind.Wz : kind.Trim().ToLowerInvariant();
            if (kind is not (ImportKind.Wz or ImportKind.Files))
                throw new ImportException("bad_kind", "kind must be \"wz\" (a .wz file for the tree) or \"files\" (a PNG or a zip of PNGs).");

            string? expected = null;
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                expected = expectedSha256.Trim();
                if (!HashPattern.IsMatch(expected))
                    throw new ImportException("bad_hash", "expectedSha256 must be 64 hexadecimal characters.");
                expected = expected.ToLowerInvariant();
            }

            var uri = ImportUrlPolicy.ParseAndCheck(url, _options);

            string? treeCategory = null;
            if (kind == ImportKind.Wz)
            {
                if (!_session.IsInitialized)
                    throw new ImportException("no_data_source", "No data source initialized. Call init_data_source first, so the import knows which tree it is for.");
                if (!string.IsNullOrWhiteSpace(category))
                    treeCategory = ResolveCategory(category.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(category))
            {
                throw new ImportException("bad_kind", "category only applies to kind \"wz\".");
            }

            ImportRecord record;
            lock (_gate)
            {
                if (_disposed)
                    throw new ImportException("busy", "The server is shutting down.");
                if (_activeDownloadId != null)
                    throw new ImportException("busy", $"Another import ({_activeDownloadId}) is still downloading; only one download runs at a time. Check it with import_status.");
            }

            PurgeFinishedFailures();
            lock (_gate)
            {
                int staged = _records.Values.Count(CountsAsStaged);
                if (staged >= _options.MaxStaged)
                    throw new ImportException("too_many_staged", $"{staged} imports are already staged (the limit is {_options.MaxStaged}). Free a place with import_undo (on an import you no longer need), or wait for the {_options.Retention.TotalDays:0} day clean-up.");
            }

            EnsureInbox();
            var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var dir = DirOf(id);
            ImportFs.Confine(dir);
            Directory.CreateDirectory(dir);

            var now = Now;
            record = new ImportRecord
            {
                Id = id, Kind = kind, Status = ImportStatus.Downloading, Phase = "waiting for the server of the link",
                CreatedUtc = now, UpdatedUtc = now, Host = uri.IdnHost, Category = treeCategory,
            };
            var cts = new CancellationTokenSource();
            lock (_gate)
            {
                if (_activeDownloadId != null)
                {
                    ImportFs.DeleteTree(dir);
                    throw new ImportException("busy", "Another import is still downloading; only one download runs at a time.");
                }
                _records[id] = record;
                _running[id] = cts;
                _activeDownloadId = id;
            }
            Save(record);
            Log(id, $"started ({kind}) from {uri.IdnHost}");

            var job = Task.Run(() => RunJobAsync(record, uri, expected, treeCategory is not null, cts));
            lock (_gate) _jobs[id] = job;

            return new ImportFetchResult
            {
                Success = true, Id = id, Kind = kind, Status = record.Status,
                Message = "The download started in the background. Check progress with import_status(id).",
            };
        }
        catch (ImportException ex)
        {
            return ImportFailure.From(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return Unexpected(ex);
        }
    }

    private void PurgeFinishedFailures()
    {
        List<ImportRecord> failed;
        lock (_gate) failed = _records.Values.Where(r => r.Status == ImportStatus.Failed).ToList();
        foreach (var r in failed)
        {
            ImportFs.DeleteTree(DirOf(r.Id));
            lock (_gate) _records.Remove(r.Id);
        }
    }

    private async Task RunJobAsync(ImportRecord record, Uri uri, string? expected, bool categoryKnown, CancellationTokenSource cts)
    {
        var id = record.Id;
        var dir = DirOf(id);
        try
        {
            var part = Path.Combine(dir, "download.part");
            var result = await _downloader.DownloadAsync(
                uri, part,
                name =>
                {
                    if (record.Kind != ImportKind.Wz) return;
                    if (!categoryKnown)
                        record.Category = CategoryFromFileName(name);
                    else
                        RequireNameMatchesCategory(name, record.Category!);
                },
                (bytes, total) =>
                {
                    lock (_gate) { record.BytesDownloaded = bytes; record.TotalBytes = total; }
                },
                cts.Token).ConfigureAwait(false);

            lock (_gate)
            {
                record.BytesDownloaded = result.Bytes;
                record.Sha256 = result.Sha256;
                record.FileName = SafeDisplayName(result.SuggestedName);
            }

            if (expected != null && !string.Equals(expected, result.Sha256, StringComparison.Ordinal))
                throw new ImportException("hash_mismatch", $"The downloaded file's SHA-256 ({result.Sha256.Substring(0, 12)}...) does not match expectedSha256. The file was deleted.");

            cts.Token.ThrowIfCancellationRequested();

            if (record.Kind == ImportKind.Wz)
            {
                await Task.Run(() => PrepareWz(record, part, cts.Token)).ConfigureAwait(false);
            }
            else
            {
                lock (_gate) record.Phase = "unpacking";
                Save(record);
                PrepareFiles(record, part, result.SuggestedName);
            }

            cts.Token.ThrowIfCancellationRequested();
            lock (_gate) { record.Status = ImportStatus.Ready; record.Phase = null; }
            Save(record);
            Log(id, "ready");
        }
        catch (OperationCanceledException)
        {
            ImportFs.DeleteTree(dir);
            lock (_gate) _records.Remove(id);
            Log(id, "cancelled");
        }
        catch (ImportException ex)
        {
            Fail(record, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            Log(id, "unexpected " + ex.GetType().Name);
            Fail(record, "internal_error", "Something unexpected went wrong; the import was stopped. Details are in the server log.");
        }
        finally
        {
            lock (_gate)
            {
                if (_activeDownloadId == id) _activeDownloadId = null;
                _running.Remove(id);
            }
            cts.Dispose();
        }
    }

    private void Fail(ImportRecord record, string code, string message)
    {
        var dir = DirOf(record.Id);
        // keep the report, drop the big files
        foreach (var name in new[] { "download.part", "src", "extracted", "files" })
            ImportFs.DeleteTree(Path.Combine(dir, name));
        try { var part = Path.Combine(dir, "download.part"); if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
        lock (_gate)
        {
            record.Status = ImportStatus.Failed;
            record.Phase = null;
            record.ErrorCode = code;
            record.Error = message;
        }
        try { Save(record); } catch (IOException) { }
        Log(record.Id, "failed: " + code);
    }

    private static string? SafeDisplayName(string? name)
        => name != null && PathGuard.IsSafeComponent(name, out _) && name.Length <= 120 ? name : null;

    private void PrepareFiles(ImportRecord record, string downloaded, string? suggestedName)
    {
        var filesDir = Path.Combine(DirOf(record.Id), "files");
        ImportFs.Confine(filesDir);
        Directory.CreateDirectory(filesDir);
        var files = ImportFilesUnpacker.Unpack(downloaded, suggestedName, filesDir, _options);
        File.Delete(downloaded);
        lock (_gate) record.Files = files;
    }

    #endregion

    #region import_status

    public ImportResultBase GetStatus(string? id)
    {
        try
        {
            ImportFs.RequireRoot();
            CleanupExpired();
            var r = Require(id);
            lock (_gate)
            {
                var result = new ImportStatusResult
                {
                    Success = true, Id = r.Id, Kind = r.Kind, Status = r.Status, Phase = r.Phase,
                    Category = r.Category, FileName = r.FileName, BytesDownloaded = r.BytesDownloaded, TotalBytes = r.TotalBytes,
                    Sha256 = r.Sha256, ImagesInSource = r.ImagesInSource,
                    CreatedUtc = r.CreatedUtc.ToString("o"),
                    ExpiresUtc = ((r.AppliedUtc.HasValue && r.AppliedUtc > r.CreatedUtc ? r.AppliedUtc.Value : r.CreatedUtc) + _options.Retention).ToString("o"),
                    Warnings = r.Warnings is { Count: > 0 } ? r.Warnings.ToList() : null,
                    Files = r.Files?.Select(f => new StagedFile { Name = f.Name, Path = f.Path, Bytes = f.Bytes, Width = f.Width, Height = f.Height, Sha256 = f.Sha256 }).ToList(),
                    AppliedAdded = r.AppliedAdded, AppliedReplaced = r.AppliedReplaced,
                };
                if (r.Status == ImportStatus.Failed)
                {
                    result.JobErrorCode = r.ErrorCode;
                    result.JobError = r.Error;
                    result.NextStep = "This import failed and changed nothing. Start again with import_fetch, or free its place with import_undo(id).";
                }
                else
                {
                    result.NextStep = r.Status switch
                    {
                        ImportStatus.Downloading or ImportStatus.Extracting => "Still working. Call import_status(id) again in a little while.",
                        ImportStatus.Ready when r.Kind == ImportKind.Wz => "Call import_preview(id) to see what would change.",
                        ImportStatus.Ready => "The files are staged. Place them with import_png(category, image, parentPath, name, pngPath=<one of the paths above>), then save_image. The fetch changed nothing in the tree. When done, free the place with import_undo(id).",
                        ImportStatus.Applying => "An apply is running. Wait for it to finish.",
                        ImportStatus.Applied => "Applied. Undo it with import_undo(id) if needed.",
                        ImportStatus.ApplyInterrupted => "An apply was interrupted. Use import_undo(id) to put the files back.",
                        ImportStatus.UndonePartial => "Some files could not be restored because they were edited after the import (see import_undo).",
                        _ => null,
                    };
                }
                return result;
            }
        }
        catch (ImportException ex)
        {
            return ImportFailure.From(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return Unexpected(ex);
        }
    }

    #endregion

    #region Helpers

    /// <summary>Waits for the background job of an import to end. For tests and shutdown.</summary>
    internal async Task WaitForJobAsync(string id, TimeSpan timeout)
    {
        Task? job;
        lock (_gate) _jobs.TryGetValue(id, out job);
        if (job == null) return;
        await job.WaitAsync(timeout).ConfigureAwait(false);
    }

    private ImportFailure Unexpected(Exception ex)
    {
        Console.Error.WriteLine($"[import] unexpected {ex.GetType().Name}");
        return ImportFailure.From("internal_error", "Something unexpected went wrong. Details are in the server log.");
    }

    private static void Log(string id, string message) => Console.Error.WriteLine($"[import] {id} {message}");

    public void Dispose()
    {
        List<CancellationTokenSource> running;
        lock (_gate)
        {
            _disposed = true;
            running = _running.Values.ToList();
        }
        foreach (var cts in running)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    #endregion
}
