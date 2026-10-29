using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WzImgMCP.Viewer;

namespace WzImgMCP.Packer;

public sealed record PackerWz(string Name, long SizeBytes, string Sha256);

public sealed record PackerError(string Code, string Message);

/// <summary>What <c>GET /v1/jobs/{id}</c> answers, and what is kept in <c>&lt;jobId&gt;/job.json</c>.</summary>
public sealed record PackerJobView(
    string JobId,
    string State,
    string Person,
    string Category,
    PackerWz? Wz,
    int ImgCount,
    IReadOnlyList<string> Warnings,
    PackerError? Error);

/// <summary>One pack job. States: <c>queued</c> (created, thread not started), <c>running</c>, <c>done</c>, <c>failed</c>.</summary>
public sealed class PackerJob
{
    public const string Queued = "queued", Running = "running", Done = "done", Failed = "failed";

    private readonly object _gate = new();
    private string _state = Queued;
    private PackerWz? _wz;
    private int _imgCount;
    private PackerError? _error;
    private readonly List<string> _warnings = new();

    public string Id { get; }
    public string Person { get; }
    public string Category { get; }

    public PackerJob(string id, string person, string category)
    {
        Id = id;
        Person = person;
        Category = category;
    }

    public bool Finished { get { lock (_gate) return _state is Done or Failed; } }

    public void MarkRunning() { lock (_gate) _state = Running; }

    public void AddWarning(string warning) { lock (_gate) _warnings.Add(warning); }

    public void Complete(PackerWz wz, int imgCount)
    {
        lock (_gate)
        {
            _wz = wz;
            _imgCount = imgCount;
            _state = Done;
        }
    }

    public void Fail(string code, string message)
    {
        lock (_gate)
        {
            _wz = null;
            _error = new PackerError(code, message);
            _state = Failed;
        }
    }

    public PackerJobView View()
    {
        lock (_gate)
            return new PackerJobView(Id, _state, Person, Category, _wz, _imgCount, _warnings.ToArray(), _error);
    }
}

/// <summary>
/// The jobs: kept in memory (the newest <see cref="PackerOptions.MaxJobsKept"/> finished ones) and, once created, as
/// <c>&lt;outbox&gt;/&lt;jobId&gt;/job.json</c>, so a job can still be read after a restart of the packer. A job.json that says
/// <c>queued</c> or <c>running</c> and belongs to no job in memory was cut short by a restart and is reported as failed.
/// The packer never deletes a finished job's folder: cleaning the outbox is the publisher's job.
/// </summary>
public sealed class PackerJobStore
{
    public const string JobFileName = "job.json";
    private const long MaxJobFileBytes = 256 * 1024;
    private static readonly Regex IdPattern = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    private readonly PackerOptions _options;
    private readonly PosixViewerFileSystem _files;
    private readonly ConcurrentDictionary<string, PackerJob> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    public PackerJobStore(PackerOptions options, PosixViewerFileSystem files)
    {
        _options = options;
        _files = files;
    }

    public static bool IsJobId(string? id) => id != null && IdPattern.IsMatch(id);

    public string JobDirectory(string id) => Path.Combine(_options.Outbox, id);

    /// <summary>Makes a new job with a fresh random id and its folder in the outbox. Throws 503 <c>outbox_unavailable</c> when the folder cannot be made.</summary>
    public PackerJob Create(string person, string category)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var dir = JobDirectory(id);
        try
        {
            if (_files.Probe(dir) != PathState.Absent)
                throw new ViewerApiException(503, "outbox_unavailable", "the outbox cannot take a new job");
            Directory.CreateDirectory(dir);
            if (new DirectoryInfo(dir).LinkTarget != null || !Directory.Exists(dir))
                throw new ViewerApiException(503, "outbox_unavailable", "the outbox cannot take a new job");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ViewerApiException(503, "outbox_unavailable", "the outbox cannot take a new job");
        }

        var job = new PackerJob(id, person, category);
        _jobs[id] = job;
        _order.Enqueue(id);
        Persist(job);
        Trim();
        return job;
    }

    /// <summary>The job's view, from memory or from its job.json; null when there is no such job.</summary>
    public PackerJobView? Find(string? id)
    {
        if (!IsJobId(id))
            return null;
        if (_jobs.TryGetValue(id!, out var job))
            return job.View();

        var path = Path.Combine(JobDirectory(id!), JobFileName);
        try
        {
            if (_files.Probe(JobDirectory(id!)) != PathState.Present || new DirectoryInfo(JobDirectory(id!)).LinkTarget != null)
                return null;
            var read = _files.Read(path, MaxJobFileBytes, null);
            var view = JsonSerializer.Deserialize<PackerJobView>(read.Bytes, Json);
            if (view == null || view.JobId != id || !IsJobId(view.JobId))
                return null;
            if (view.State is PackerJob.Queued or PackerJob.Running)
                view = view with { State = PackerJob.Failed, Wz = null, Error = new PackerError("interrupted", "the packer stopped before this job finished") };
            return view with { Warnings = view.Warnings ?? Array.Empty<string>() };
        }
        catch (Exception ex) when (ex is ViewerApiException or JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes job.json (new file, then rename; never through a link). A failure to write is logged by the caller's next read, not thrown.</summary>
    public void Persist(PackerJob job)
    {
        var dir = JobDirectory(job.Id);
        var final = Path.Combine(dir, JobFileName);
        var temp = Path.Combine(dir, JobFileName + ".tmp");
        try
        {
            if (new DirectoryInfo(dir).LinkTarget != null)
                return;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(job.View(), Json);
            File.Delete(temp);   // a leftover of a failed write; deleting a link removes the link, not its target
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))   // CreateNew (O_EXCL) never opens through a link
            {
                stream.Write(bytes);
            }
            File.Move(temp, final, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the in-memory job still answers; only a restart would lose it
        }
    }

    private void Trim()
    {
        // Forget the oldest finished jobs once there are more than the limit; running jobs are never forgotten.
        while (_order.Count > _options.MaxJobsKept && _order.TryPeek(out var oldest))
        {
            if (_jobs.TryGetValue(oldest, out var job) && !job.Finished)
                break;
            _order.TryDequeue(out _);
            _jobs.TryRemove(oldest, out _);
        }
    }
}

/// <summary>
/// Lets one heavy piece of work (a pack or a verify) run at a time. Its own type, like the viewer's limiters, so no other
/// semaphore in the service collection can replace it. The lease is given back by the work itself, never by the waiting request.
/// </summary>
public sealed class PackerWorkGate
{
    private readonly SemaphoreSlim _slot = new(1, 1);

    public bool Busy => _slot.CurrentCount == 0;

    /// <summary>Takes the slot without waiting. Null when it is in use.</summary>
    public Lease? TryEnter() => _slot.Wait(0) ? new Lease(_slot) : null;

    public sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _owner;
        internal Lease(SemaphoreSlim owner) { _owner = owner; }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
