using System.Runtime.CompilerServices;
using Harbor.Ui.Framework.Commands;
using Harbor.Ui.Framework.Overlays;
using TUnit.Assertions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
/// Steal/textual T4 (#1186): the palette merges provider streams instead of
/// owning catalogs. Pins the provider contract — discovery (score-0,
/// provider-ordered) vs scored hits, parallel merge order, warmup fan-out,
/// and that a failing provider never blanks the palette.
/// </summary>
public class CommandProvidersTests
{
    private static async Task<List<CommandHit>> CollectAsync(
        IAsyncEnumerable<CommandHit> stream,
        CancellationToken cancellationToken = default)
    {
        var hits = new List<CommandHit>();
        await foreach (CommandHit hit in stream.WithCancellation(cancellationToken))
        {
            hits.Add(hit);
        }

        return hits;
    }

    [Test]
    public async Task Matcher_MatchesPanelFuzzySemantics()
    {
        await Assert.That(CommandMatcher.Score("abc", string.Empty)).IsEqualTo(0);
        await Assert.That(CommandMatcher.Score("abc", "b") >= 0).IsTrue();
        await Assert.That(CommandMatcher.Score("abc", "z")).IsEqualTo(-1);
    }

    [Test]
    public async Task Slash_EmptyQuery_ReturnsDiscoveryInCatalogOrder()
    {
        var provider = new SlashCommandProvider();
        List<CommandHit> hits = await CollectAsync(provider.SearchAsync("", CancellationToken.None));

        await Assert.That(hits.Count).IsEqualTo(SlashCommandCatalog.All.Count);
        for (int i = 0; i < hits.Count; i++)
        {
            await Assert.That(hits[i].IsDiscovery).IsTrue();
            await Assert.That(hits[i].Score).IsEqualTo(0);
            await Assert.That(hits[i].ProviderId).IsEqualTo("slash");
            await Assert.That(hits[i].Title).IsEqualTo(SlashCommandCatalog.All[i].Invocation);
        }
    }

    [Test]
    public async Task Slash_Query_RanksExactFirst()
    {
        var provider = new SlashCommandProvider();
        List<CommandHit> hits = await CollectAsync(provider.SearchAsync("help", CancellationToken.None));

        await Assert.That(hits.Count > 0).IsTrue();
        await Assert.That(hits[0].Id).IsEqualTo("help");
        foreach (CommandHit hit in hits)
        {
            await Assert.That(hit.IsDiscovery).IsFalse();
        }
    }

    [Test]
    public async Task System_IsJustAnotherProvider()
    {
        var provider = new SystemCommandProvider();

        await Assert.That(provider.ProviderId).IsEqualTo("system");

        List<CommandHit> discovery = await CollectAsync(provider.SearchAsync("", CancellationToken.None));
        await Assert.That(discovery.Count).IsEqualTo(SystemCommandProvider.Defaults.Count);
        await Assert.That(discovery.Count).IsEqualTo(10);

        List<CommandHit> scored = await CollectAsync(provider.SearchAsync("session", CancellationToken.None));
        await Assert.That(scored.Any(h => h.Title == "New Session")).IsTrue();
    }

    [Test]
    public async Task File_WarmupThenSearch()
    {
        var provider = new FileCommandProvider(seed: ["src/main.cs", "src/util.cs", "README.md"]);
        await provider.StartupAsync(CancellationToken.None);

        await Assert.That(provider.IndexedCount).IsEqualTo(3);

        List<CommandHit> discovery = await CollectAsync(provider.SearchAsync("", CancellationToken.None));
        await Assert.That(discovery.Count).IsEqualTo(3);
        await Assert.That(discovery[0].Detail).IsEqualTo("README.md");

        List<CommandHit> scored = await CollectAsync(provider.SearchAsync("main", CancellationToken.None));
        await Assert.That(scored.Count).IsEqualTo(1);
        await Assert.That(scored[0].Title).IsEqualTo("main.cs");
    }

    [Test]
    public async Task File_Startup_PullsEnumeratorAndSorts()
    {
        var provider = new FileCommandProvider(enumerate: _ =>
            Task.FromResult<IReadOnlyList<string>>(["z/b.cs", "a/c.cs"]));

        await Assert.That(provider.IndexedCount).IsEqualTo(0);
        await provider.StartupAsync(CancellationToken.None);

        await Assert.That(provider.IndexedCount).IsEqualTo(2);
        List<CommandHit> discovery = await CollectAsync(provider.SearchAsync("", CancellationToken.None));
        await Assert.That(discovery[0].Detail).IsEqualTo("a/c.cs");
    }

    [Test]
    public async Task Worktree_EmptyQuery_PreservesSeedOrder()
    {
        var provider = new WorktreeCommandProvider(entries:
        [
            new WorktreeJumpEntry("s1", "Alpha", "/repo/a", "main", "idle"),
            new WorktreeJumpEntry("s2", "Beta", "/repo/b", "feat", "working"),
        ]);

        List<CommandHit> hits = await CollectAsync(provider.SearchAsync("", CancellationToken.None));

        await Assert.That(hits.Count).IsEqualTo(2);
        await Assert.That(hits[0].Id).IsEqualTo("s1");
        await Assert.That(hits[1].Id).IsEqualTo("s2");
        await Assert.That(hits[0].IsDiscovery).IsTrue();
    }

    [Test]
    public async Task Worktree_Query_FiltersByBranch()
    {
        var provider = new WorktreeCommandProvider(entries:
        [
            new WorktreeJumpEntry("s1", "Alpha", "/repo/a", "main", "idle"),
            new WorktreeJumpEntry("s2", "Beta", "/repo/b", "feat-x", "working"),
        ]);

        List<CommandHit> hits = await CollectAsync(provider.SearchAsync("feat-x", CancellationToken.None));

        await Assert.That(hits.Count).IsEqualTo(1);
        await Assert.That(hits[0].Id).IsEqualTo("s2");
    }

    [Test]
    public async Task Registry_EmptyQuery_IsProviderOrdered()
    {
        var registry = CommandProviderRegistry.CreateDefault();
        IReadOnlyList<CommandHit> hits = await registry.SearchAsync("", CancellationToken.None);

        int slash = SlashCommandCatalog.All.Count;
        await Assert.That(hits.Count).IsEqualTo(slash + SystemCommandProvider.Defaults.Count);
        await Assert.That(hits[0].ProviderId).IsEqualTo("slash");
        await Assert.That(hits[slash].ProviderId).IsEqualTo("system");
    }

    [Test]
    public async Task Registry_Query_RanksByScore()
    {
        var registry = CommandProviderRegistry.CreateDefault();
        IReadOnlyList<CommandHit> hits = await registry.SearchAsync("help", CancellationToken.None);

        await Assert.That(hits.Count > 0).IsTrue();
        await Assert.That(hits[0].Id).IsEqualTo("help");
        await Assert.That(hits[0].ProviderId).IsEqualTo("slash");
    }

    [Test]
    public async Task Registry_FailingProvider_ContributesNothing()
    {
        var registry = new CommandProviderRegistry([new SlashCommandProvider(), new BoomProvider()]);
        IReadOnlyList<CommandHit> hits = await registry.SearchAsync("help", CancellationToken.None);

        await Assert.That(hits.Count > 0).IsTrue();
        await Assert.That(hits[0].Id).IsEqualTo("help");
    }

    [Test]
    public async Task Registry_Warmup_CallsEveryProvider()
    {
        var first = new CountingProvider("a");
        var second = new CountingProvider("b");
        var registry = new CommandProviderRegistry([first, second]);

        await registry.WarmupAsync(CancellationToken.None);

        await Assert.That(first.Startups).IsEqualTo(1);
        await Assert.That(second.Startups).IsEqualTo(1);
    }

    private sealed class BoomProvider : ICommandProvider
    {
        public string ProviderId => "boom";

        public int Order => 99;

        public Task StartupAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public IAsyncEnumerable<CommandHit> SearchAsync(string? query, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    private sealed class CountingProvider(string providerId) : ICommandProvider
    {
        public string ProviderId { get; } = providerId;

        public int Order => 99;

        public int Startups { get; private set; }

        public Task StartupAsync(CancellationToken cancellationToken)
        {
            Startups++;
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<CommandHit> SearchAsync(string? query, CancellationToken cancellationToken)
            => CommandHits.From([]);
    }
}
