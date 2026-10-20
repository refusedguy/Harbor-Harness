using System.Runtime.CompilerServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Registries.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using Harbor.TestKit;

namespace Harbor.Core.Tests;

/// <summary>
///     #1106 (no-retry summary): the summarization stream retries transient
///     provider failures on the main-loop budget instead of falling straight
///     through to destructive truncation. Fatal failures still fail fast.
/// </summary>
public class CompactionSummaryRetryTests
{
    private static readonly ModelInfo HugeModel = new(
        "test-model", "test", "Test Model", 1_000_000, 1_024, false, false, true, Pricing.Unknown, "openai");

    private static CompactionService CreateService(ILlmClient client, IRetryPolicy? retry = null)
    {
        var providers = new ProviderRegistry();
        providers.Register(ProviderId.Create("test"), () => client);
        return new CompactionService(
            new TokenTracker(),
            providers,
            NullLogger<CompactionService>.Instance,
            null,
            retry)
        {
            KeepRecentTokens = 60,
            TailTurns = 0
        };
    }

    private static List<AgentMessage> FourLongMessages() =>
    [
        new UserMessage(Guid.NewGuid().ToString("N"), "session-1", DateTimeOffset.UtcNow,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "code", "test-model"),
        new AssistantMessage(Guid.NewGuid().ToString("N"), "session-1", DateTimeOffset.UtcNow,
            new[] { new TextPart("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb") }, StopReason.Stop, new Usage(0, 0), "test-model"),
        new UserMessage(Guid.NewGuid().ToString("N"), "session-1", DateTimeOffset.UtcNow,
            "cccccccccccccccccccccccccccccccccccccccc", "code", "test-model"),
        new AssistantMessage(Guid.NewGuid().ToString("N"), "session-1", DateTimeOffset.UtcNow,
            new[] { new TextPart("dddddddddddddddddddddddddddddddddddd") }, StopReason.Stop, new Usage(0, 0), "test-model")
    ];

    [Test]
    public async Task CompactAsync_TransientRateLimitThenSuccess_RetriesOnceAndSucceeds()
    {
        var client = new ScriptedLlmClient(
            [TestEvents.LlmError("rate limited", kind: ProviderErrorKind.RateLimit, statusCode: 429)],
            [TestEvents.TextDelta("0", "compressed summary of the pruned head")]);
        var svc = CreateService(client, new ImmediateRetryPolicy());

        var result = await svc.CompactAsync("session-1", FourLongMessages(), HugeModel);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(client.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task CompactAsync_FatalAuthError_DoesNotRetry()
    {
        var client = new ScriptedLlmClient(
            [TestEvents.LlmError("bad key", kind: ProviderErrorKind.Auth, statusCode: 401)]);
        var policy = new ImmediateRetryPolicy();
        var svc = CreateService(client, policy);

        var result = await svc.CompactAsync("session-1", FourLongMessages(), HugeModel);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("LLM error during compaction");
        await Assert.That(client.Calls).IsEqualTo(1);
        await Assert.That(policy.Executions).IsEqualTo(1);
    }

    [Test]
    public async Task CompactAsync_TransientBudgetExhausted_FailsAfterMaxAttempts()
    {
        var client = new ScriptedLlmClient(
            [TestEvents.LlmError("overloaded", kind: ProviderErrorKind.ServerError, statusCode: 503)],
            [TestEvents.LlmError("overloaded", kind: ProviderErrorKind.ServerError, statusCode: 503)]);
        var svc = CreateService(client, new ImmediateRetryPolicy(maxAttempts: 2));

        var result = await svc.CompactAsync("session-1", FourLongMessages(), HugeModel);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("LLM error during compaction");
        await Assert.That(client.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task CompactAsync_NoPolicy_TransientFailsWithoutRetry()
    {
        var client = new ScriptedLlmClient(
            [TestEvents.LlmError("rate limited", kind: ProviderErrorKind.RateLimit, statusCode: 429)]);
        var svc = CreateService(client);

        var result = await svc.CompactAsync("session-1", FourLongMessages(), HugeModel);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(client.Calls).IsEqualTo(1);
    }

    /// <summary>Streams a scripted event list per attempt; extra attempts stream nothing.</summary>
    private sealed class ScriptedLlmClient(params IReadOnlyList<LlmEvent>[] scripts) : ILlmClient
    {
        private readonly Queue<IReadOnlyList<LlmEvent>> _scripts = new(scripts);

        public int Calls;

        public ProviderId ProviderId => ProviderId.Create("test");

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            IReadOnlyList<LlmEvent> events = _scripts.Count > 0 ? _scripts.Dequeue() : [];
            foreach (LlmEvent evt in events)
            {
                await Task.Yield();
                yield return evt;
            }
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(new[] { HugeModel }));
    }

    /// <summary>Retries transient stream errors immediately (no clock), so tests never sleep.</summary>
    private sealed class ImmediateRetryPolicy(int maxAttempts = 3) : IRetryPolicy
    {
        public int Executions;

        public Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            RetryOptions options,
            CancellationToken ct)
            => ExecuteAsync(operation, options, null, ct);

        public async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            RetryOptions options,
            Action<Exception, int>? onRetry,
            CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Executions++;
                    return await operation(ct).ConfigureAwait(false);
                }
                catch (LlmStreamErrorException lex)
                    when (attempt < maxAttempts && RetryPolicy.IsTransient(lex, out _))
                {
                    onRetry?.Invoke(lex, attempt);
                }
            }
        }

        /// <summary>
        ///     The value-shaped retry path (#1514) is not the one the compaction
        ///     summary uses — that path still reports its failures as
        ///     exceptions — so the fake refuses it loudly instead of pretending
        ///     to retry.
        /// </summary>
        public Task<Result<T, StreamFailure>> ExecuteValueAsync<T>(
            Func<CancellationToken, Task<Result<T, StreamFailure>>> operation,
            RetryOptions options,
            Action<StreamFailure, int>? onRetry,
            CancellationToken ct)
            => throw new NotSupportedException(
                "The value-shaped retry path is not used by the compaction summary retry.");
    }
}
