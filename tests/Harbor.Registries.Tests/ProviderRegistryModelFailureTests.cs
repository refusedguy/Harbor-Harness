using System.Runtime.CompilerServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using TUnit.Assertions;

namespace Harbor.Registries.Tests;

/// <summary>
///     Issue #202 (B3) regression tests: <see cref="ProviderRegistry" /> model
///     fan-out must convert per-provider failures into error records — never
///     throw on <c>.Value</c>, and never cache a failure (a later retry must
///     re-fetch instead of serving a stale error).
/// </summary>
public class ProviderRegistryModelFailureTests
{
    private sealed class StubCatalogClient : ILlmClient
    {
        private readonly Result<IReadOnlyList<ModelInfo>> _modelsResult;

        public StubCatalogClient(ProviderId providerId, Result<IReadOnlyList<ModelInfo>> modelsResult)
        {
            ProviderId = providerId;
            _modelsResult = modelsResult;
        }

        public ProviderId ProviderId { get; }

        public int ModelCalls { get; private set; }

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
        {
            ModelCalls++;
            return Task.FromResult(_modelsResult);
        }
    }

    /// <summary>
    ///     A client whose model fetch never returns a result — it throws. Used by
    ///     the #588 tests to reach the fan-out's two catch blocks.
    /// </summary>
    private sealed class ThrowingCatalogClient : ILlmClient
    {
        private readonly Func<Exception> _throw;

        public ThrowingCatalogClient(ProviderId providerId, Func<Exception> throwFactory)
        {
            ProviderId = providerId;
            _throw = throwFactory;
        }

        public ProviderId ProviderId { get; }

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
            => throw _throw();
    }

    /// <summary>
    ///     An exception whose message says nothing. <see cref="Exception" />'s own
    ///     constructor substitutes a default message when handed an empty one, so
    ///     the only way to reach that state is to override the member.
    /// </summary>
    private sealed class SilentException : Exception
    {
        public override string Message => string.Empty;
    }

    private static ModelInfo TestModel(string provider, string id) =>
        new(id, provider, id, 128_000, 4096, false, false, false, Pricing.Unknown, "openai");

    private static Result<IReadOnlyList<ModelInfo>> ModelsFor(string provider, string id) =>
        Result.Success<IReadOnlyList<ModelInfo>>(new[] { TestModel(provider, id) });

    [Test]
    public async Task GetAllModelsAsync_PartialFailure_ReturnsHealthyModelsOnly()
    {
        var registry = new ProviderRegistry();
        var goodPid = ProviderId.Create("good");
        var badPid = ProviderId.Create("bad");
        registry.Register(goodPid, () => new StubCatalogClient(goodPid, ModelsFor("good", "good-model")));
        registry.Register(badPid, () => new StubCatalogClient(badPid, Result.Failure<IReadOnlyList<ModelInfo>>("boom")));

        var result = await registry.GetAllModelsAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(1);
        await Assert.That(result.Value[0].Id).IsEqualTo("good-model");
    }

    [Test]
    public async Task GetAllModelsAsync_AllFailing_ReturnsFailureMentioningProvider()
    {
        var registry = new ProviderRegistry();
        var badPid = ProviderId.Create("bad");
        registry.Register(badPid, () => new StubCatalogClient(badPid, Result.Failure<IReadOnlyList<ModelInfo>>("boom")));

        var result = await registry.GetAllModelsAsync();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("bad");
    }

    [Test]
    public async Task GetModelsCachedAsync_Failure_IsNotCached()
    {
        var registry = new ProviderRegistry();
        var pid = ProviderId.Create("flaky");
        var client = new StubCatalogClient(pid, Result.Failure<IReadOnlyList<ModelInfo>>("down"));
        registry.Register(pid, () => client);

        var first = await registry.GetModelsCachedAsync(pid);
        var second = await registry.GetModelsCachedAsync(pid);

        await Assert.That(first.IsFailure).IsTrue();
        await Assert.That(second.IsFailure).IsTrue();
        await Assert.That(client.ModelCalls).IsEqualTo(2);
    }

    [Test]
    public async Task GetModelsCachedAsync_Success_IsCached()
    {
        var registry = new ProviderRegistry();
        var pid = ProviderId.Create("stable");
        var client = new StubCatalogClient(pid, ModelsFor("stable", "stable-model"));
        registry.Register(pid, () => client);

        var first = await registry.GetModelsCachedAsync(pid);
        var second = await registry.GetModelsCachedAsync(pid);

        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(second.Value.Count).IsEqualTo(1);
        await Assert.That(client.ModelCalls).IsEqualTo(1);
    }

    [Test]
    public async Task GetModelsCachedAsync_Unregistered_ReturnsFailure()
    {
        var registry = new ProviderRegistry();

        var result = await registry.GetModelsCachedAsync(ProviderId.Create("ghost"));

        await Assert.That(result.IsFailure).IsTrue();
    }

    // =====================================================================
    // #588 — the fan-out's per-provider result channel.
    //
    // The hand-rolled `ModelBatch(ProviderId, Models, string? Error)` that used
    // to carry each task's outcome is gone: a provider task now returns the
    // client's own `Result<IReadOnlyList<ModelInfo>>`, and the provider id for
    // an error line comes from the fan-out's index. These four tests pin what
    // that swap must not lose. Two of them fail against the pre-#588 code; the
    // other two pin the partial-failure tolerance that #588 explicitly rejected
    // changing.
    // =====================================================================

    /// <summary>
    ///     Each failed provider contributes one <c>"id: reason"</c> line, and the id
    ///     must be the provider that actually failed. The result no longer carries
    ///     it — the aggregate reads it from the fan-out's index, and
    ///     <c>Task.WhenAll</c> hands results back in input order — so with more than
    ///     one provider in the batch a broken correspondence shows up here as two
    ///     mismatched messages.
    /// </summary>
    [Test]
    public async Task GetAllModelsAsync_SeveralProvidersFail_NamesEachOneWithItsOwnReason()
    {
        var registry = new ProviderRegistry();
        var first = ProviderId.Create("alpha");
        var second = ProviderId.Create("beta");
        registry.Register(first, () => new StubCatalogClient(first, Result.Failure<IReadOnlyList<ModelInfo>>("alpha-down")));
        registry.Register(second, () => new StubCatalogClient(second, Result.Failure<IReadOnlyList<ModelInfo>>("beta-down")));

        var result = await registry.GetAllModelsAsync();

        await Assert.That(result.IsFailure).IsTrue()
            .Because("no provider answered, so there is nothing to return");

        await Assert.That(result.Error).Contains("alpha: alpha-down")
            .Because(
                "one 'id: reason' line per failed provider, each naming the provider that actually "
                + "failed rather than whichever one happened to occupy the fan-out slot");
        await Assert.That(result.Error).Contains("beta: beta-down");
    }

    /// <summary>
    ///     The aggregate must always say WHY a provider failed. An exception with an
    ///     empty message used to be handed to the result as-is, so the line came out
    ///     as <c>"mute: "</c> — a provider named, with no reason at all. That is the
    ///     "failure with nothing to say" a hand-rolled <c>string? Error</c> could
    ///     not represent, and <c>Result.Failure&lt;T&gt;("")</c> refuses to build.
    /// </summary>
    [Test]
    public async Task GetAllModelsAsync_ProviderThrowsWithEmptyMessage_StillGivesAReason()
    {
        var registry = new ProviderRegistry();
        var pid = ProviderId.Create("mute");
        registry.Register(pid, () => new ThrowingCatalogClient(pid, static () => new SilentException()));

        var result = await registry.GetAllModelsAsync();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("mute: SilentException")
            .Because(
                "the catch block has to name the exception when its message is empty, or the "
                + "aggregate reports a provider failure with a blank reason");
    }

    /// <summary>
    ///     A cancelled fetch used to be flattened to the bare literal
    ///     <c>"timeout"</c>, which threw away the reason the operation was cancelled
    ///     — the same "well-typed error that says nothing" defect #561 removed from
    ///     <c>CompilationResult.Error</c>.
    /// </summary>
    [Test]
    public async Task GetAllModelsAsync_ProviderCancels_KeepsWhyItWasCancelled()
    {
        var registry = new ProviderRegistry();
        var pid = ProviderId.Create("slow");
        registry.Register(
            pid,
            () => new ThrowingCatalogClient(pid, static () => new OperationCanceledException("probe cancelled")));

        var result = await registry.GetAllModelsAsync();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("timed out after 5000 ms")
            .Because(
                "the per-provider budget is 5 s, and the bare literal \"timeout\" that used to stand "
                + "in for it said nothing about which of the three cancellations this was");
        await Assert.That(result.Error).Contains("probe cancelled");
    }

    /// <summary>
    ///     A provider that answers with an empty catalogue is not a failure, and it
    ///     does not cancel the others. #588 rejected replacing the fan-out with
    ///     <c>Result.Combine</c> for exactly this reason: <c>Combine</c> is
    ///     all-or-nothing, so a machine with Ollama stopped would come back with no
    ///     models at all instead of with the other providers'.
    /// </summary>
    [Test]
    public async Task GetAllModelsAsync_OnlyEmptyCatalogues_IsNotAFailure()
    {
        var registry = new ProviderRegistry();
        var empty = ProviderId.Create("empty");
        registry.Register(
            empty,
            () => new StubCatalogClient(empty, Result.Success<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>())));

        var result = await registry.GetAllModelsAsync();

        await Assert.That(result.IsSuccess).IsTrue()
            .Because("an empty catalogue is a successful fetch of nothing, not a failed fetch");
        await Assert.That(result.Value.Count).IsEqualTo(0);
    }
}
