using Harbor.Lsp;

namespace Harbor.Lsp.Tests;

public class LspServerCatalogTests
{
    [Test]
    public async Task BuiltinCatalog_HasFiveServers()
    {
        await Assert.That(LspServerDefinition.Builtin.Count()).IsEqualTo(5);
        await Assert.That(LspServerDefinition.Builtin.Select(d => d.Id))
            .IsEquivalentTo(["typescript", "python", "go", "rust", "csharp"]);
    }

    [Test]
    public async Task Handles_MatchesDeclaredExtensions_Only()
    {
        LspServerDefinition ts = LspServerDefinition.TypeScript;
        await Assert.That(ts.Handles("/a/b/App.tsx")).IsTrue();
        await Assert.That(ts.Handles("/a/b/App.TS")).IsTrue(); // case-insensitive
        await Assert.That(ts.Handles("/a/b/App.py")).IsFalse();

        await Assert.That(LspServerDefinition.CSharp.Handles("/p/Program.cs")).IsTrue();
        await Assert.That(LspServerDefinition.Rust.Handles("/p/main.rs")).IsTrue();
        await Assert.That(LspServerDefinition.Go.Handles("/p/main.go")).IsTrue();
        await Assert.That(LspServerDefinition.Python.Handles("/p/main.py")).IsTrue();
    }

    [Test]
    public async Task FindWorkspaceRoot_NearestGitDirectoryWins()
    {
        string root = Path.Combine(Path.GetTempPath(), "harbor-lsp-tests", Guid.NewGuid().ToString("N"));
        string nested = Path.Combine(root, "src", "deep");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.Combine(root, ".git"));

        try
        {
            string file = Path.Combine(nested, "a.ts");
            await Assert.That(LspManager.FindWorkspaceRoot(file)).IsEqualTo(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task FindWorkspaceRoot_NoGit_FallsBackToParentDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "harbor-lsp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            string file = Path.Combine(root, "a.ts");
            await Assert.That(LspManager.FindWorkspaceRoot(file)).IsEqualTo(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class LspUriTests
{
    [Test]
    public async Task FileUri_AbsolutePath_Prefixed()
    {
        await Assert.That(LspServerSession.FileUri("/tmp/a.ts")).IsEqualTo("file:///tmp/a.ts");
    }

    [Test]
    public async Task FileUri_ThenFromUri_RoundTrips_WithSpaces()
    {
        string path = "/tmp/harbor lsp/with space.ts";
        string uri = LspServerSession.FileUri(path);
        await Assert.That(LspServerSession.FromUri(uri)).IsEqualTo(path);
    }

    [Test]
    public async Task FromUri_NonFileScheme_IsEmpty()
    {
        await Assert.That(LspServerSession.FromUri("untitled:Untitled-1")).IsEmpty();
        await Assert.That(LspServerSession.FromUri("https://example.com/a.ts")).IsEmpty();
    }
}
