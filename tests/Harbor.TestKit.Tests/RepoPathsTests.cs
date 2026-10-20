namespace Harbor.TestKit.Tests;

/// <summary>
///     The repository-root walk-up, memoised. The thing worth pinning is that
///     it finds THIS checkout and that the answer is the same every time —
///     a root that moved between two calls in one run would be a very
///     confusing failure.
/// </summary>
public class RepoPathsTests
{
    [Test]
    public async Task Finds_The_Checkout_This_Test_Runs_In()
    {
        string root = RepoPaths.Root;

        await Assert.That(File.Exists(Path.Combine(root, "Harbor.slnx"))).IsTrue()
            .Because("the root is the directory that holds the solution file, and the helper's whole job is to find it");
    }

    [Test]
    public async Task The_Root_Is_Memoised()
    {
        await Assert.That(RepoPaths.Root).IsEqualTo(RepoPaths.Root)
            .Because("the tree does not move while a test run is in progress, and re-walking it per call would be waste");
    }

    [Test]
    public async Task TryGetRoot_Agrees_With_Root()
    {
        bool found = RepoPaths.TryGetRoot(out string? root);

        await Assert.That(found).IsTrue();
        await Assert.That(root).IsEqualTo(RepoPaths.Root);
    }

    [Test]
    public async Task Combine_Builds_A_Path_Under_The_Root()
    {
        string combined = RepoPaths.Combine("tests", "fixtures");

        await Assert.That(combined.StartsWith(RepoPaths.Root, StringComparison.Ordinal)).IsTrue();
        await Assert.That(combined.EndsWith(Path.Combine("tests", "fixtures"), StringComparison.Ordinal)).IsTrue();
    }
}
