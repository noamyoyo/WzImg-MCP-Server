using System.Reflection;
using MapleLib.WzLib;
using ModelContextProtocol.Server;
using WzImgMCP.Server;
using WzImgMCP.Tools;
using WzImgMCP.Utils;

namespace WzImgMCP.Tests;

/// <summary>
/// The idle trim waits for a quiet period after the last real tool call. Reading the data source, asking whether it is initialized
/// and polling get_cache_stats must not count as activity (a monitoring loop would keep the server from ever trimming), and every
/// other tool must. The clock is the guard's fake clock, so nothing sleeps.
/// </summary>
[Collection("AllowedRootEnv")]   // the every-tool test points WZIMG_ALLOWED_ROOT at its own folder
public sealed class IdleActivityTests : IDisposable
{
    private sealed class FakeClock : TimeProvider
    {
        private long _ms;
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => Interlocked.Read(ref _ms);
        public void Advance(TimeSpan span) => Interlocked.Add(ref _ms, (long)span.TotalMilliseconds);
    }

    private readonly ScanTestTree _tree = new(6);
    private readonly FakeClock _clock = new();
    private readonly WzSessionManager _session;
    private readonly MemoryGuard _guard;

    public IdleActivityTests()
    {
        _session = _tree.NewSession();
        _guard = new MemoryGuard(() => _session.CurrentDataSource,
            new MemoryGuardSettings(SoftLimitBytes: 0, IdleTrim: TimeSpan.FromSeconds(300)), _clock, () => 10, () => { });
        _session.UseMemoryGuard(_guard);
    }

    public void Dispose()
    {
        _session.Dispose();
        _tree.Dispose();
    }

    [Fact]
    public void ReadingTheDataSource_IsNotActivity()
    {
        _session.GetImage(ScanTestTree.Category, "m001.img");
        _clock.Advance(TimeSpan.FromSeconds(299));

        _ = _session.DataSource;
        _ = _session.IsInitialized;
        _ = _session.CurrentDataSource;
        _ = _session.DataSource.GetStats();

        Assert.Equal(TimeSpan.FromSeconds(299), _guard.QuietFor);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("idle", _guard.Tick()!.Reason);
    }

    [Fact]
    public void PollingGetCacheStats_IsNotActivity()
    {
        var tools = new FileTools(_session);
        for (int i = 0; i < 20; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(20));
            MarkdownTestHelper.AssertSuccess(tools.GetCacheStats());
        }

        Assert.Equal(TimeSpan.FromSeconds(400), _guard.QuietFor);
        Assert.Equal("idle", _guard.Tick()!.Reason);          // a monitoring loop did not keep it from trimming
    }

    [Fact]
    public void ARealToolCall_IsActivity()
    {
        _clock.Advance(TimeSpan.FromSeconds(250));

        MarkdownTestHelper.AssertSuccess(new FileTools(_session).ListCategories());

        Assert.Equal(TimeSpan.Zero, _guard.QuietFor);
        _clock.Advance(TimeSpan.FromSeconds(100));
        Assert.Null(_guard.Tick());
    }

    [Fact]
    public void EveryTool_ExceptGetCacheStats_CountsAsActivity()
    {
        // Every tool method is called with harmless arguments (names that exist nowhere, paths inside an empty folder that is the
        // allowed root); most answer with an error, which is fine: the call still started.
        string root = Directory.CreateTempSubdirectory("wzimg-idle-").FullName;
        string? previous = Environment.GetEnvironmentVariable(PathGuard.EnvVar);
        Environment.SetEnvironmentVariable(PathGuard.EnvVar, root);
        try
        {
            var untouched = new List<string>();
            int called = 0;
            foreach (var type in typeof(FileTools).Assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null))
            {
                object instance = Activator.CreateInstance(type, _session)!;
                foreach (var method in type.GetMethods().Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null))
                {
                    string name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? method.Name;
                    _clock.Advance(TimeSpan.FromSeconds(100));
                    try { method.Invoke(instance, Arguments(method, root)); }
                    catch (TargetInvocationException) { /* the tool failed after it had started: the call counts */ }
                    called++;

                    bool touched = _guard.QuietFor < TimeSpan.FromSeconds(100);
                    if (touched == (name == "get_cache_stats"))
                        untouched.Add(name);
                }
            }

            Assert.True(called > 60, $"only {called} tools were found");
            Assert.True(untouched.Count == 0, "wrong activity accounting for: " + string.Join(", ", untouched));
            Assert.Empty(Directory.GetFileSystemEntries(root));   // the harmless arguments created nothing
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathGuard.EnvVar, previous);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static object?[] Arguments(MethodInfo method, string root)
        => method.GetParameters().Select(p =>
        {
            var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
            if (p.HasDefaultValue && p.DefaultValue != null && p.DefaultValue != DBNull.Value)
                return p.DefaultValue;
            if (t == typeof(string)) return Path.Combine(root, "does-not-exist");
            if (t == typeof(bool)) return false;
            if (t == typeof(int)) return 0;
            if (t == typeof(long)) return 0L;
            if (t == typeof(double)) return 0d;
            return t.IsValueType ? Activator.CreateInstance(t) : null;
        }).ToArray();
}
