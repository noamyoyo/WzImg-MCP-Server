#if LINUX_BUILD
using System.Net;
using System.Text.Json;
using WzImgMCP.Import;
using WzImgMCP.Tools;

namespace WzImgMCP.Tests;

/// <summary>import_fetch kind wz, import_status, import_preview, import_apply and import_undo on real (synthetic) .wz files.</summary>
[Collection("AllowedRootEnv")]
public sealed class ImportWzFlowTests : IClassFixture<ImportWzData>
{
    private const string Direct = ImportEnv.DirectLink;
    private readonly ImportWzData _data;

    public ImportWzFlowTests(ImportWzData data) { _data = data; }

    private ImportEnv NewEnv(Func<ImportOptions, ImportOptions>? tweak = null) => new(tweak, copyTreeFrom: _data.TreeV1);

    private async Task<string> ReadyAsync(ImportEnv env, byte[]? wz = null, string? name = "Skill.wz", string? category = null)
    {
        env.Web.ServeFile(Direct, wz ?? _data.V2Wz, name);
        var status = await env.FetchAndWait(ImportEnv.ViewLink, "wz", null, category);
        Assert.True(status.Status == ImportStatus.Ready, $"status {status.Status} {status.JobErrorCode} {status.JobError}");
        return status.Id!;
    }

    private async Task<(string Id, ImportPreviewResult Preview)> PreviewedAsync(ImportEnv env)
    {
        var id = await ReadyAsync(env);
        var preview = env.Preview(id);
        Assert.True(preview.Success, preview.Error);
        Assert.NotNull(preview.Token);
        return (id, preview);
    }

    /// <summary>The folder the images were unpacked to (the extractor spells it as it likes; the service finds it ignoring case).</summary>
    private static string StagedDir(ImportEnv env, string id) => Directory.GetDirectories(Path.Combine(env.DirOf(id), "extracted")).Single();

    private static string Skill(ImportEnv env, params string[] parts) => Path.Combine(new[] { env.Tree, "Skill" }.Concat(parts).ToArray());

    private static Dictionary<string, string> TreeSnapshot(ImportEnv env) => ImportEnv.Snapshot(env.Tree);

    // ---- fetch ----

    [Fact]
    public async Task Fetch_Wz_ExtractsIntoTheInbox_AndTouchesNothingInTheTree()
    {
        using var env = NewEnv();
        var before = TreeSnapshot(env);

        var id = await ReadyAsync(env);
        var status = Assert.IsType<ImportStatusResult>(env.Service.GetStatus(id));

        Assert.Equal("Skill", status.Category);
        Assert.Equal("Skill.wz", status.FileName);
        Assert.Equal(5, status.ImagesInSource);                // A, B, C, Sub/X, Sub/Y
        Assert.Contains("import_preview", status.NextStep);
        Assert.Equal(before, TreeSnapshot(env));
        Assert.False(File.Exists(env.LockFile));
        Assert.True(Directory.Exists(StagedDir(env, id)));
        Assert.False(Directory.Exists(Path.Combine(env.DirOf(id), "src")));   // the .wz itself is dropped after extraction
        Assert.StartsWith(env.Inbox, env.DirOf(id));
    }

    [Fact]
    public async Task Fetch_Wz_NeedsAnOpenDataSource()
    {
        using var env = new ImportEnv();   // no tree
        ImportEnv.AssertRefused(env.Service.Fetch(ImportEnv.ViewLink, "wz", null), "no_data_source");
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("List.wz", "category_refused")]
    [InlineData("Sound.wz", "category_refused")]
    [InlineData("sound.WZ", "category_refused")]
    [InlineData("Weird.wz", "category_unknown")]            // not in the tree
    [InlineData("Skill.txt", "not_a_wz")]
    [InlineData(null, "category_unknown")]                   // the link did not say what the file is
    public async Task Fetch_Wz_RefusesTheWrongCategory_BeforeReadingTheBody(string? fileName, string code)
    {
        using var env = NewEnv();
        env.Web.ServeFile(Direct, _data.V2Wz, fileName);
        var status = await env.FetchAndWait(Direct, "wz");
        Assert.Equal(ImportStatus.Failed, status.Status);
        Assert.Equal(code, status.JobErrorCode);
        Assert.Equal(0, status.BytesDownloaded);
    }

    [Fact]
    public void Fetch_Wz_CategoryGivenUpFront_IsCheckedAtOnce()
    {
        using var env = NewEnv();
        ImportEnv.AssertRefused(env.Service.Fetch(Direct, "wz", null, "List"), "category_refused");
        ImportEnv.AssertRefused(env.Service.Fetch(Direct, "wz", null, "sound"), "category_refused");
        ImportEnv.AssertRefused(env.Service.Fetch(Direct, "wz", null, "Nope"), "category_unknown");
        ImportEnv.AssertRefused(env.Service.Fetch(Direct, "wz", null, "../Skill"), "category_unknown");
        Assert.Empty(env.Web.Requests);
    }

    [Fact]
    public async Task Fetch_Wz_CategoryGiven_IsUsedWhenTheLinkHasNoName_AndMatchesTheTreeSpelling()
    {
        using var env = NewEnv();
        var id = await ReadyAsync(env, name: null, category: "skill");
        Assert.Equal("Skill", ((ImportStatusResult)env.Service.GetStatus(id)).Category);
    }

    [Fact]
    public async Task Fetch_Wz_UsesTheTreesSpellingOfTheCategory_WhateverTheFileIsCalled()
    {
        using var env = new ImportEnv(copyTreeFrom: _data.TreeV1, prepareTree: tree => Directory.Move(Path.Combine(tree, "Skill"), Path.Combine(tree, "skill")));
        var id = await ReadyAsync(env, name: "SKILL.wz");
        Assert.Equal("skill", ((ImportStatusResult)env.Service.GetStatus(id)).Category);
        var preview = env.Preview(id);
        Assert.Equal(2, preview.NewCount);
        Assert.True(env.Service.Apply(id, preview.Token).Success);
        Assert.True(File.Exists(Path.Combine(env.Tree, "skill", "C.img")));
        Assert.False(Directory.Exists(Path.Combine(env.Tree, "Skill")));
    }

    [Fact]
    public async Task Fetch_Wz_WithAnotherKeyThanGms_IsRefused()
    {
        using var env = NewEnv();
        env.Web.ServeFile(Direct, _data.V2WzBms, "Skill.wz");
        var status = await env.FetchAndWait(Direct, "wz");
        Assert.Equal("not_gms", status.JobErrorCode);
        Assert.Contains("GMS", status.JobError);
        Assert.False(Directory.Exists(Path.Combine(env.DirOf(status.Id!), "extracted")));
    }

    [Fact]
    public async Task Fetch_Wz_ThatIsNotAWzFile_IsRefused()
    {
        using var env = NewEnv();
        env.Web.ServeFile(Direct, new byte[5000], "Skill.wz");
        var status = await env.FetchAndWait(Direct, "wz");
        Assert.Equal("not_a_wz", status.JobErrorCode);
    }

    [Fact]
    public async Task Fetch_Wz_ChecksTheHash_BeforeUnpacking()
    {
        using var env = NewEnv();
        env.Web.ServeFile(Direct, _data.V2Wz, "Skill.wz");
        var status = await env.FetchAndWait(Direct, "wz", new string('a', 64));
        Assert.Equal("hash_mismatch", status.JobErrorCode);
        Assert.False(Directory.Exists(Path.Combine(env.DirOf(status.Id!), "extracted")));
    }

    [Fact]
    public async Task Fetch_Wz_RefusesAnInboxThatIsASymbolicLink()
    {
        using var env = NewEnv();
        var outside = Directory.CreateDirectory(Path.Combine(env.Temp, "outside")).FullName;
        Directory.CreateSymbolicLink(env.Inbox, outside);
        env.Web.ServeFile(Direct, _data.V2Wz, "Skill.wz");
        ImportEnv.AssertRefused(env.Service.Fetch(ImportEnv.ViewLink, "wz", null), "path_not_allowed");
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        await Task.CompletedTask;
    }

    // ---- preview ----

    [Fact]
    public async Task Preview_ListsNewChangedUnchangedAndTreeOnly_AndChangesNothing()
    {
        using var env = NewEnv();
        var id = await ReadyAsync(env);
        var before = TreeSnapshot(env);

        var preview = env.Preview(id);

        Assert.True(preview.Success, preview.Error);
        Assert.Equal("Skill", preview.Category);
        Assert.Equal(2, preview.NewCount);
        Assert.Equal(new[] { "C.img", "SUB/Y.img" }, preview.NewImages);           // the new image goes into the tree's spelling of the folder
        Assert.Equal(2, preview.ChangedCount);
        Assert.Equal(new[] { "B.img", "SUB/X.img" }, preview.ChangedImages!.Select(c => c.Path).ToArray());
        Assert.Equal(1, preview.UnchangedCount);                                    // A
        Assert.Equal(0, preview.UnreadableCount);
        Assert.Equal(1, preview.LeftAloneCount);                                    // D is in the tree only
        Assert.Equal(new[] { "D.img" }, preview.LeftAloneImages);
        Assert.False(string.IsNullOrEmpty(preview.Token));
        Assert.Contains("import_apply", preview.NextStep);

        var b = preview.ChangedImages!.Single(c => c.Path == "B.img");
        Assert.Equal(1, b.Added);       // extra
        Assert.Equal(1, b.Removed);     // old
        Assert.Equal(1, b.Changed);     // who
        Assert.Contains(b.Entries!, e => e.Path == "extra" && e.Kind == "added");
        Assert.Contains(b.Entries!, e => e.Path == "old" && e.Kind == "removed");
        Assert.Contains(b.Entries!, e => e.Path == "who" && e.Kind == "changed");

        Assert.Equal(before, TreeSnapshot(env));
        Assert.False(File.Exists(env.LockFile));
    }

    [Fact]
    public async Task Preview_WithAnUnreadableImage_GivesNoToken_UnlessAllowUnreadable_AndApplySaysWhatWasSkipped()
    {
        using var env = NewEnv();
        var id = await ReadyAsync(env);
        File.WriteAllBytes(Path.Combine(StagedDir(env, id), "C.img"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var plain = env.Preview(id);

        Assert.True(plain.Success, plain.Error);
        Assert.Equal(1, plain.UnreadableCount);
        Assert.Equal("C.img", plain.UnreadableImages!.Single().Path);
        Assert.Equal(1, plain.NewCount);   // only Sub/Y
        Assert.Null(plain.Token);
        Assert.Contains("allowUnreadable", plain.NextStep);
        ImportEnv.AssertRefused(env.Service.Apply(id, "anything"), "token_invalid");

        var accepted = env.Preview(id, allowUnreadable: true);
        Assert.False(string.IsNullOrEmpty(accepted.Token));
        Assert.Contains("NOT copied", string.Join(" ", accepted.Warnings!));

        // a later plain preview takes the earlier token away
        var again = env.Preview(id);
        Assert.Null(again.Token);
        ImportEnv.AssertRefused(env.Service.Apply(id, accepted.Token), "token_invalid");

        var final = env.Preview(id, allowUnreadable: true);
        var apply = Assert.IsType<ImportApplyResult>(env.Service.Apply(id, final.Token));
        Assert.True(apply.Success, apply.Error);
        Assert.Contains("C.img", Assert.Single(apply.SkippedUnreadable!));
        Assert.Contains("NOT copied", apply.Message);
        Assert.False(File.Exists(Skill(env, "C.img")));
        Assert.True(File.Exists(Skill(env, "SUB", "Y.img")));
    }

    [Fact]
    public async Task Preview_ASymbolicLinkWhereANewImageWouldGo_IsReported_AndNothingIsWrittenThroughIt()
    {
        using var env = NewEnv();
        var outside = Path.Combine(env.Temp, "outside.img");
        File.WriteAllText(outside, "precious");
        File.CreateSymbolicLink(Skill(env, "C.img"), outside);
        var id = await ReadyAsync(env);

        var preview = env.Preview(id, allowUnreadable: true);

        Assert.Contains(preview.UnreadableImages!, u => u.Path == "C.img");
        env.Service.Apply(id, preview.Token);
        Assert.Equal("precious", File.ReadAllText(outside));
    }

    [Fact]
    public async Task Preview_NothingToApply_GivesNoToken()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        Assert.True(env.Service.Apply(id, preview.Token).Success);

        var second = await ReadyAsync(env);   // the same file again
        var again = env.Preview(second);

        Assert.True(again.Success);
        Assert.Equal(0, again.NewCount);
        Assert.Equal(0, again.ChangedCount);
        Assert.Equal(5, again.UnchangedCount);
        Assert.Null(again.Token);
        Assert.Contains("Nothing would change", again.NextStep);
        ImportEnv.AssertRefused(env.Service.Apply(second, "anything"), "token_invalid");
    }

    [Fact]
    public async Task Preview_OfLooseFiles_OrBeforeReady_IsRefused()
    {
        using var env = NewEnv();
        env.Web.ServeFile(Direct, PngMaker.Png(), "a.png");
        var files = await env.FetchAndWait(Direct, "files");
        ImportEnv.AssertRefused(env.Service.Preview(files.Id), "wrong_kind");
        ImportEnv.AssertRefused(env.Service.Apply(files.Id, "x"), "wrong_kind");

        var gate = new TaskCompletionSource();
        env.Web.Route(Direct + "&slow=1", async (_, ct) => { await gate.Task.WaitAsync(ct); return FakeWeb.File(_data.V2Wz, "Skill.wz"); });
        env.Service.Undo(files.Id);
        var fetch = Assert.IsType<ImportFetchResult>(env.Service.Fetch(Direct + "&slow=1", "wz", null));
        ImportEnv.AssertRefused(env.Service.Preview(fetch.Id), "bad_state");
        ImportEnv.AssertRefused(env.Service.Apply(fetch.Id, "x"), "bad_state");
        gate.SetResult();
        await env.Service.WaitForJobAsync(fetch.Id!, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Preview_CutsLongLists_ButCountsEverything()
    {
        using var env = NewEnv(o => new ImportOptions { PreviewMaxNames = 1, PreviewMaxImages = 1, PreviewMaxEntries = 1 });
        var id = await ReadyAsync(env);
        var preview = env.Preview(id);
        Assert.True(preview.Truncated);
        Assert.Equal(2, preview.NewCount);
        Assert.Single(preview.NewImages!);
        Assert.Equal(2, preview.ChangedCount);
        Assert.Single(preview.ChangedImages!);
        Assert.Single(preview.ChangedImages![0].Entries!);
        Assert.True(preview.ChangedImages[0].EntriesTruncated);
        Assert.Contains("counts are complete", string.Join(" ", preview.Warnings!));
    }

    // ---- apply ----

    [Fact]
    public async Task Apply_CopiesOnlyNewAndChangedImages_BacksUp_AndLeavesTheRestAlone()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        var before = TreeSnapshot(env);
        var oldB = File.ReadAllBytes(Skill(env, "B.img"));
        var oldX = File.ReadAllBytes(Skill(env, "SUB", "X.img"));
        var staged = StagedDir(env, id);
        var stagedBytes = Directory.GetFiles(staged, "*.img", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(staged, f), File.ReadAllBytes);

        var apply = Assert.IsType<ImportApplyResult>(env.Service.Apply(id, preview.Token));

        Assert.True(apply.Success, apply.Error);
        Assert.Equal(2, apply.AddedCount);
        Assert.Equal(2, apply.ReplacedCount);
        var after = TreeSnapshot(env);
        // changed and new images carry the staged bytes
        Assert.Equal(stagedBytes["B.img"], File.ReadAllBytes(Skill(env, "B.img")));
        Assert.Equal(stagedBytes["C.img"], File.ReadAllBytes(Skill(env, "C.img")));
        // the tree's own spelling of the folder is kept: no second "Sub" next to "SUB"
        Assert.Equal(stagedBytes[Path.Combine("Sub", "X.img")], File.ReadAllBytes(Skill(env, "SUB", "X.img")));
        Assert.Equal(stagedBytes[Path.Combine("Sub", "Y.img")], File.ReadAllBytes(Skill(env, "SUB", "Y.img")));
        Assert.False(Directory.Exists(Skill(env, "Sub")));
        // exactly these four paths differ; A, D (tree only), manifest.json and every sidecar are byte-identical
        var differing = after.Keys.Union(before.Keys).Where(k => !before.TryGetValue(k, out var h) || !after.TryGetValue(k, out var h2) || h != h2).OrderBy(k => k).ToArray();
        Assert.Equal(new[] { Path.Combine("Skill", "B.img"), Path.Combine("Skill", "C.img"), Path.Combine("Skill", "SUB", "X.img"), Path.Combine("Skill", "SUB", "Y.img") }, differing);
        Assert.True(File.Exists(Skill(env, "D.img")));
        // backups of what was replaced (and nothing for new images)
        var backup = Path.Combine(env.DirOf(id), "backup");
        Assert.Equal(oldB, File.ReadAllBytes(Path.Combine(backup, "B.img")));
        Assert.Equal(oldX, File.ReadAllBytes(Path.Combine(backup, "SUB", "X.img")));
        Assert.False(File.Exists(Path.Combine(backup, "C.img")));
        // no half-written files, no lock
        Assert.Empty(Directory.EnumerateFiles(env.Tree, ".import-*", SearchOption.AllDirectories));
        Assert.False(File.Exists(env.LockFile));
        // the unpacked images are dropped; only the backups and the journal stay for undo
        Assert.False(Directory.Exists(Path.Combine(env.DirOf(id), "extracted")));
        Assert.True(File.Exists(Path.Combine(env.DirOf(id), "journal.jsonl")));
        // the editor sees the new image straight away
        Assert.True(env.Session.DataSource.ImageExists("Skill", "C.img"));
        Assert.Equal("B-new", MarkdownTestHelper.GetFirstValue(new PropertyTools(env.Session).GetString("Skill", "B.img", "who"), "value"));

        var status = (ImportStatusResult)env.Service.GetStatus(id);
        Assert.Equal(ImportStatus.Applied, status.Status);
        Assert.Equal(2, status.AppliedAdded);
    }

    [Fact]
    public async Task Apply_WithoutAToken_OrWithAWrongOne_IsRefused()
    {
        using var env = NewEnv();
        var id = await ReadyAsync(env);
        var before = TreeSnapshot(env);
        ImportEnv.AssertRefused(env.Service.Apply(id, null), "token_invalid");
        ImportEnv.AssertRefused(env.Service.Apply(id, " "), "token_invalid");
        ImportEnv.AssertRefused(env.Service.Apply(id, "made-up-token"), "token_invalid");   // no preview yet
        var preview = env.Preview(id);
        ImportEnv.AssertRefused(env.Service.Apply(id, "made-up-token"), "token_invalid");
        ImportEnv.AssertRefused(env.Service.Apply(id, preview.Token + "x"), "token_invalid");
        Assert.Equal(before, TreeSnapshot(env));
        Assert.True(env.Service.Apply(id, preview.Token).Success);   // the right token still works after wrong guesses
    }

    [Fact]
    public async Task Token_WorksOnce()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        Assert.True(env.Service.Apply(id, preview.Token).Success);
        ImportEnv.AssertRefused(env.Service.Apply(id, preview.Token), "bad_state");   // already applied
        // and after an undo the staged import is gone, so the token cannot be used at all
        Assert.True(env.Service.Undo(id).Success);
        ImportEnv.AssertRefused(env.Service.Apply(id, preview.Token), "not_found");
    }

    [Fact]
    public async Task Token_FromAnEarlierPreview_IsStale()
    {
        using var env = NewEnv();
        var id = await ReadyAsync(env);
        var first = env.Preview(id);
        var second = env.Preview(id);
        Assert.NotEqual(first.Token, second.Token);
        ImportEnv.AssertRefused(env.Service.Apply(id, first.Token), "token_invalid");
        Assert.True(env.Service.Apply(id, second.Token).Success);
    }

    [Fact]
    public async Task Token_OfAnotherImport_DoesNotWork()
    {
        using var env = NewEnv();
        var (a, previewA) = await PreviewedAsync(env);
        var b = await ReadyAsync(env);
        var previewB = env.Preview(b);
        ImportEnv.AssertRefused(env.Service.Apply(b, previewA.Token), "token_invalid");
        ImportEnv.AssertRefused(env.Service.Apply(a, previewB.Token), "token_invalid");
        Assert.True(env.Service.Apply(a, previewA.Token).Success);
    }

    [Fact]
    public async Task Apply_Refuses_WhenATreeFileChangedSinceThePreview()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        File.AppendAllText(Skill(env, "B.img"), "edited");   // someone else changed B after the preview
        var before = TreeSnapshot(env);

        var result = env.Service.Apply(id, preview.Token);

        ImportEnv.AssertRefused(result, "tree_changed");
        Assert.Contains("B.img", result.Error);
        Assert.Equal(before, TreeSnapshot(env));                       // nothing was written
        Assert.False(File.Exists(env.LockFile));
        // the old token stays useless against a changed tree; a fresh preview sees the new state
        ImportEnv.AssertRefused(env.Service.Apply(id, preview.Token), "tree_changed");
        var fresh = env.Preview(id);
        Assert.True(env.Service.Apply(id, fresh.Token).Success);
    }

    [Fact]
    public async Task Apply_Refuses_WhenANewImageAppearedInTheTree_OrAChangedOneDisappeared()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        File.WriteAllBytes(Skill(env, "C.img"), ImportWzData.ImgBytes("someone else made C"));
        ImportEnv.AssertRefused(env.Service.Apply(id, preview.Token), "tree_changed");
        File.Delete(Skill(env, "C.img"));
        File.Delete(Skill(env, "B.img"));
        ImportEnv.AssertRefused(env.Service.Apply(id, preview.Token), "tree_changed");
        Assert.False(File.Exists(Skill(env, "B.img")));   // not recreated
    }

    [Fact]
    public async Task Apply_Refuses_WhenAStagedFileWasTamperedWith()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        File.AppendAllText(Path.Combine(StagedDir(env, id), "B.img"), "x");
        ImportEnv.AssertRefused(env.Service.Apply(id, preview.Token), "staging_damaged");
    }

    [Fact]
    public async Task Apply_Refuses_WhenTheEditorHoldsUnsavedEditsOfAnAffectedImage()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        var modify = new ModifyTools(env.Session);
        MarkdownTestHelper.AssertSuccess(modify.SetString("Skill", "B.img", "who", "unsaved"));
        var before = TreeSnapshot(env);

        var result = env.Service.Apply(id, preview.Token);

        ImportEnv.AssertRefused(result, "unsaved_edits");
        Assert.Contains("B.img", result.Error);
        Assert.Equal(before, TreeSnapshot(env));

        MarkdownTestHelper.AssertSuccess(modify.DiscardChanges("Skill", "B.img"));
        Assert.True(env.Service.Apply(id, preview.Token).Success);   // the same token still works: nothing was spent
    }

    [Fact]
    public async Task Apply_UnsavedEditsOfAnImageThatIsNotAffected_DoNotMatter()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        MarkdownTestHelper.AssertSuccess(new ModifyTools(env.Session).SetString("Skill", "D.img", "who", "unsaved"));
        Assert.True(env.Service.Apply(id, preview.Token).Success);
    }

    // ---- the lock file ----

    [Fact]
    public async Task Lock_ExistsWhileApplying_AndIsGoneAfterwards()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        var seen = new List<string>();
        env.Service.StepProbe = what =>
        {
            Assert.True(File.Exists(env.LockFile), "the lock file must exist while files are being written");
            seen.Add(File.ReadAllText(env.LockFile));
        };

        Assert.True(env.Service.Apply(id, preview.Token).Success);

        Assert.Equal(4, seen.Count);
        Assert.All(seen, text => Assert.Contains(id, text));
        Assert.False(File.Exists(env.LockFile));
        Assert.Equal(Root(env), Path.GetDirectoryName(env.LockFile));   // it sits in the allowed root, beside img/ and inbox/
    }

    private static string Root(ImportEnv env) => env.Root;

    [Fact]
    public async Task Lock_IsRemovedAndTheTreeRestored_WhenAnApplyFailsHalfWay()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        var before = TreeSnapshot(env);
        int writes = 0;
        env.Service.StepProbe = _ => { if (++writes == 2) throw new IOException("disk went away"); };

        var result = env.Service.Apply(id, preview.Token);

        ImportEnv.AssertRefused(result, "apply_failed");
        Assert.False(File.Exists(env.LockFile));
        Assert.Equal(before, TreeSnapshot(env));                       // every file and folder is back as it was
        Assert.Equal(ImportStatus.Ready, ((ImportStatusResult)env.Service.GetStatus(id)).Status);
        // the token was spent by the attempt; a new preview allows a clean second try
        ImportEnv.AssertRefused(env.Service.Apply(id, preview.Token), "token_invalid");
        env.Service.StepProbe = null;
        Assert.True(env.Service.Apply(id, env.Preview(id).Token).Success);
    }

    [Fact]
    public async Task Lock_AStaleFileFromACrash_IsTakenOver()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        File.WriteAllText(env.LockFile, "{\"import\":\"old\"}");
        Assert.True(env.Service.Apply(id, preview.Token).Success);
        Assert.False(File.Exists(env.LockFile));
    }

    [Fact]
    public async Task Lock_IsAlsoHeldWhileUndoing()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        Assert.True(env.Service.Apply(id, preview.Token).Success);
        bool held = false;
        env.Service.StepProbe = _ => held = File.Exists(env.LockFile);
        Assert.True(env.Service.Undo(id).Success);
        Assert.True(held);
        Assert.False(File.Exists(env.LockFile));
    }

    [Fact]
    public async Task Steps_DoNotRunAtTheSameTime()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        var inside = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        env.Service.StepProbe = _ => { inside.Set(); release.Wait(TimeSpan.FromSeconds(30)); };

        var apply = Task.Run(() => env.Service.Apply(id, preview.Token));
        Assert.True(inside.Wait(TimeSpan.FromSeconds(30)));

        ImportEnv.AssertRefused(env.Service.Preview(id), "bad_state");                     // it is being applied
        ImportEnv.AssertRefused(env.Service.Apply(id, preview.Token), "bad_state");
        ImportEnv.AssertRefused(env.Service.Undo(id), "busy");

        // a second import cannot start a step either while this one holds the step gate
        env.Web.ServeFile(Direct + "&two=1", _data.V2Wz, "Skill.wz");
        var other = await env.FetchAndWait(Direct + "&two=1", "wz");
        ImportEnv.AssertRefused(env.Service.Preview(other.Id), "busy");
        ImportEnv.AssertRefused(env.Service.Apply(other.Id, "t"), "busy");

        release.Set();
        Assert.True((await apply).Success);
    }

    // ---- undo ----

    [Fact]
    public async Task Undo_PutsBackTheOldFiles_RemovesAddedOnes_AndDeletesTheStaging()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        var before = TreeSnapshot(env);
        Assert.True(env.Service.Apply(id, preview.Token).Success);

        var undo = Assert.IsType<ImportUndoResult>(env.Service.Undo(id));

        Assert.True(undo.Success, undo.Error);
        Assert.Equal("undone", undo.Outcome);
        Assert.Equal(2, undo.RestoredCount);
        Assert.Equal(2, undo.RemovedCount);
        Assert.Equal(0, undo.SkippedCount);
        Assert.True(undo.StagingDeleted);
        Assert.Equal(before, TreeSnapshot(env));                 // exactly as before, folders included
        Assert.False(Directory.Exists(env.DirOf(id)));
        Assert.False(File.Exists(env.LockFile));
        ImportEnv.AssertRefused(env.Service.Undo(id), "not_found");
        ImportEnv.AssertRefused(env.Service.GetStatus(id), "not_found");
        Assert.False(env.Session.DataSource.ImageExists("Skill", "C.img"));
    }

    [Fact]
    public async Task Undo_SkipsAndListsFilesEditedAfterTheImport()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        var oldD = File.ReadAllBytes(Skill(env, "D.img"));
        var oldA = File.ReadAllBytes(Skill(env, "A.img"));
        var oldX = File.ReadAllBytes(Skill(env, "SUB", "X.img"));
        Assert.True(env.Service.Apply(id, preview.Token).Success);
        File.AppendAllText(Skill(env, "B.img"), "edited later");        // a replaced file edited afterwards
        File.AppendAllText(Skill(env, "C.img"), "edited later");        // an added file edited afterwards
        var editedB = File.ReadAllBytes(Skill(env, "B.img"));

        var undo = Assert.IsType<ImportUndoResult>(env.Service.Undo(id));

        Assert.True(undo.Success);
        Assert.Equal("partly_undone", undo.Outcome);
        Assert.Equal(2, undo.SkippedCount);
        Assert.Equal(new[] { "B.img", "C.img" }, undo.Skipped!.Select(s => s.Path).OrderBy(p => p).ToArray());
        Assert.All(undo.Skipped!, s => Assert.Contains("edited after the import", s.Reason));
        Assert.Equal(1, undo.RestoredCount);                           // SUB/X.img
        Assert.Equal(1, undo.RemovedCount);                            // SUB/Y.img
        Assert.Equal(editedB, File.ReadAllBytes(Skill(env, "B.img"))); // the edit survives
        Assert.True(File.Exists(Skill(env, "C.img")));
        Assert.Equal(oldX, File.ReadAllBytes(Skill(env, "SUB", "X.img")));
        Assert.False(File.Exists(Skill(env, "SUB", "Y.img")));
        Assert.Equal(oldA, File.ReadAllBytes(Skill(env, "A.img")));
        Assert.Equal(oldD, File.ReadAllBytes(Skill(env, "D.img")));
        Assert.False(File.Exists(env.LockFile));

        // the import stays on disk (its backups), does not count against the limit, and can be asked again
        var status = (ImportStatusResult)env.Service.GetStatus(id);
        Assert.Equal(ImportStatus.UndonePartial, status.Status);
        var again = Assert.IsType<ImportUndoResult>(env.Service.Undo(id));
        Assert.Equal(2, again.SkippedCount);
    }

    [Fact]
    public async Task Undo_OfAnImportThatWasNeverApplied_JustDeletesTheStaging()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        var before = TreeSnapshot(env);

        var undo = Assert.IsType<ImportUndoResult>(env.Service.Undo(id));

        Assert.Equal("discarded", undo.Outcome);
        Assert.Equal(before, TreeSnapshot(env));
        Assert.False(Directory.Exists(env.DirOf(id)));
        ImportEnv.AssertRefused(env.Service.Apply(id, preview.Token), "not_found");
    }

    [Fact]
    public async Task Undo_Refuses_WhenTheEditorHoldsUnsavedEditsOfAnAffectedImage()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        Assert.True(env.Service.Apply(id, preview.Token).Success);
        var modify = new ModifyTools(env.Session);
        MarkdownTestHelper.AssertSuccess(modify.SetString("Skill", "B.img", "who", "unsaved"));
        ImportEnv.AssertRefused(env.Service.Undo(id), "unsaved_edits");
        MarkdownTestHelper.AssertSuccess(modify.DiscardChanges("Skill", "B.img"));
        Assert.True(env.Service.Undo(id).Success);
    }

    [Fact]
    public async Task AnApplyThatWasInterrupted_CanStillBeUndone_AfterARestart()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        var before = TreeSnapshot(env);
        Assert.True(env.Service.Apply(id, preview.Token).Success);
        // pretend the server stopped while applying: the state says "applying", the journal is on disk
        var statePath = Path.Combine(env.DirOf(id), "state.json");
        File.WriteAllText(statePath, File.ReadAllText(statePath).Replace("\"Status\":\"applied\"", "\"Status\":\"applying\""));

        var restarted = env.NewService();
        var status = Assert.IsType<ImportStatusResult>(restarted.GetStatus(id));
        Assert.Equal(ImportStatus.ApplyInterrupted, status.Status);
        Assert.Contains("import_undo", status.NextStep);

        Assert.True(restarted.Undo(id).Success);
        Assert.Equal(before, TreeSnapshot(env));
    }

    [Fact]
    public async Task AnAppliedImport_SurvivesARestartOfTheService()
    {
        using var env = NewEnv();
        var (id, preview) = await PreviewedAsync(env);
        Assert.True(env.Service.Apply(id, preview.Token).Success);
        var restarted = env.NewService();
        var status = Assert.IsType<ImportStatusResult>(restarted.GetStatus(id));
        Assert.Equal(ImportStatus.Applied, status.Status);
        Assert.Equal(2, status.AppliedAdded);
        Assert.True(restarted.Undo(id).Success);
    }

    [Fact]
    public async Task ADownloadThatWasRunningWhenTheServerStopped_IsReportedAsFailed()
    {
        using var env = NewEnv();
        var id = await ReadyAsync(env);
        var statePath = Path.Combine(env.DirOf(id), "state.json");
        File.WriteAllText(statePath, File.ReadAllText(statePath).Replace("\"Status\":\"ready\"", "\"Status\":\"extracting\""));
        var status = Assert.IsType<ImportStatusResult>(env.NewService().GetStatus(id));
        Assert.Equal(ImportStatus.Failed, status.Status);
        Assert.Equal("interrupted", status.JobErrorCode);
    }

    // ---- clean-up after the retention time ----

    [Fact]
    public async Task Cleanup_DeletesAnImportAfterSevenDays_NotBefore()
    {
        using var env = NewEnv();
        var id = await ReadyAsync(env);
        env.Clock.Advance(TimeSpan.FromDays(6) + TimeSpan.FromHours(23));
        Assert.IsType<ImportStatusResult>(env.Service.GetStatus(id));
        Assert.True(Directory.Exists(env.DirOf(id)));

        env.Clock.Advance(TimeSpan.FromHours(2));
        ImportEnv.AssertRefused(env.Service.GetStatus(id), "not_found");
        Assert.False(Directory.Exists(env.DirOf(id)));
    }

    [Fact]
    public async Task Cleanup_CountsAnAppliedImportFromTheApply_AndAnAppliedImportHoldsNoPlace()
    {
        using var env = NewEnv(o => new ImportOptions { MaxStaged = 1 });
        var (id, preview) = await PreviewedAsync(env);
        env.Clock.Advance(TimeSpan.FromDays(5));
        Assert.True(env.Service.Apply(id, preview.Token).Success);
        env.Clock.Advance(TimeSpan.FromDays(5));                              // ten days since the fetch, five since the apply
        Assert.Equal(ImportStatus.Applied, ((ImportStatusResult)env.Service.GetStatus(id)).Status);

        // the applied import keeps only backup/ and the journal, and it does not count against the limit of staged imports
        Assert.False(Directory.Exists(Path.Combine(env.DirOf(id), "extracted")));
        env.Web.ServeFile(Direct, PngMaker.Png(), "a.png");
        var next = await env.FetchAndWait(ImportEnv.ViewLink, "files");
        Assert.Equal(ImportStatus.Ready, next.Status);
        ImportEnv.AssertRefused(env.Service.Fetch(ImportEnv.ViewLink, "files", null), "too_many_staged");   // this one does hold the place

        env.Clock.Advance(TimeSpan.FromDays(3));                              // eight days since the apply
        ImportEnv.AssertRefused(env.Service.GetStatus(id), "not_found");
        Assert.False(Directory.Exists(env.DirOf(id)));
    }

    [Fact]
    public void Cleanup_RemovesOldFoldersThatHaveNoStateFile()
    {
        using var env = NewEnv();
        var orphan = Path.Combine(env.Inbox, new string('b', 32));
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "junk"), "x");
        Directory.SetLastWriteTimeUtc(orphan, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var recent = Path.Combine(env.Inbox, new string('c', 32));
        Directory.CreateDirectory(recent);
        Directory.SetLastWriteTimeUtc(recent, new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, env.Service.CleanupExpired());
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public async Task Status_ShowsExpiryAndProgress()
    {
        using var env = NewEnv();
        var id = await ReadyAsync(env);
        var status = Assert.IsType<ImportStatusResult>(env.Service.GetStatus(id));
        Assert.Equal(DateTime.Parse(status.CreatedUtc!).AddDays(7), DateTime.Parse(status.ExpiresUtc!));
        Assert.Equal(_data.V2Wz.Length, status.BytesDownloaded);
        Assert.False(string.IsNullOrEmpty(status.Sha256));
    }

    [Fact]
    public void NamesFromAnUpload_AreShownWithoutControlCharacters_AndCut()
    {
        Assert.Equal("a?b?c", ImportService.Shown("a\nb\u001bc"));
        Assert.Equal("ok \U0001F600", ImportService.Shown("ok \U0001F600"));   // a real emoji stays
        Assert.Equal("x?y", ImportService.Shown("x\ud800y"));                  // a lone surrogate does not
        Assert.Equal(203, ImportService.Shown(new string('n', 500)).Length);
        Assert.Equal("", ImportService.Shown(null));
    }

    // ---- nothing leaks into logs or errors ----

    [Fact]
    public async Task Errors_NeverContainThePathsOrTheLinkOfTheServer()
    {
        using var env = NewEnv();
        env.Web.ServeFile(Direct, new byte[100], "Skill.wz");
        var failed = await env.FetchAndWait(ImportEnv.ViewLink, "wz");
        var texts = new[] { failed.JobError!, ((ImportFailure)ImportFailure.From("x", "y")).Error! };
        foreach (var text in texts)
        {
            Assert.DoesNotContain(env.Root, text);
            Assert.DoesNotContain(ImportEnv.DriveFileId, text);
        }
        var refused = env.Service.Preview(failed.Id);
        Assert.DoesNotContain(env.Root, refused.Error);
        var json = File.ReadAllText(Path.Combine(env.DirOf(failed.Id!), "state.json"));
        Assert.DoesNotContain(ImportEnv.DriveFileId, json);   // only the host of the link is kept
        Assert.Contains("drive.google.com", json);
    }
}
#endif
