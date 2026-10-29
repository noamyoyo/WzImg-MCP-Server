using MapleLib.Img;
using MapleLib.WzLib;
using System.Text.Json;
using WzImgMCP.Utils;

namespace WzImgMCP.Server;

/// <summary>
/// Manages the loaded data source state for the MCP server
/// </summary>
public class WzSessionManager : IDisposable
{
    /// <summary>Environment variable that names the folder opened automatically on first use.</summary>
    public const string DefaultDataPathEnvVar = "WZIMG_DEFAULT_DATA_PATH";
    public const string DefaultDataPathFallback = "/data/img";

    private volatile IDataSource? _dataSource;
    private readonly Dictionary<string, WzImage> _parsedImages = new();
    private readonly object _initLock = new();
    private readonly string? _defaultDataPathOverride;
    private bool _explicitInit;
    private bool _autoInitialized;
    private bool _disposed;
    private int _openCount;
    private MemoryGuard _guard;
    private volatile ScanLimits _scanLimits = ServerSettings.ScanLimitsFromEnvironment();

    public WzSessionManager()
    {
        _guard = new MemoryGuard(() => _dataSource, ServerSettings.MemoryGuardFromEnvironment());
    }

    /// <summary>Test hook: uses this folder instead of WZIMG_DEFAULT_DATA_PATH for the automatic open.</summary>
    public WzSessionManager(string? defaultDataPath) : this() { _defaultDataPathOverride = defaultDataPath; }

    /// <summary>The guard that trims the cache and the managed heap (its timer runs after <see cref="StartMemoryGuard"/>).</summary>
    public MemoryGuard MemoryGuard => _guard;

    /// <summary>The current data source, or null. Does not open the default folder.</summary>
    public IDataSource? CurrentDataSource => _dataSource;

    /// <summary>Test hook: replaces the memory guard (for example one with a fake clock). The old guard is disposed.</summary>
    public void UseMemoryGuard(MemoryGuard guard)
    {
        var old = Interlocked.Exchange(ref _guard, guard);
        old.Dispose();
    }

    /// <summary>Starts the memory guard's timer; <paramref name="log"/> gets one line per trim. Called once at startup.</summary>
    public void StartMemoryGuard(Action<string> log) => _guard.Start(log);

    /// <summary>Records a tool call (the idle trim waits for a quiet period after the last one).</summary>
    public void Touch() => _guard.Touch();

    /// <summary>Limits of scans and bulk tools (WZIMG_SCAN_MAX_IMAGES and the others). Replaceable for tests.</summary>
    public ScanLimits ScanLimits
    {
        get => _scanLimits;
        set => _scanLimits = value;
    }

    /// <summary>
    /// The current data source as an IMG file system source, which the scan and listing tools need (they list images without
    /// opening them and open images without caching them). Throws like <see cref="DataSource"/> when none is open.
    /// </summary>
    public ImgFileSystemDataSource ImgSource
        => DataSource as ImgFileSystemDataSource
            ?? throw new InvalidOperationException("This tool needs an IMG file system data source.");

    /// <summary>The folder that is opened automatically when a tool needs the data source and none was opened.</summary>
    public string DefaultDataPath
    {
        get
        {
            var v = _defaultDataPathOverride ?? Environment.GetEnvironmentVariable(DefaultDataPathEnvVar);
            return string.IsNullOrWhiteSpace(v) ? DefaultDataPathFallback : v.Trim();
        }
    }

    /// <summary>How many data sources this session opened so far (automatic open and init_data_source).</summary>
    public int OpenCount => Volatile.Read(ref _openCount);

    /// <summary>True when the current data source was opened by the automatic open, false after init_data_source.</summary>
    public bool AutoInitialized { get { lock (_initLock) return _autoInitialized; } }

    /// <summary>
    /// Gets whether a data source is initialized. When none is, the default folder (WZIMG_DEFAULT_DATA_PATH, default
    /// /data/img) is opened first, once, under a lock, unless init_data_source was already called. A missing folder,
    /// a folder without manifest.json or one outside WZIMG_ALLOWED_ROOT leaves the session uninitialized (the tools
    /// then return their usual "No data source initialized" error) and is retried on the next call.
    /// </summary>
    public bool IsInitialized
    {
        get
        {
            _guard.Touch();
            return _dataSource?.IsInitialized == true || TryAutoInit();
        }
    }

    /// <summary>
    /// Gets the current data source (throws if not initialized)
    /// </summary>
    public IDataSource DataSource
    {
        get
        {
            _guard.Touch();
            if (_dataSource == null) TryAutoInit();
            return _dataSource
                ?? throw new InvalidOperationException("Data source not initialized. Call init_data_source first.");
        }
    }

    /// <summary>
    /// Gets the version info for the current data source
    /// </summary>
    public VersionInfo? VersionInfo => _dataSource?.VersionInfo;

    private bool TryAutoInit()
    {
        lock (_initLock)
        {
            if (_dataSource?.IsInitialized == true) return true;
            if (_explicitInit || _disposed) return false;   // an explicit init_data_source always wins
            try
            {
                var path = PathGuard.Resolve(DefaultDataPath);
                if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "manifest.json"))) return false;
                OpenDataSource(path);
                _autoInitialized = true;
                return _dataSource?.IsInitialized == true;
            }
            catch (Exception ex)
            {
                // Still "not initialized" for the caller, but say why (corrupt manifest, I/O error, folder outside the allowed root).
                Console.Error.WriteLine($"[WzSessionManager] Automatic open of the default data folder failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Initializes a new IMG filesystem data source
    /// </summary>
    public DataSourceInfo InitDataSource(string basePath)
    {
        lock (_initLock)
        {
            // Open first: when it throws, the previous data source stays in place and usable, and the flags are unchanged.
            var info = OpenDataSource(basePath);
            _explicitInit = true;
            _autoInitialized = false;
            return info;
        }
    }

    private DataSourceInfo OpenDataSource(string basePath)
    {
        // Validate path exists
        if (!Directory.Exists(basePath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {basePath}");
        }

        // Create the new data source first; the existing one is only replaced (and disposed) once this succeeded.
        // The cache limit comes from WZIMG_CACHE_MB (default 400): the same setting a HaCreatorConfig carries as Cache.MaxMemoryCacheMB.
        var config = new HaCreatorConfig();
        config.Cache.MaxMemoryCacheMB = ServerSettings.CacheMb;
        var next = new ImgFileSystemDataSource(basePath, config);
        DataSourceInfo info;
        try
        {
            var versionInfo = next.VersionInfo;
            info = new DataSourceInfo
            {
                Path = basePath,
                Name = next.Name,
                Version = versionInfo?.Version ?? "unknown",
                DisplayName = versionInfo?.DisplayName ?? basePath,
                Categories = next.GetCategories().ToList(),
                IsPreBB = versionInfo?.IsPreBB ?? false,
                Is64Bit = versionInfo?.Is64Bit ?? false
            };
        }
        catch
        {
            next.Dispose();
            throw;
        }

        var previous = _dataSource;
        _dataSource = next;
        _parsedImages.Clear();
        previous?.Dispose();
        Interlocked.Increment(ref _openCount);
        return info;
    }

    /// <summary>
    /// Scans a directory for IMG filesystem data sources
    /// </summary>
    public List<AvailableDataSource> ScanForDataSources(string path, bool recursive = true)
    {
        var found = new List<AvailableDataSource>();

        if (!Directory.Exists(path))
        {
            return found;
        }

        // Do not follow symbolic links / junctions (they could lead out of the allowed root) and
        // check every result against the allowed root as well.
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };

        foreach (var dir in Directory.EnumerateDirectories(path, "*", enumeration))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!IsAllowed(dir) || !IsAllowed(manifestPath))
            {
                continue;
            }

            if (File.Exists(manifestPath))
            {
                try
                {
                    var json = File.ReadAllText(manifestPath);
                    var manifest = JsonSerializer.Deserialize<VersionInfo>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    found.Add(new AvailableDataSource
                    {
                        Path = dir,
                        Version = manifest?.Version,
                        DisplayName = manifest?.DisplayName ?? Path.GetFileName(dir),
                        Size = GetDirectorySize(dir)
                    });
                }
                catch
                {
                    // Skip directories with invalid manifests
                }
            }
        }

        // Also check the root path
        var rootManifest = Path.Combine(path, "manifest.json");
        if (IsAllowed(rootManifest) && File.Exists(rootManifest))
        {
            try
            {
                var json = File.ReadAllText(rootManifest);
                var manifest = JsonSerializer.Deserialize<VersionInfo>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                found.Insert(0, new AvailableDataSource
                {
                    Path = path,
                    Version = manifest?.Version,
                    DisplayName = manifest?.DisplayName ?? Path.GetFileName(path),
                    Size = GetDirectorySize(path)
                });
            }
            catch { }
        }

        return found;
    }

    /// <summary>True when the path (after resolving symbolic links) is inside WZIMG_ALLOWED_ROOT, or no root is set.</summary>
    private static bool IsAllowed(string path)
    {
        try
        {
            PathGuard.Resolve(path);
            return true;
        }
        catch (PathNotAllowedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets an image from the current data source, parsing it if necessary
    /// </summary>
    public WzImage GetImage(string category, string imageName)
    {
        var img = DataSource.GetImage(category, imageName);
        if (img == null)
        {
            throw new FileNotFoundException($"Image not found: {category}/{imageName}");
        }

        // Ensure the image is parsed
        if (!img.Parsed)
        {
            img.ParseImage();
        }

        return img;
    }

    /// <summary>
    /// Checks if an image is currently parsed
    /// </summary>
    public bool IsImageParsed(string category, string imageName)
    {
        var img = DataSource.GetImage(category, imageName);
        return img?.Parsed ?? false;
    }

    /// <summary>
    /// Parses an image (loads its properties into memory)
    /// </summary>
    public int ParseImage(string category, string imageName)
    {
        var img = GetImage(category, imageName);
        if (!img.Parsed)
        {
            img.ParseImage();
        }
        return img.WzProperties?.Count ?? 0;
    }

    public const string UnsavedChangesMessage = "unsaved changes: call save_image or discard_changes first";

    /// <summary>
    /// Unparses an image (frees memory). Refused while the image has unsaved changes.
    /// </summary>
    public void UnparseImage(string category, string imageName)
    {
        var img = DataSource.GetImage(category, imageName);
        if (img?.Changed == true)
        {
            // An unparsed image that is still flagged as changed reads back empty and would be saved empty.
            throw new InvalidOperationException(UnsavedChangesMessage);
        }
        if (img?.Parsed == true)
        {
            img.UnparseImage();
        }
    }

    /// <summary>
    /// Gets a list of the parsed images. These are the images in the cache: no image is loaded to answer this.
    /// </summary>
    public List<ParsedImageInfo> GetParsedImages()
    {
        var result = new List<ParsedImageInfo>();

        foreach (var cached in ImgSource.GetCachedImages())
        {
            var img = cached.Image;
            if (img.Parsed)
            {
                result.Add(new ParsedImageInfo
                {
                    Category = cached.Category,
                    Name = img.Name,
                    PropertyCount = img.WzProperties?.Count ?? 0
                });
            }
        }

        return result;
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _guard.Dispose();
        _dataSource?.Dispose();
        _parsedImages.Clear();
        _disposed = true;
    }
}

/// <summary>
/// Information about the current data source
/// </summary>
public class DataSourceInfo
{
    public required string Path { get; set; }
    public required string Name { get; set; }
    public required string Version { get; set; }
    public required string DisplayName { get; set; }
    public required List<string> Categories { get; set; }
    public bool IsPreBB { get; set; }
    public bool Is64Bit { get; set; }
}

/// <summary>
/// Information about an available data source found during scanning
/// </summary>
public class AvailableDataSource
{
    public required string Path { get; set; }
    public string? Version { get; set; }
    public string? DisplayName { get; set; }
    public long Size { get; set; }
}

/// <summary>
/// Information about a parsed image
/// </summary>
public class ParsedImageInfo
{
    public required string Category { get; set; }
    public required string Name { get; set; }
    public int PropertyCount { get; set; }
}
