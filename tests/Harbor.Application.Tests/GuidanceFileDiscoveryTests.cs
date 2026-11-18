using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Application.Tests;

/// <summary>
///     #2675 item 070: guidance files in ancestor directories are part of the
///     system prompt, <c>AGENT.md</c> is a fallback spelling, and an explicit
///     home directory bounds the walk. The labels are what the prompt builder
///     prints, so they are asserted here too.
/// </summary>
public sealed class GuidanceFileDiscoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harbor-guidance").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Test]
    public async Task Discover_NestedDirectory_WalksAncestorsUpToHome()
    {
        string nested = Path.Combine(_root, "sub", "deep");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "AGENTS.md"), "deep rules");
        await File.WriteAllTextAsync(Path.Combine(_root, "sub", "AGENT.md"), "fallback rules");
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "root agents");
        await File.WriteAllTextAsync(Path.Combine(_root, "CLAUDE.md"), "root claude");

        IReadOnlyList<GuidanceFileDiscovery.GuidanceFile> found =
            GuidanceFileDiscovery.Discover(nested, homeDirectory: _root);

        await Assert.That(found.Count).IsEqualTo(4);
        await Assert.That(found[0].Label).IsEqualTo("AGENTS.md");
        await Assert.That(Path.GetFileName(found[0].FullPath)).IsEqualTo("AGENTS.md");
        await Assert.That(found[1].Label).IsEqualTo("../AGENT.md");
        await Assert.That(found[2].Label).IsEqualTo("../../AGENTS.md");
        await Assert.That(found[3].Label).IsEqualTo("../../CLAUDE.md");
    }

    [Test]
    public async Task Discover_HomeDirectory_StopsTheWalk()
    {
        string nested = Path.Combine(_root, "sub", "deep");
        string home = Path.Combine(_root, "sub");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "above home");
        await File.WriteAllTextAsync(Path.Combine(home, "AGENTS.md"), "home rules");
        await File.WriteAllTextAsync(Path.Combine(nested, "AGENTS.md"), "deep rules");

        IReadOnlyList<GuidanceFileDiscovery.GuidanceFile> found =
            GuidanceFileDiscovery.Discover(nested, homeDirectory: home);

        await Assert.That(found.Count).IsEqualTo(2);
        await Assert.That(found[0].Label).IsEqualTo("AGENTS.md");
        await Assert.That(found[1].Label).IsEqualTo("../AGENTS.md");
    }

    [Test]
    public async Task Discover_AgentMd_IsDiscoveredAsFallback()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENT.md"), "fallback only");

        IReadOnlyList<GuidanceFileDiscovery.GuidanceFile> found =
            GuidanceFileDiscovery.Discover(_root, homeDirectory: _root);

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].Label).IsEqualTo("AGENT.md");
    }

    [Test]
    public async Task LoadContextFiles_AncestorGuidance_ReachesThePromptSource()
    {
        string child = Path.Combine(_root, "child");
        Directory.CreateDirectory(child);
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "parent rule");

        IReadOnlyList<ContextFile> files = new WorkspaceContextService().LoadContextFiles(child);

        await Assert.That(files.Any(f => f.Path.Contains("..") && f.Content.Contains("parent rule"))).IsTrue();
    }
}
