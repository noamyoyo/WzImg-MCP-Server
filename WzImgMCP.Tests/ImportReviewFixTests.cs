#if LINUX_BUILD
using WzImgMCP.Import;
using WzImgMCP.Tools;

namespace WzImgMCP.Tests;

/// <summary>Tests for the fixes that came out of the security review of import by link. Each one fails when its fix is taken out.</summary>
[Collection("AllowedRootEnv")]
public sealed class ImportReviewFixTests : IClassFixture<ImportWzData>
{
    private const string Direct = ImportEnv.DirectLink;
    private readonly ImportWzData _data;

    public ImportReviewFixTests(ImportWzData data) { _data = data; }

    private ImportEnv NewEnv(Func<ImportOptions, ImportOptions>? tweak = null) => new(tweak, copyTreeFrom: _data.TreeV1);

    private async Task<string> ReadyAsync(ImportEnv env, string route = Direct, string? name = "Skill.wz", string? category = null)
    {
        env.Web.ServeFile(route, _data.V2Wz, name);
        var status = await env.FetchAndWait(route, "wz", null, category);
        Assert.True(status.Status == ImportStatus.Ready, $"status {status.Status} {status.JobErrorCode} {status.JobError}");
        return status.Id!;
    }

    private async Task<(string Id, string Token)> PreviewedAsync(ImportEnv env)
    {
        var id = await ReadyAsync(env);
        var preview = env.Preview(id);
        Assert.NotNull(preview.Token);
        return (id, preview.Token!);
    }

    private static string Skill(ImportEnv env, params string[] parts) => Path.Combine(new[] { env.Tree, "Skill" }.Concat(parts).ToArray());
    private static Dictionary<string, string> TreeSnapshot(ImportEnv env) => ImportEnv.Snapshot(env.Tree);

    /// <summary>Makes the 2nd file of an apply fail, and every file of an undo (so the rollback cannot put anything back).</summary>
    private static Action<string> FailSecondFileAndEveryRestore(Func<int> count, Action increment) => what =>
    {
        if (what.StartsWith("undo:")) throw new IOException("restore failed");
        increment();
        if (count() == 2) throw new IOException("copy failed");
    };

    // ---- F1: a rollback that fails keeps the backups, the journal and the lock; undo restores from them ----

    [Fact]
    public async Task F1_FailedApply_ThenFailedRollback_IsInterrupted_NotReady_AndBackupsSurvive_AndUndoRestores()
    {
        using var env = NewEnv();
        var (id, token) = await PreviewedAsync(env);
        var before = TreeSnapshot(env);
        var dir = env.DirOf(id);
        int writes = 0;
        env.Service.StepProbe = FailSecondFileAndEveryRestore(() => writes, () => writes++);

        var result = env.Service.Apply(id, token);

        ImportEnv.AssertRefused(result, "apply_failed");
        Assert.Contains("backups", result.Error);
        Assert.Contains("import_undo", result.Error);
        var status = (ImportStatusResult)env.Service.GetStatus(id);
        Assert.Equal(ImportStatus.ApplyInterrupted, status.Status);       // never "ready" with a half changed tree
        Assert.True(File.Exists(Path.Combine(dir, "backup", "B.img")), "the backup of the replaced original must survive");
        Assert.True(new FileInfo(Path.Combine(dir, "journal.jsonl")).Length > 0);
        Assert.True(File.Exists(env.LockFile), "the lock stays while the tree may be half changed");
        Assert.NotEqual(before, TreeSnapshot(env));                       // the tree really is half changed

        // the discard path must not delete it either, and cleaning up after a long time must not delete it
        ImportEnv.AssertRefused(env.Service.Preview(id), "bad_state");
        env.Clock.Advance(TimeSpan.FromDays(30));
        env.Service.CleanupExpired();
        Assert.True(File.Exists(Path.Combine(dir, "backup", "B.img")));

        // another import may not be applied on top of a half changed tree
        var other = await ReadyAsync(env, Direct + "&other=1");
        var otherToken = env.Preview(other).Token;
        ImportEnv.AssertRefused(env.Service.Apply(other, otherToken), "interrupted_import_pending");

        // an undo that fails again leaves everything where it was, the lock included
        var failedUndo = env.Service.Undo(id);
        Assert.False(failedUndo.Success);
        Assert.True(File.Exists(Path.Combine(dir, "backup", "B.img")));
        Assert.True(File.Exists(env.LockFile));
        Assert.Equal(ImportStatus.ApplyInterrupted, ((ImportStatusResult)env.Service.GetStatus(id)).Status);

        // once restoring works, undo puts every original back from the backups and then (only then) removes the staging
        env.Service.StepProbe = null;
        var undo = Assert.IsType<ImportUndoResult>(env.Service.Undo(id));
        Assert.True(undo.Success, undo.Error);
        Assert.Equal("undone", undo.Outcome);
        Assert.Equal(before, TreeSnapshot(env));
        Assert.False(File.Exists(env.LockFile));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task F1_AnUndoThatCannotFindABackup_KeepsTheStaging()
    {
        using var env = NewEnv();
        var (id, token) = await PreviewedAsync(env);
        int writes = 0;
        env.Service.StepProbe = FailSecondFileAndEveryRestore(() => writes, () => writes++);
        Assert.False(env.Service.Apply(id, token).Success);
        env.Service.StepProbe = null;
        File.Delete(Path.Combine(env.DirOf(id), "backup", "B.img"));   // the only copy of the original is lost

        var undo = Assert.IsType<ImportUndoResult>(env.Service.Undo(id));

        Assert.Equal("partly_undone", undo.Outcome);
        Assert.Contains(undo.Skipped!, s => s.Path == "B.img" && s.Reason!.Contains("backup"));
        Assert.True(Directory.Exists(env.DirOf(id)));
        Assert.Equal(ImportStatus.UndonePartial, ((ImportStatusResult)env.Service.GetStatus(id)).Status);
    }

    [Fact]
    public async Task F1_AnImportThatStillHasUndoData_IsRestored_NotDiscarded()
    {
        using var env = NewEnv();
        var (id, token) = await PreviewedAsync(env);
        var before = TreeSnapshot(env);
        int writes = 0;
        env.Service.StepProbe = FailSecondFileAndEveryRestore(() => writes, () => writes++);
        Assert.False(env.Service.Apply(id, token).Success);
        env.Service.StepProbe = null;
        // a state file that wrongly says "ready" must not make undo throw the backups away
        var statePath = Path.Combine(env.DirOf(id), "state.json");
        File.WriteAllText(statePath, File.ReadAllText(statePath).Replace("\"Status\":\"apply_interrupted\"", "\"Status\":\"ready\""));
        var restarted = env.NewService();

        var undo = Assert.IsType<ImportUndoResult>(restarted.Undo(id));

        Assert.Equal("undone", undo.Outcome);
        Assert.Equal(before, TreeSnapshot(env));
    }

    // ---- F2: the lock is taken before anything changes ----

    [Fact]
    public async Task F2_AFolderAtTheLockPath_RefusesTheApply_AndLeavesTheImportReady_WithTheTokenUnspent()
    {
        using var env = NewEnv();
        var (id, token) = await PreviewedAsync(env);
        Directory.CreateDirectory(env.LockFile);
        var before = TreeSnapshot(env);

        ImportEnv.AssertRefused(env.Service.Apply(id, token), "lock_failed");

        Assert.Equal(ImportStatus.Ready, ((ImportStatusResult)env.Service.GetStatus(id)).Status);   // not stranded in "applying"
        Assert.Equal(before, TreeSnapshot(env));
        Assert.False(File.Exists(Path.Combine(env.DirOf(id), "journal.jsonl")));
        Directory.Delete(env.LockFile);
        Assert.True(env.Service.Apply(id, token).Success);                                          // the same token still works
    }

    [Fact]
    public async Task F2_ASymbolicLinkAtTheLockPath_IsRefused()
    {
        using var env = NewEnv();
        var (id, token) = await PreviewedAsync(env);
        File.CreateSymbolicLink(env.LockFile, Path.Combine(env.Temp, "nowhere"));
        ImportEnv.AssertRefused(env.Service.Apply(id, token), "path_not_allowed");
        Assert.Equal(ImportStatus.Ready, ((ImportStatusResult)env.Service.GetStatus(id)).Status);
        Assert.False(File.Exists(Path.Combine(env.Temp, "nowhere")));
    }

    [Fact]
    public async Task F2_AnErrorTakingOrRemovingTheOldLock_RefusesTheApply_AndTheUndo_WithoutChangingTheState()
    {
        using var env = NewEnv();
        var (id, token) = await PreviewedAsync(env);
        File.WriteAllText(env.LockFile, "{}");   // an old lock that cannot be removed
        env.Service.LockProbe = _ => throw new UnauthorizedAccessException("read-only");

        ImportEnv.AssertRefused(env.Service.Apply(id, token), "lock_failed");
        Assert.Equal(ImportStatus.Ready, ((ImportStatusResult)env.Service.GetStatus(id)).Status);

        env.Service.LockProbe = null;
        Assert.True(env.Service.Apply(id, token).Success);

        env.Service.LockProbe = _ => throw new IOException("disk");
        ImportEnv.AssertRefused(env.Service.Undo(id), "lock_failed");
        Assert.Equal(ImportStatus.Applied, ((ImportStatusResult)env.Service.GetStatus(id)).Status);
        env.Service.LockProbe = null;
        Assert.True(env.Service.Undo(id).Success);
    }

    // ---- F3: an applied import holds no place ----

    [Fact]
    public async Task F3_AnAppliedImport_KeepsOnlyBackupAndJournal_AndStillUndoes()
    {
        using var env = NewEnv(o => new ImportOptions { MaxStaged = 1 });
        var (id, token) = await PreviewedAsync(env);
        var before = TreeSnapshot(env);
        Assert.True(env.Service.Apply(id, token).Success);
        var dir = env.DirOf(id);
        Assert.False(Directory.Exists(Path.Combine(dir, "extracted")));
        Assert.True(Directory.Exists(Path.Combine(dir, "backup")));

        // with a limit of one, a second import can be fetched although the first one is applied
        var second = await ReadyAsync(env, Direct + "&second=1");
        Assert.NotNull(second);

        Assert.True(env.Service.Undo(id).Success);
        Assert.Equal(before, TreeSnapshot(env));
    }

    // ---- F5: a clean rollback leaves no undo data behind ----

    [Fact]
    public async Task F5_AfterACleanRollback_JournalAndBackupAreGone_AndALaterApplyAndUndoAreExact()
    {
        using var env = NewEnv();
        var (id, token) = await PreviewedAsync(env);
        var before = TreeSnapshot(env);
        var dir = env.DirOf(id);
        int writes = 0;
        env.Service.StepProbe = what => { if (what.StartsWith("apply:") && ++writes == 2) throw new IOException("copy failed"); };

        ImportEnv.AssertRefused(env.Service.Apply(id, token), "apply_failed");

        Assert.Equal(before, TreeSnapshot(env));
        Assert.Equal(ImportStatus.Ready, ((ImportStatusResult)env.Service.GetStatus(id)).Status);
        Assert.False(File.Exists(Path.Combine(dir, "journal.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(dir, "backup")));
        Assert.False(File.Exists(env.LockFile));

        env.Service.StepProbe = null;
        Assert.True(env.Service.Apply(id, env.Preview(id).Token).Success);
        var undo = Assert.IsType<ImportUndoResult>(env.Service.Undo(id));
        Assert.Equal("undone", undo.Outcome);               // not "partly_undone" because of entries of the first attempt
        Assert.Equal(2, undo.RestoredCount);
        Assert.Equal(2, undo.RemovedCount);
        Assert.Equal(0, undo.SkippedCount);
        Assert.Equal(before, TreeSnapshot(env));
    }

    // ---- F6: leftover temporary files ----

    [Fact]
    public async Task F6_LeftoverTemporaryFiles_AreRemoved_OnlyWithOurExactNamePattern_InFoldersTheImportTouches()
    {
        using var env = NewEnv();
        var (id, token) = await PreviewedAsync(env);
        string Tmp() => ".import-" + Guid.NewGuid().ToString("N") + ".tmp";
        var strayTop = Path.Combine(Skill(env), Tmp());
        var straySub = Path.Combine(Skill(env, "SUB"), Tmp());
        File.WriteAllText(strayTop, "x");
        File.WriteAllText(straySub, "x");
        var other1 = Path.Combine(Skill(env), ".import-short.tmp");
        var other2 = Path.Combine(Skill(env), Tmp() + ".bak");
        var other3 = Path.Combine(Skill(env), "mine.tmp");
        foreach (var f in new[] { other1, other2, other3 }) File.WriteAllText(f, "keep");
        Directory.CreateDirectory(Skill(env, "Untouched"));
        var untouched = Path.Combine(Skill(env, "Untouched"), Tmp());
        File.WriteAllText(untouched, "x");

        Assert.True(env.Service.Apply(id, token).Success);

        Assert.False(File.Exists(strayTop));
        Assert.False(File.Exists(straySub));
        Assert.All(new[] { other1, other2, other3, untouched }, f => Assert.True(File.Exists(f), f));

        // undo cleans up too
        var strayAgain = Path.Combine(Skill(env, "SUB"), Tmp());
        File.WriteAllText(strayAgain, "x");
        Assert.True(env.Service.Undo(id).Success);
        Assert.False(File.Exists(strayAgain));
        Assert.True(File.Exists(other3));
    }

    // ---- F7: category and file name must agree ----

    [Theory]
    [InlineData("Mob.wz")]
    [InlineData("mob.wz")]
    [InlineData("Sound.wz")]
    [InlineData("List.wz")]
    public async Task F7_GivenCategoryAndALinkNamingAnotherKnownCategory_IsRefused_BeforeTheBody(string fileName)
    {
        using var env = NewEnv();
        env.Web.ServeFile(Direct, _data.V2Wz, fileName);
        var status = await env.FetchAndWait(Direct, "wz", null, "Skill");
        Assert.Equal(ImportStatus.Failed, status.Status);
        Assert.Equal("category_mismatch", status.JobErrorCode);
        Assert.Equal(0, status.BytesDownloaded);
    }

    [Theory]
    [InlineData("Skill.wz")]
    [InlineData("SKILL.WZ")]
    [InlineData("Skill (1).wz")]       // a renamed copy: the given category decides
    [InlineData("download.bin")]
    [InlineData(null)]
    public async Task F7_GivenCategoryThatAgreesOrIsNotContradicted_IsAccepted(string? fileName)
    {
        using var env = NewEnv();
        var id = await ReadyAsync(env, Direct, fileName, "Skill");
        Assert.Equal("Skill", ((ImportStatusResult)env.Service.GetStatus(id)).Category);
    }

    // ---- F8: unsaved edits are checked again right before each file ----

    [Fact]
    public async Task F8_AnEditThatStartsWhileTheApplyRuns_StopsItBeforeThatFile_AndEverythingIsPutBack()
    {
        using var env = NewEnv();
        var (id, token) = await PreviewedAsync(env);
        var before = TreeSnapshot(env);
        var modify = new ModifyTools(env.Session);
        env.Service.StepProbe = what =>
        {
            // after the first file (B.img) is done, someone starts editing a later one
            if (what == "apply:B.img")
                MarkdownTestHelper.AssertSuccess(modify.SetString("Skill", "SUB/X.img", "who", "edited meanwhile"));
        };

        var result = env.Service.Apply(id, token);

        ImportEnv.AssertRefused(result, "unsaved_edits");
        Assert.Contains("SUB/X.img", result.Error);
        Assert.Equal(before, TreeSnapshot(env));
        Assert.Equal(ImportStatus.Ready, ((ImportStatusResult)env.Service.GetStatus(id)).Status);
        Assert.False(File.Exists(env.LockFile));
    }

    // ---- Info: the clean-up takes the step lock ----

    [Fact]
    public async Task CleanupExpired_DoesNothingWhileAStepRuns_AndRunsAfterwards()
    {
        using var env = NewEnv();
        var old = await ReadyAsync(env);
        var (id, token) = await PreviewedAsync(env, Direct + "&second=1");

        var inside = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        env.Service.StepProbe = _ => { inside.Set(); release.Wait(TimeSpan.FromSeconds(30)); };
        var apply = Task.Run(() => env.Service.Apply(id, token));
        Assert.True(inside.Wait(TimeSpan.FromSeconds(30)));

        env.Clock.Advance(TimeSpan.FromDays(8));                // "old" is expired while the apply is running
        Assert.Equal(0, env.Service.CleanupExpired());          // a step is running: nothing is deleted
        Assert.True(Directory.Exists(env.DirOf(old)));

        release.Set();
        Assert.True((await apply).Success);
        env.Service.StepProbe = null;
        Assert.True(env.Service.CleanupExpired() >= 1);
        Assert.False(Directory.Exists(env.DirOf(old)));
    }

    private async Task<(string Id, string Token)> PreviewedAsync(ImportEnv env, string route)
    {
        var id = await ReadyAsync(env, route);
        return (id, env.Preview(id).Token!);
    }

    // ---- Q4: googleusercontent only as a redirect target ----

    [Fact]
    public async Task Q4_ARedirectToGoogleusercontent_StillWorks_ButNotAsTheFirstLink()
    {
        using var env = NewEnv();
        var png = PngMaker.Png();
        const string target = "https://doc-0s-ab-docs.googleusercontent.com/docs/securesc/file";
        env.Web.Redirect(Direct, target);
        env.Web.ServeFile(target, png, "a.png");
        Assert.Equal(ImportStatus.Ready, (await env.FetchAndWait(Direct, "files")).Status);
        ImportEnv.AssertRefused(env.Service.Fetch(target, "files", null), "url_not_allowed");
    }
}
#endif
