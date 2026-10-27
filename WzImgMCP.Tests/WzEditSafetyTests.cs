using WzImgMCP.Server;
using WzImgMCP.Tools;

namespace WzImgMCP.Tests;

/// <summary>
/// Edit tools mark the image as changed. discard_changes, unparse_image and unload_category must not
/// turn that into an empty image that a later save_image / pack_to_wz writes out.
/// </summary>
[Collection("AllowedRootEnv")]
public sealed class WzEditSafetyTests : IDisposable
{
    private const string Category = "String";
    private const string Image = "Pet.img";
    private const string NamePath = "5000000/name";

    private readonly TempTree? _tree;
    private readonly WzSessionManager _session = new();
    private readonly ModifyTools _modify;
    private readonly PropertyTools _props;
    private readonly LifecycleTools _life;

    public WzEditSafetyTests()
    {
        _modify = new ModifyTools(_session);
        _props = new PropertyTools(_session);
        _life = new LifecycleTools(_session);
        if (!WzTestData.Available)
            return;
        _tree = WzTestData.CopyOfExtractedString();
        _session.InitDataSource(_tree.Path);
    }

    public void Dispose()
    {
        _session.Dispose();
        _tree?.Dispose();
    }

    private string PetFile => Path.Combine(_tree!.Path, Category, Image);

    private string? Name()
    {
        var r = _props.GetString(Category, Image, NamePath);
        MarkdownTestHelper.AssertSuccess(r);
        return WzTestData.Value(r, "value");
    }

    private int PropertyCount()
    {
        var r = _life.ParseImage(Category, Image);
        MarkdownTestHelper.AssertSuccess(r);
        return MarkdownTestHelper.GetFirstInt(r, "property_count");
    }

    [SkippableFact]
    public void DiscardChanges_RestoresOriginalData_AndSaveKeepsOriginalBytes()
    {
        Skip.IfNot(WzTestData.Available, WzTestData.SkipReason);
        var original = Name();
        var originalCount = PropertyCount();
        var originalBytes = File.ReadAllBytes(PetFile);
        Assert.NotEmpty(originalBytes);
        Assert.True(originalCount > 0);

        MarkdownTestHelper.AssertSuccess(_modify.SetString(Category, Image, NamePath, "EDITED-THEN-DISCARDED"));
        Assert.Equal("EDITED-THEN-DISCARDED", Name());

        MarkdownTestHelper.AssertSuccess(_modify.DiscardChanges(Category, Image));

        Assert.Equal(original, Name());
        Assert.Equal(originalCount, PropertyCount());

        MarkdownTestHelper.AssertSuccess(_modify.SaveImage(Category, Image));
        Assert.Equal(originalBytes, File.ReadAllBytes(PetFile));
    }

    [SkippableFact]
    public void UnparseImage_IsRefusedWhileEdited_AndDataStaysIntact()
    {
        Skip.IfNot(WzTestData.Available, WzTestData.SkipReason);
        var originalCount = PropertyCount();
        MarkdownTestHelper.AssertSuccess(_modify.SetString(Category, Image, NamePath, "UNSAVED-EDIT"));

        var refused = _life.UnparseImage(Category, Image);

        MarkdownTestHelper.AssertFailure(refused);
        Assert.Contains("unsaved changes: call save_image or discard_changes first", refused);
        Assert.Equal("UNSAVED-EDIT", Name());
        Assert.Equal(originalCount, PropertyCount());

        // The edit is still saveable and complete.
        MarkdownTestHelper.AssertSuccess(_modify.SaveImage(Category, Image));
        using var fresh = new WzSessionManager();
        fresh.InitDataSource(_tree!.Path);
        var r = new PropertyTools(fresh).GetString(Category, Image, NamePath);
        Assert.Equal("UNSAVED-EDIT", WzTestData.Value(r, "value"));
    }

    [SkippableFact]
    public void UnloadCategory_IsRefusedWhileAnImageIsEdited_AndDataStaysIntact()
    {
        Skip.IfNot(WzTestData.Available, WzTestData.SkipReason);
        var originalCount = PropertyCount();
        MarkdownTestHelper.AssertSuccess(_modify.SetString(Category, Image, NamePath, "UNSAVED-EDIT"));

        var refused = _life.UnloadCategory(Category);

        MarkdownTestHelper.AssertFailure(refused);
        Assert.Contains("unsaved changes", refused);
        Assert.Equal("UNSAVED-EDIT", Name());
        Assert.Equal(originalCount, PropertyCount());
    }
}
