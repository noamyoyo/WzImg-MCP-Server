namespace WzImgMCP.Tests;

/// <summary>
/// Tests that snapshot "the folder around the tree" (to prove a tool wrote nothing next to it) must not see what other tests, which
/// run in parallel, create in the system temp folder. This failed once as a flake: SaveImageHardeningTests listed the temp folder
/// while a ScanTestTree appeared in it. Every tree now lives in a folder of its own, so the parent of a tree holds the tree only.
/// </summary>
public sealed class TempIsolationTests
{
    private static string Names(string dir) => string.Join(",", Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).OrderBy(x => x));

    [Fact]
    public void TheParentOfAFixtureTree_HoldsTheTreeOnly_WhateverElseIsCreatedInTheTempFolder()
    {
        using var fixture = new TestFixture();
        string parent = Path.GetDirectoryName(fixture.TestDataPath)!;
        string before = Names(parent);

        using var otherTree = new ScanTestTree(2);          // what used to appear in the folder the test looked at
        using var otherFixture = new TestFixture();

        Assert.Equal("data", before);
        Assert.Equal(before, Names(parent));
        Assert.NotEqual(parent, Path.GetDirectoryName(otherFixture.TestDataPath));
        Assert.NotEqual(parent, Path.GetDirectoryName(otherTree.Path));
    }

    [Fact]
    public void TheParentOfAScanTree_HoldsTheTreeOnly_AndIsRemovedWithIt()
    {
        string path, parent;
        using (var tree = new ScanTestTree(2))
        {
            path = tree.Path;
            parent = Path.GetDirectoryName(path)!;
            Assert.Equal("tree", Names(parent));
        }

        Assert.False(Directory.Exists(path));
        Assert.False(Directory.Exists(parent));
    }
}
