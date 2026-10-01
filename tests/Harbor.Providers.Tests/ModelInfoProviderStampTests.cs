// ModelInfoProviderStampTests.cs — GUARD for issue #848.
//
// THE CONTRACT BEING ENFORCED
// ---------------------------
// `ModelInfo.ProviderId` is what a dozen readers take as "which provider is
// this model" — the prompt's `- Model: <provider>/<id>` line, the picker's
// filters, `/models` grouping, and `CompactionService`, which resolves an LLM
// CLIENT from it. A catalog is fetched under exactly one registry key, so every
// entry it returns must carry THAT key. This file asserts the catalog enforces
// it rather than trusting its input.
//
// WHY THIS FILE, AND WHY IT IS ABOUT THE HARDCODED ARRAY
// -----------------------------------------------------
// #848 counted four hand-stamped sites (ProviderConfig.ParseModel, Ollama,
// AnthropicModels, OpenAIModels) and noted that all four agree with their
// registry id today. That is true, and it is why a rule pinning those four
// literals would be GREEN on the unfixed tree — a guard that proves nothing.
//
// The site #848 missed is the fifth: `ProviderConfig.Models` — a hardcoded
// `models` array in a provider JSON. `DynamicModelCatalog.GetModelsAsync`
// returns that array VERBATIM (`return Result.Success(config.Models)`),
// including the `providerId` the JSON author typed. The file is registered
// under its own `id` (JsonProviderDiscovery, three call sites), so the registry
// key and the stamped field are set by two independent reads of one file, and
// nothing in the product compares them.
//
// This is REACHABLE, not theoretical:
//
//   ~/.harbor/providers/gateway.json   ← user-writable, and
//                                        FindProvidersDirectories yields it
//                                        FIRST, so a user's override wins
//   { "id": "gateway",
//     "models": [ { "id": "claude-opus-4", "providerId": "anthropic", … } ] }
//
// registers the provider as `gateway` and hands the picker, `/models` and the
// prompt a model stamped `anthropic`. The bundled `providers/anthropic.json`
// and `providers/openai.json` show the array is a supported, in-repo shape —
// they are simply excluded from JSON discovery by id, which is why nothing
// in-tree trips this today and why a data-only rule would also miss it.
//
// THE RULE
// --------
// A hardcoded catalog is stamped from the config it was HANDED, exactly as the
// fetched catalog is (`ParseModel` already stamps `config.Id`). The `providerId`
// in the JSON is not a second source of truth for "who am I" — the file's `id`
// is.
//
// NON-VACUITY
// -----------
// A test that builds a config and asserts on it can pass for the wrong reason
// (asserting on a list that was empty, or on a value the product never set).
// So the cases below are pinned from both ends: the mismatch case is asserted
// to CONTAIN a foreign stamp before the contract is asserted, and the
// already-correct case is asserted to come back UNCHANGED, so a fix that
// blanket-overwrites every field would fail rather than pass quietly.
//
// The matched-by-value counterpart — that the bundled provider JSONs do not
// AUTHOR a `providerId` at all, since the product now derives it — is
// `ModelInfoProviderStampRules` in Harbor.Architecture.Tests. This file is the
// behaviour; that one is the data.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Providers.OpenAiCompatible;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Providers.Tests;

/// <summary>
///     Issue #848: a provider's hardcoded catalog is stamped with the id that
///     provider is registered under, not with whatever the file's JSON said.
/// </summary>
public sealed class ModelInfoProviderStampTests
{
    /// <summary>
    ///     A catalog is fetched under one registry key, so a hardcoded entry
    ///     stamped with a DIFFERENT provider comes back re-stamped with the id
    ///     the catalog was handed.
    /// </summary>
    [Test]
    public async Task HardcodedModel_StampedWithAForeignProvider_IsReStampedWithTheConfigsOwnId()
    {
        var catalog = new DynamicModelCatalog(
            new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK))),
            NewCacheDir(),
            NullLogger<DynamicModelCatalog>.Instance);

        // The gateway shape: registered as `gateway`, models claiming `anthropic`.
        var config = new ProviderConfig
        {
            Id = "gateway",
            DisplayName = "Gateway",
            BaseUrl = "https://api.gateway.example",
            Models =
            [
                new ModelInfo(
                    "claude-opus-4-20250514",
                    "anthropic",
                    "Claude Opus 4",
                    200_000,
                    32_000,
                    true,
                    true,
                    true,
                    Pricing.Unknown,
                    "anthropic"),
            ],
        };

        // Pin the fixture BEFORE asserting the contract: if the mismatch were
        // absent, the assertion below would be testing nothing.
        await Assert.That(config.Models![0].ProviderId).IsEqualTo("anthropic")
            .Because("the fixture must actually carry a foreign stamp or this test proves nothing");

        Result<IReadOnlyList<ModelInfo>> result = await catalog.GetModelsAsync(config);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(1)
            .Because("a hardcoded catalog is returned whole; this is not a filtering path");

        await Assert.That(result.Value[0].ProviderId).IsEqualTo("gateway")
            .Because(
                "the catalog was fetched under the registry key 'gateway', so the model it returns must say "
                + "'gateway'. A foreign stamp makes the prompt render '- Model: anthropic/…' for a gateway "
                + "request, makes ProviderModelPicker's `m.ProviderId == group.Id` filter hide the model, and "
                + "makes CompactionService resolve the wrong ILlmClient to summarize with. See issue #848.");

        // The id half is data; it must survive the re-stamp.
        await Assert.That(result.Value[0].Id).IsEqualTo("claude-opus-4-20250514")
            .Because("re-stamping the provider must not disturb any other member");
    }

    /// <summary>
    ///     Every entry, not just the first: a catalog that re-stamps one model
    ///     and passes another through is the same defect wearing a hat.
    /// </summary>
    [Test]
    public async Task EveryEntryInAHardcodedCatalog_IsReStamped_NotJustTheFirst()
    {
        var catalog = new DynamicModelCatalog(
            new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK))),
            NewCacheDir(),
            NullLogger<DynamicModelCatalog>.Instance);

        var config = new ProviderConfig
        {
            Id = "gateway",
            DisplayName = "Gateway",
            BaseUrl = "https://api.gateway.example",
            Models =
            [
                Stamp("m1", "anthropic"),
                Stamp("m2", "openai"),
                Stamp("m3", "kilocode"),
            ],
        };

        Result<IReadOnlyList<ModelInfo>> result = await catalog.GetModelsAsync(config);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(3);

        for (int i = 0; i < result.Value.Count; i++)
        {
            await Assert.That(result.Value[i].ProviderId).IsEqualTo("gateway")
                .Because($"entry {i} ('{result.Value[i].Id}') must carry the registry key, not '{result.Value[i].ProviderId}'");
        }
    }

    /// <summary>
    ///     The other direction, and the reason the first two are worth anything:
    ///     a catalog that is ALREADY stamped correctly comes back untouched —
    ///     same instances, same field values. A fix that blanket-rewrote every
    ///     member, or that re-stamped unconditionally into a new array on every
    ///     call, would fail here.
    /// </summary>
    [Test]
    public async Task HardcodedModel_AlreadyStampedCorrectly_IsReturnedUnchanged()
    {
        var catalog = new DynamicModelCatalog(
            new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK))),
            NewCacheDir(),
            NullLogger<DynamicModelCatalog>.Instance);

        var config = new ProviderConfig
        {
            Id = "gateway",
            DisplayName = "Gateway",
            BaseUrl = "https://api.gateway.example",
            Models = [Stamp("m1", "gateway")],
        };

        ModelInfo before = config.Models![0];
        Result<IReadOnlyList<ModelInfo>> result = await catalog.GetModelsAsync(config);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value[0].ProviderId).IsEqualTo("gateway");
        await Assert.That(result.Value[0].Id).IsEqualTo(before.Id)
            .Because("an already-correct catalog is returned as-is");
        await Assert.That(result.Value[0].DisplayName).IsEqualTo(before.DisplayName)
            .Because("re-stamping the provider must not disturb any other member");
        await Assert.That(result.Value[0].ContextWindow).IsEqualTo(before.ContextWindow)
            .Because("re-stamping the provider must not disturb any other member");
    }

    /// <summary>
    ///     The fetched path keeps deriving from the config — the fix must not
    ///     have narrowed to the hardcoded branch. `ParseModel` stamps
    ///     `config.Id`; this pins that a `/models` response still comes back
    ///     under the fetching provider's id.
    /// </summary>
    [Test]
    public async Task FetchedModel_StillCarriesTheFetchingProvidersOwnId()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{ "data": [ { "id": "served-model", "context_length": 4096 } ] }""",
                System.Text.Encoding.UTF8,
                "application/json"),
        });

        string cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(new HttpClient(handler), cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "gateway",
                DisplayName = "Gateway",
                BaseUrl = "https://api.gateway.example",
                ModelsUrl = "https://api.gateway.example/v1/models",
                ModelsPath = "data",
            };

            Result<IReadOnlyList<ModelInfo>> result = await catalog.GetModelsAsync(config);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Count).IsEqualTo(1);
            await Assert.That(result.Value[0].ProviderId).IsEqualTo("gateway")
                .Because(
                    "the /models path stamps config.Id (ProviderConfig.ParseModel) and must keep doing so — the "
                    + "hardcoded-array fix must not have narrowed the invariant to one branch");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    private static ModelInfo Stamp(string id, string providerId) =>
        new(id, providerId, id, 8192, 4096, false, false, true, Pricing.Unknown, "openai");

    private static string NewCacheDir() =>
        Path.Combine(Path.GetTempPath(), $"harbor-stamp-{Guid.NewGuid():N}");
}