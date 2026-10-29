using MapleLib.Img;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using WzImgMCP.Server;
using WzImgMCP.Tools;

namespace WzImgMCP.Tests;

/// <summary>
/// An image that a tool is about to change must be protected before its first change, whatever a scan, the memory guard's trim,
/// the cache's eviction or unload_category does in between. Every test drives one interleaving by hand: the seam
/// <see cref="WzSessionManager.EditWindowHook"/> runs the other party exactly in the window between "the edit tool has the image"
/// and "the image is flagged as changed", so nothing sleeps and nothing depends on timing. Each test then proves that the edit
/// survived and that save_image wrote the edited content to the file.
/// </summary>
[Collection("AllowedRootEnv")]   // the eviction test sets WZIMG_CACHE_MB
public sealed class EditRaceTests : IDisposable
{
    private const string Cat = ScanTestTree.Category;
    private const string Img = "m001.img";

    private readonly ScanTestTree _tree = new(10);

    public void Dispose() => _tree.Dispose();

    private static string Value(string markdown, string key) => MarkdownTestHelper.GetFirstValue(markdown, key) ?? "";

    /// <summary>The info/id of the image as the file on disk has it, read by a fresh session.</summary>
    private static int IdOnDisk(string root, string image = Img)
    {
        using var fresh = new WzSessionManager();
        fresh.InitDataSource(root);
        return ((WzIntProperty)fresh.GetImage(Cat, image).GetFromPath("info/id")!).Value;
    }

    private static void SetId(WzSessionManager session, ImageEdit edit, int value)
    {
        ((WzIntProperty)edit.Image.GetFromPath("info/id")!).Value = value;
        edit.Commit();
    }

    private static void SaveAndAssertOnDisk(WzSessionManager session, string root, int expected, string image = Img)
    {
        var saved = new ModifyTools(session).SaveImage(Cat, image);
        MarkdownTestHelper.AssertSuccess(saved);
        Assert.Equal(expected, IdOnDisk(root, image));
    }

    /// <summary>Runs on a thread of its own (a task may run inline on the calling thread, which would re-enter a held lock).</summary>
    private static T OnAnotherThread<T>(Func<T> work)
    {
        T result = default!;
        var thread = new Thread(() => result = work());
        thread.Start();
        thread.Join();
        return result;
    }

    private static bool CanTakeLockFromAnotherThread(WzSessionManager session, WzImage image) => OnAnotherThread(() =>
    {
        bool got = Monitor.TryEnter(session.GateOf(image), 0);
        if (got) Monitor.Exit(session.GateOf(image));
        return got;
    });

    private static MemoryGuard TrimmingGuard(WzSessionManager session)
        => new(() => session.CurrentDataSource, new MemoryGuardSettings(SoftLimitBytes: 1000, IdleTrim: TimeSpan.Zero),
            heapBytes: () => 5000, collect: () => { });

    // ---- (a) an edit and a scan

    [Fact]
    public void ScanReleaseVersusEdit_AnEditInFlightIsNeverUnparsedAway()
    {
        using var session = _tree.NewSession();
        var image = session.GetImage(Cat, Img);
        image.UnparseImage();                                   // cached but unparsed: the scan parses it and used to unparse it again
        Assert.Equal(1, ((ImgFileSystemDataSource)session.DataSource).GetStats().CachedImageCount);

        var plan = new ScanPlan { Items = new() { new ScanItem(Cat, Img, null) } };
        var outcome = ScanGuard.Run(session, plan, (img, cat, name) =>
        {
            // Another request is changing the image while the scan looks at it: the value is changed, the flag is not set yet
            // (the order the edit tools used to have).
            ((WzIntProperty)img.GetFromPath("info/id")!).Value = 99;
            return false;
        });

        Assert.Equal(1, outcome.Scanned);
        Assert.True(image.Parsed);                              // the scan did not unparse it ...
        Assert.Equal(99, ((WzIntProperty)image.GetFromPath("info/id")!).Value);   // ... so the change is still there
        Assert.Same(image, session.DataSource.GetImage(Cat, Img));

        image.Changed = true;                                   // the edit tool finishes
        SaveAndAssertOnDisk(session, _tree.Path, 99);
    }

    [Fact]
    public void ScanVersusEdit_ACachedImageIsVisitedUnderItsLock_AndNeverDisposed()
    {
        using var session = _tree.NewSession();
        var image = session.GetImage(Cat, Img);
        object? lockedDuringVisit = null;

        ScanGuard.Run(session, new ScanPlan { Items = new() { new ScanItem(Cat, Img, null) } }, (img, cat, name) =>
        {
            // From another thread the lock of this image is taken: a tool that wants to edit it has to wait for the scan.
            lockedDuringVisit = CanTakeLockFromAnotherThread(session, img);
            return false;
        });

        Assert.Equal(false, lockedDuringVisit);
        Assert.NotNull(image.Name);
    }

    // ---- (b) an edit and the memory guard's trim

    [Fact]
    public void TrimVersusEdit_TheEditStartsOverOnTheCachedInstance_AndSurvives()
    {
        using var session = _tree.NewSession();
        var guard = TrimmingGuard(session);
        var attempts = new List<WzImage>();
        MemoryTrim? trim = null;
        session.EditWindowHook = (img, attempt) =>
        {
            attempts.Add(img);
            if (attempt == 1)
                trim = guard.Tick();                            // the whole cache is released while the tool holds the image
        };

        using (var edit = session.BeginEdit(Cat, Img))
            SetId(session, edit, 99);

        Assert.NotNull(trim);
        Assert.Equal(1, trim!.ReleasedImages);                  // it did take the image out of the cache
        Assert.Equal(2, attempts.Count);                        // so the edit began again, on the instance the cache holds now
        Assert.NotSame(attempts[0], attempts[1]);
        Assert.Same(attempts[1], session.DataSource.GetImage(Cat, Img));
        Assert.True(attempts[1].Changed);

        Assert.Equal(0, ((ImgFileSystemDataSource)session.DataSource).ReleaseUnpinnedImages());   // a later trim keeps it
        Assert.Same(attempts[1], session.DataSource.GetImage(Cat, Img));
        SaveAndAssertOnDisk(session, _tree.Path, 99);
    }

    [Fact]
    public void TrimAfterTheFlag_NeverTakesTheImageAway()
    {
        using var session = _tree.NewSession();
        var guard = TrimmingGuard(session);
        using var edit = session.BeginEdit(Cat, Img);

        var trim = guard.Tick();                                // the edit is half done

        Assert.Equal(0, trim!.ReleasedImages);
        Assert.Equal(1, trim.PinnedImages);
        SetId(session, edit, 99);
        Assert.Same(edit.Image, session.DataSource.GetImage(Cat, Img));
    }

    // ---- (c) an edit and the cache's eviction

    [Fact]
    public void EvictionVersusEdit_TheEditStartsOverOnTheCachedInstance_AndSurvives()
    {
        using var heavy = new ScanTestTree(8, propertiesPerImage: 8000);   // about 14 MB of cache each against a limit of 16 MB
        Environment.SetEnvironmentVariable(WzImgMCP.Utils.ServerSettings.CacheMbEnvVar, "16");
        try
        {
            using var session = heavy.NewSession();
            string first = heavy.RelativePaths[0];
            string other = heavy.RelativePaths[1];
            var attempts = new List<WzImage>();
            session.EditWindowHook = (img, attempt) =>
            {
                attempts.Add(img);
                if (attempt != 1)
                    return;
                session.GetImage(Cat, other);                   // a second big image pushes the least recently used one out
                Assert.DoesNotContain(((ImgFileSystemDataSource)session.DataSource).GetCachedImages(), c => ReferenceEquals(c.Image, img));
            };

            using (var edit = session.BeginEdit(Cat, first))
                SetId(session, edit, 99);

            Assert.Equal(2, attempts.Count);
            Assert.NotSame(attempts[0], attempts[1]);
            Assert.Same(attempts[1], session.DataSource.GetImage(Cat, first));
            Assert.True(attempts[1].Changed);
            SaveAndAssertOnDisk(session, heavy.Path, 99, first);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WzImgMCP.Utils.ServerSettings.CacheMbEnvVar, null);
        }
    }

    [Fact]
    public void FlaggedImagesAreNeverEvicted_WhateverIsLoadedAfterwards()
    {
        using var heavy = new ScanTestTree(8, propertiesPerImage: 8000);
        Environment.SetEnvironmentVariable(WzImgMCP.Utils.ServerSettings.CacheMbEnvVar, "16");
        try
        {
            using var session = heavy.NewSession();
            using var edit = session.BeginEdit(Cat, heavy.RelativePaths[0]);

            for (int i = 1; i < 6; i++)
                session.GetImage(Cat, heavy.RelativePaths[i]);

            Assert.Same(edit.Image, session.DataSource.GetImage(Cat, heavy.RelativePaths[0]));
            SetId(session, edit, 99);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WzImgMCP.Utils.ServerSettings.CacheMbEnvVar, null);
        }
    }

    // ---- unload_category, unparse_image and discard_changes

    [Fact]
    public void UnloadVersusEdit_TheEditStartsOverOnTheCachedInstance_AndSurvives()
    {
        using var session = _tree.NewSession();
        var attempts = new List<WzImage>();
        string? unloadResult = null;
        session.EditWindowHook = (img, attempt) =>
        {
            attempts.Add(img);
            if (attempt == 1)
                unloadResult = new LifecycleTools(session).UnloadCategory(Cat);   // sees an unflagged image: unparses it and drops it
        };

        using (var edit = session.BeginEdit(Cat, Img))
            SetId(session, edit, 99);

        MarkdownTestHelper.AssertSuccess(unloadResult!);
        Assert.Equal(2, attempts.Count);
        Assert.Same(attempts[1], session.DataSource.GetImage(Cat, Img));
        Assert.True(attempts[1].Parsed);
        SaveAndAssertOnDisk(session, _tree.Path, 99);
    }

    [Fact]
    public void UnloadAndUnparse_RefuseAFlaggedImage_AndKeepItsEdit()
    {
        using var session = _tree.NewSession();
        using var edit = session.BeginEdit(Cat, Img);
        SetId(session, edit, 99);

        var unload = new LifecycleTools(session).UnloadCategory(Cat);
        var unparse = new LifecycleTools(session).UnparseImage(Cat, Img);

        MarkdownTestHelper.AssertFailure(unload);
        MarkdownTestHelper.AssertFailure(unparse);
        Assert.True(edit.Image.Parsed);
        Assert.Equal(99, ((WzIntProperty)edit.Image.GetFromPath("info/id")!).Value);
    }

    // ---- the lease itself

    [Fact]
    public void TheLockIsHeldWhileEditing_AndAnUncommittedEditLeavesNoFlag()
    {
        using var session = _tree.NewSession();
        WzImage image;
        using (var edit = session.BeginEdit(Cat, Img))
        {
            image = edit.Image;
            Assert.True(image.Changed);                          // flagged before the tool has changed anything
            Assert.False(CanTakeLockFromAnotherThread(session, image));
        }                                                        // nothing was committed: a "property not found" error, say

        Assert.False(image.Changed);
        Assert.True(CanTakeLockFromAnotherThread(session, image));
    }

    [Fact]
    public void TheEditTools_FlagTheImage_AndAFailedEditLeavesNoFlag()
    {
        using var session = _tree.NewSession();
        var tools = new ModifyTools(session);

        MarkdownTestHelper.AssertFailure(tools.SetInt(Cat, Img, "info/does_not_exist", 5));
        Assert.False(session.GetImage(Cat, Img).Changed);

        MarkdownTestHelper.AssertSuccess(tools.SetInt(Cat, Img, "info/id", 99));
        Assert.True(session.GetImage(Cat, Img).Changed);
        SaveAndAssertOnDisk(session, _tree.Path, 99);
    }

    [Fact]
    public void CopyProperty_InOppositeDirections_DoesNotNeedTwoLocksAtOnce()
    {
        using var session = _tree.NewSession();
        var tools = new ModifyTools(session);

        MarkdownTestHelper.AssertSuccess(tools.CopyProperty(Cat, "m001.img", "info/id", Cat, "m002.img", "info", "copied1"));
        MarkdownTestHelper.AssertSuccess(tools.CopyProperty(Cat, "m002.img", "info/copied1", Cat, "m001.img", "info", "copied2"));

        Assert.NotNull(session.GetImage(Cat, "m002.img").GetFromPath("info/copied1"));
        Assert.NotNull(session.GetImage(Cat, "m001.img").GetFromPath("info/copied2"));
    }
}
