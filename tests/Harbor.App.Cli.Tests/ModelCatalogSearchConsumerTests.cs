// ModelCatalogSearchConsumerTests.cs — issue #3110, atomic slice 6/10.
//
// ModelCatalogSearch was a pure leaf with zero callers. ProviderVerbs now
// projects the provider catalog onto the leaf's entry type, runs its validated
// search and prints one bounded page. These tests drive that real consumer
// with an injected registry and assert observable output: partial-name
// filtering, provider filtering, the MaxLimit cap and the HasMore hint.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.App.Cli.Commands;
using Harbor.Tools.Builtin;
using Microsoft.Extensions.Logging.Abstractions;
using FakeProviderRegistryBase = Harbor.TestKit.Generated.FakeProviderRegistry;

namespace Harbor.App.Cli.Tests;

[NotInParallel]
public class ModelCatalogSearchConsumerTests : CafVerbTestBase
{
    private static ModelInfo Model(string id, string provider) => new(
        Id: id,
        ProviderId: provider,
        DisplayName: id,
        ContextWindow: 8192,
        MaxOutputTokens: 2048,
        SupportsReasoning: false,
        SupportsVision: false,
        SupportsToolUse: true,
        Pricing: Pricing.Unknown,
        PromptTemplate: "openai");

    private static IReadOnlyList<ModelInfo> Catalog() =>
    [
        Model("anthropic/claude-opus-4", "anthropic"),
        Model("anthropic/claude-haiku", "anthropic"),
        Model("openai/gpt-4o", "openai"),
        Model("kilocode/tencent/hy3:free", "kilocode"),
        Model("kilocode/tencent/hy3:free:auto", "kilocode"),
    ];

    [Test]
    public async Task PartialName_Query_ReturnsOnlyMatches()
    {
        (int exit, string output, _) = await CaptureAsync(() =>
            ProviderVerbs.RunListModelsAsync(
                NullLogger.Instance,
                providerId: null,
                query: "opus",
                registry: new StubRegistry(Catalog())));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("claude-opus-4");
        await Assert.That(output).DoesNotContain("gpt-4o");
        await Assert.That(output).DoesNotContain("tencent/hy3");
    }

    [Test]
    public async Task Provider_Filter_ReturnsOnlyThatProvidersModels()
    {
        ModelSearchPage page = ProviderVerbs.SearchModels(Catalog(), null, "KILOCODE", null, null);

        await Assert.That(page.Items.Count).IsEqualTo(2);
        await Assert.That(page.Items.All(m => m.Provider == "kilocode")).IsTrue();
    }

    [Test]
    public async Task Limit_IsCappedAtMaxLimit()
    {
        var many = new List<ModelInfo>();
        for (int i = 0; i < ModelCatalogSearch.MaxLimit * 2; i++)
            many.Add(Model($"p/model-{i}", "p"));

        ModelSearchPage page = ProviderVerbs.SearchModels(many, null, null, 1000, null);

        await Assert.That(page.Items.Count).IsEqualTo(ModelCatalogSearch.MaxLimit);
        await Assert.That(page.HasMore).IsTrue();
    }

    [Test]
    public async Task Verb_PrintsHasMoreHint_WhenPageIsBounded()
    {
        var many = new List<ModelInfo>();
        for (int i = 0; i < ModelCatalogSearch.MaxLimit + 3; i++)
            many.Add(Model($"p/model-{i}", "p"));

        (int exit, string output, _) = await CaptureAsync(() =>
            ProviderVerbs.RunListModelsAsync(
                NullLogger.Instance,
                providerId: null,
                registry: new StubRegistry(many)));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("more");
        await Assert.That(output).Contains("offset");
    }

    private sealed class StubRegistry : FakeProviderRegistryBase
    {
        public StubRegistry(IReadOnlyList<ModelInfo> models)
        {
            GetAllModelsAsyncResult = Result.Success<IReadOnlyList<ModelInfo>>(models);
            GetModelsCachedAsyncResult = Result.Success<IReadOnlyList<ModelInfo>>(models);
        }
    }
}
