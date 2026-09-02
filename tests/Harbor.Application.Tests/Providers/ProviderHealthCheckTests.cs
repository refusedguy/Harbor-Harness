using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Application.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Application.Tests.Providers;
/// <summary>
///     PROD-UI-0 З.2 — <see cref="ProviderHealthCheck" /> probes a provider
///     through its registered client and classifies failures into
///     user-presentable reasons (bad key / unreachable / unknown host).
/// </summary>
public class ProviderHealthCheckTests
{
    private static ProviderHealthCheck CreateCheck(ILlmClient client)
    {
        var registry = new FakeRegistry(client);
        return new ProviderHealthCheck(registry, NullLogger<ProviderHealthCheck>.Instance);
    }

    [Test]
    public async Task CheckAsync_Success_ReturnsLatencyAndModelCount()
    {
        var check = CreateCheck(new FakeClient(Result.Success<IReadOnlyList<ModelInfo>>([
            MakeModel("m1"),
            MakeModel("m2")
        ])));

        var result = await check.CheckAsync(ProviderId.TryCreate("test").Value);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ModelsCount).IsEqualTo(2);
        await Assert.That(result.Value.LatencyMs).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task CheckAsync_UnregisteredProvider_Fails()
    {
        var registry = new FakeRegistry(null);
        var check = new ProviderHealthCheck(registry, NullLogger<ProviderHealthCheck>.Instance);

        var result = await check.CheckAsync(ProviderId.TryCreate("ghost").Value);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("not registered");
    }

    [Test]
    public async Task CheckAsync_EmptyModelList_Fails()
    {
        var check = CreateCheck(new FakeClient(Result.Success<IReadOnlyList<ModelInfo>>([])));

        var result = await check.CheckAsync(ProviderId.TryCreate("empty").Value);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("no models");
    }

    [Test]
    public async Task CheckAsync_401Error_ClassifiedAsBadKey()
    {
        var check = CreateCheck(new FakeClient(
            Result.Failure<IReadOnlyList<ModelInfo>>("Response status code does not indicate success: 401 (Unauthorized).")));

        var result = await check.CheckAsync(ProviderId.TryCreate("locked").Value);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("key is invalid");
    }

    [Test]
    public async Task CheckAsync_DnsError_ClassifiedAsUnknownHost()
    {
        var check = CreateCheck(new FakeClient(
            Result.Failure<IReadOnlyList<ModelInfo>>("No such host is known. (api.nonexistent.invalid)")));

        var result = await check.CheckAsync(ProviderId.TryCreate("dnsfail").Value);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Unknown host");
    }

    [Test]
    public async Task CheckAsync_Timeout_FailsWithUnreachableMessage()
    {
        // A client that never answers → the check's own budget must fire and
        // be reported as "unreachable (timed out)", not leak a raw cancel.
        var check = new ProviderHealthCheck(
            new FakeRegistry(new HangingClient()),
            NullLogger<ProviderHealthCheck>.Instance,
            timeout: TimeSpan.FromMilliseconds(250));

        var result = await check.CheckAsync(ProviderId.TryCreate("slow").Value);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("unreachable");
    }

    [Test]
    public async Task Classify_ConnectionRefused_MappedToUnreachable()
    {
        string msg = ProviderHealthCheck.Classify("Connection refused to localhost:11434");

        await Assert.That(msg).Contains("unreachable");
    }

    [Test]
    public async Task Classify_UnknownError_PassThroughWithPeriod()
    {
        string msg = ProviderHealthCheck.Classify("something exotic happened");

        await Assert.That(msg).IsEqualTo("something exotic happened.");
    }

    private static ModelInfo MakeModel(string id) => new(
        Id: id,
        ProviderId: "fake",
        DisplayName: id,
        ContextWindow: 8192,
        MaxOutputTokens: 4096,
        SupportsReasoning: false,
        SupportsVision: false,
        SupportsToolUse: false,
        Pricing: Pricing.Unknown,
        PromptTemplate: "openai");

    private sealed class FakeRegistry(ILlmClient? client) : IProviderRegistry
    {
        public IReadOnlyList<ProviderId> GetRegisteredProviderIds() =>
            client is null ? [] : [client.ProviderId];

        public Result<ILlmClient> GetClient(ProviderId providerId) =>
            client is null
                ? Result.Failure<ILlmClient>($"Provider '{providerId.Value}' is not registered.")
                : Result.Success(client);

        public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default)
        {
            if (client is null)
                return Task.FromResult(Result.Failure<IReadOnlyList<ModelInfo>>("no providers"));
            return client.GetModelsAsync(cancellationToken);
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default) =>
            GetAllModelsAsync(cancellationToken);

        public void Register(ProviderId providerId, Func<ILlmClient> factory) { }

        public Result Unregister(ProviderId providerId) =>
            client is null
                ? Result.Failure($"Provider '{providerId.Value}' is not registered.")
                : Result.Success();
    }

    private sealed class FakeClient(Result<IReadOnlyList<ModelInfo>> outcome) : ILlmClient
    {
        public ProviderId ProviderId { get; } = ProviderId.TryCreate("fake").Value;

        public IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            async IAsyncEnumerable<LlmEvent> Empty()
            {
                await Task.CompletedTask;
                yield break;
            }
            return Empty();
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(outcome);
    }

    private sealed class HangingClient : ILlmClient
    {
        public ProviderId ProviderId { get; } = ProviderId.TryCreate("hanging").Value;

        public IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            async IAsyncEnumerable<LlmEvent> Empty()
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                yield break;
            }
            return Empty();
        }

        public async Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return Result.Failure<IReadOnlyList<ModelInfo>>("unreachable");
        }
    }
}
