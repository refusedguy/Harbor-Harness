using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #2680 F178. <see cref="ModelCatalogSearch" /> is a pure search-and-page
///     leaf. These tests pin its observable output without resolving a provider
///     registry, reading an environment variable or starting a process.
/// </summary>
public class ModelCatalogSearchTests
{
    private static readonly ModelSearchEntry[] Catalog =
    [
        new("kilocode/tencent/hy3:free", "kilocode", "Hy3 Free"),
        new("anthropic/claude-opus-4", "anthropic", "Claude Opus 4"),
        new("openai/gpt-5", "openai", "GPT-5"),
        new("ollama/llama3", "ollama", null),
        new("kilocode/kilo-auto/free", "kilocode", "Auto Free"),
    ];

    [Test]
    public async Task TryCreate_RejectsNonPositiveLimitAndNegativeOffset()
    {
        await Assert.That(ModelCatalogSearch.TryCreate(null, null, 0, null, out _)).IsFalse();
        await Assert.That(ModelCatalogSearch.TryCreate(null, null, -1, null, out _)).IsFalse();
        await Assert.That(ModelCatalogSearch.TryCreate(null, null, null, -1, out _)).IsFalse();
        await Assert.That(ModelCatalogSearch.TryCreate(null, null, 1, -5, out _)).IsFalse();
    }

    [Test]
    public async Task TryCreate_AppliesDefaults()
    {
        await Assert.That(ModelCatalogSearch.TryCreate(null, null, null, null, out ModelSearchQuery query)).IsTrue();
        await Assert.That(query.Text).IsNull();
        await Assert.That(query.Provider).IsNull();
        await Assert.That(query.Limit).IsEqualTo(ModelCatalogSearch.DefaultLimit);
        await Assert.That(query.Offset).IsEqualTo(ModelCatalogSearch.DefaultOffset);
    }

    [Test]
    public async Task TryCreate_ClampsLimitToHardCap()
    {
        await Assert.That(ModelCatalogSearch.TryCreate(null, null, 500, 3, out ModelSearchQuery query)).IsTrue();
        await Assert.That(query.Limit).IsEqualTo(ModelCatalogSearch.MaxLimit);
        await Assert.That(query.Offset).IsEqualTo(3);
    }

    [Test]
    public async Task TryCreate_TrimsTextAndProviderAndMapsBlankToNull()
    {
        await Assert.That(ModelCatalogSearch.TryCreate("  opus  ", " ANTHROPIC ", 5, 0, out ModelSearchQuery query)).IsTrue();
        await Assert.That(query.Text).IsEqualTo("opus");
        await Assert.That(query.Provider).IsEqualTo("ANTHROPIC");

        await Assert.That(ModelCatalogSearch.TryCreate("   ", "\t", 5, 0, out ModelSearchQuery blank)).IsTrue();
        await Assert.That(blank.Text).IsNull();
        await Assert.That(blank.Provider).IsNull();
    }

    [Test]
    public async Task Search_WithoutQueryReturnsCatalogInOrder()
    {
        await Assert.That(ModelCatalogSearch.TryCreate(null, null, null, null, out ModelSearchQuery query)).IsTrue();
        ModelSearchPage page = ModelCatalogSearch.Search(Catalog, query);

        await Assert.That(page.TotalMatched).IsEqualTo(5);
        await Assert.That(page.Items.Count).IsEqualTo(5);
        await Assert.That(page.Items[0].Id).IsEqualTo("kilocode/tencent/hy3:free");
        await Assert.That(page.Items[4].Id).IsEqualTo("kilocode/kilo-auto/free");
        await Assert.That(page.HasMore).IsFalse();
    }

    [Test]
    public async Task Search_MatchesIdCaseInsensitively()
    {
        await Assert.That(ModelCatalogSearch.TryCreate("TENCENT/HY3", null, null, null, out ModelSearchQuery query)).IsTrue();
        ModelSearchPage page = ModelCatalogSearch.Search(Catalog, query);

        await Assert.That(page.TotalMatched).IsEqualTo(1);
        await Assert.That(page.Items.Count).IsEqualTo(1);
        await Assert.That(page.Items[0].Id).IsEqualTo("kilocode/tencent/hy3:free");
    }

    [Test]
    public async Task Search_MatchesDisplayNameCaseInsensitively()
    {
        await Assert.That(ModelCatalogSearch.TryCreate("auto free", null, null, null, out ModelSearchQuery query)).IsTrue();
        ModelSearchPage page = ModelCatalogSearch.Search(Catalog, query);

        await Assert.That(page.TotalMatched).IsEqualTo(1);
        await Assert.That(page.Items[0].Id).IsEqualTo("kilocode/kilo-auto/free");
    }

    [Test]
    public async Task Search_ProviderFilterUsesCaseInsensitiveEquality()
    {
        await Assert.That(ModelCatalogSearch.TryCreate(null, "KILOCODE", null, null, out ModelSearchQuery query)).IsTrue();
        ModelSearchPage page = ModelCatalogSearch.Search(Catalog, query);

        await Assert.That(page.TotalMatched).IsEqualTo(2);
        await Assert.That(page.Items[0].Id).IsEqualTo("kilocode/tencent/hy3:free");
        await Assert.That(page.Items[1].Id).IsEqualTo("kilocode/kilo-auto/free");
    }

    [Test]
    public async Task Search_ProviderFilterExcludesEntriesWithoutProvider()
    {
        await Assert.That(ModelCatalogSearch.TryCreate("llama", "ollama", null, null, out ModelSearchQuery query)).IsTrue();
        ModelSearchPage page = ModelCatalogSearch.Search(Catalog, query);

        await Assert.That(page.TotalMatched).IsEqualTo(1);
        await Assert.That(page.Items[0].Id).IsEqualTo("ollama/llama3");
    }

    [Test]
    public async Task Search_PaginatesWithStableTotals()
    {
        await Assert.That(ModelCatalogSearch.TryCreate(null, null, 2, 1, out ModelSearchQuery query)).IsTrue();
        ModelSearchPage page = ModelCatalogSearch.Search(Catalog, query);

        await Assert.That(page.TotalMatched).IsEqualTo(5);
        await Assert.That(page.Offset).IsEqualTo(1);
        await Assert.That(page.Items.Count).IsEqualTo(2);
        await Assert.That(page.Items[0].Id).IsEqualTo("anthropic/claude-opus-4");
        await Assert.That(page.Items[1].Id).IsEqualTo("openai/gpt-5");
        await Assert.That(page.HasMore).IsTrue();
    }

    [Test]
    public async Task Search_OffsetBeyondResultsYieldsEmptyPage()
    {
        await Assert.That(ModelCatalogSearch.TryCreate(null, null, 2, 99, out ModelSearchQuery query)).IsTrue();
        ModelSearchPage page = ModelCatalogSearch.Search(Catalog, query);

        await Assert.That(page.TotalMatched).IsEqualTo(5);
        await Assert.That(page.Items.Count).IsEqualTo(0);
        await Assert.That(page.HasMore).IsFalse();
    }

    [Test]
    public async Task Search_HardCapLimitsPageSize()
    {
        var many = new ModelSearchEntry[45];
        for (int i = 0; i < many.Length; i++) many[i] = new ModelSearchEntry($"p/model-{i}", "p", null);

        await Assert.That(ModelCatalogSearch.TryCreate(null, null, 1000, 0, out ModelSearchQuery query)).IsTrue();
        await Assert.That(query.Limit).IsEqualTo(20);

        ModelSearchPage page = ModelCatalogSearch.Search(many, query);
        await Assert.That(page.Items.Count).IsEqualTo(20);
        await Assert.That(page.TotalMatched).IsEqualTo(45);
        await Assert.That(page.HasMore).IsTrue();
    }

    [Test]
    public async Task Search_EmptyCatalogReturnsEmptyPage()
    {
        await Assert.That(ModelCatalogSearch.TryCreate(null, null, null, null, out ModelSearchQuery query)).IsTrue();
        ModelSearchPage page = ModelCatalogSearch.Search([], query);

        await Assert.That(page.TotalMatched).IsEqualTo(0);
        await Assert.That(page.Items.Count).IsEqualTo(0);
        await Assert.That(page.HasMore).IsFalse();
    }

    [Test]
    public async Task Search_NoMatchReturnsEmptyPage()
    {
        await Assert.That(ModelCatalogSearch.TryCreate("does-not-exist", null, null, null, out ModelSearchQuery query)).IsTrue();
        ModelSearchPage page = ModelCatalogSearch.Search(Catalog, query);

        await Assert.That(page.TotalMatched).IsEqualTo(0);
        await Assert.That(page.Items.Count).IsEqualTo(0);
        await Assert.That(page.HasMore).IsFalse();
    }
}
