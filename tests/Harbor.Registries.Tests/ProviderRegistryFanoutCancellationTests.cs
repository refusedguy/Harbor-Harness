using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using TUnit.Assertions;
using Harbor.Registries.Providers;
// #1769: the LLM double is generated — the fake only declares what it needs.
using FakeLlmClientBase = Harbor.TestKit.Generated.FakeLlmClient;


namespace Harbor.Registries.Tests;

/// <summary>
///     Issue #1108 regression tests: a cancelled
///     <see cref="ProviderRegistry.GetAllModelsAsync" /> fan-out degrades to
///     partial results — it never throws <see cref="OperationCanceledException" />.
///     The dispatches used to be started with the outer token passed to
///     <c>Task.Run</c>, so a pre-start cancellation landed a cancelled task in
///     the <c>WhenAll</c> below and its await threw, discarding every healthy
///     provider's catalog with it.
/// </summary>
public class ProviderRegistryFanoutCancellationTests
{
    private sealed class StubCatalogClient : FakeLlmClientBase
    {
        public StubCatalogClient(ProviderId providerId, Result<IReadOnlyList<ModelInfo>> modelsResult)
        {
            ProviderId = providerId;
            GetModelsAsyncResult = modelsResult;
        }
    }

    private sealed class CancellingCatalogClient : FakeLlmClientBase
    {
        public CancellingCatalogClient(ProviderId providerId) => ProviderId = providerId;

        public override Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromCanceled<Result<IReadOnlyList<ModelInfo>>>(cancellationToken);
    }

    private static ModelInfo TestModel(string provider, string id) =>
        new(id, provider, id, 128_000, 4096, false, false, false, Pricing.Unknown, "openai");

    /// <summary>
    ///     A token cancelled before the fan-out starts must not turn the
    ///     aggregate into a throw: the dispatch still runs (its linked
    ///     per-provider CTS converts cancellation into one more
    ///     <c>"id: reason"</c> line) and healthy providers are returned.
    /// </summary>
    [Test]
    public async Task GetAllModelsAsync_PreCancelledToken_ReturnsHealthyModels()
    {
        var registry = new ProviderRegistry();
        var goodPid = ProviderId.Create("good");
        var badPid = ProviderId.Create("bad");
        registry.Register(
            goodPid,
            () => new StubCatalogClient(
                goodPid,
                Result.Success<IReadOnlyList<ModelInfo>>(new[] { TestModel("good", "good-model") })));
        registry.Register(badPid, () => new CancellingCatalogClient(badPid));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await registry.GetAllModelsAsync(cts.Token);

        await Assert.That(result.IsSuccess).IsTrue()
            .Because("cancellation degrades the fan-out to partial results — it never throws OCE");
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(result.Value[0].Id).IsEqualTo("good-model");
    }

    /// <summary>
    ///     Single-provider variant: even with nothing healthy to return, a
    ///     pre-cancelled fan-out answers with a failure result naming the
    ///     provider — not with an <see cref="OperationCanceledException" />
    ///     escaping to the caller.
    /// </summary>
    [Test]
    public async Task GetAllModelsAsync_PreCancelledTokenAllFailing_ReturnsFailureNotThrow()
    {
        var registry = new ProviderRegistry();
        var pid = ProviderId.Create("slow");
        registry.Register(pid, () => new CancellingCatalogClient(pid));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await registry.GetAllModelsAsync(cts.Token);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("slow");
    }
}
