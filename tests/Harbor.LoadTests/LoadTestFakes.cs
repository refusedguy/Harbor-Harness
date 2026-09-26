using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Providers.OpenAiCompatible;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.LoadTests;

/// <summary>
///     Minimal in-memory composition fakes for the multi-session load
///     harness. Deliberately NOT shared with Harbor.Application.Tests fakes —
///     the load suite composes only what the concurrency assertions need, and
///     keeping the set tiny makes the harness contract explicit.
/// </summary>
public static class LoadTestFakes
{
    /// <summary>The single mock model every load-test agent runs on.</summary>
    public static ModelInfo TestModel { get; } =
        new("test-model", "mock", "Mock Load Model", 128_000, 4096, false, false, true, Pricing.Unknown, "openai");

    /// <summary>
    ///     A <see cref="ProviderConfig" /> whose BaseUrl points at the running
    ///     <see cref="MockLlmServer" /> — the real OpenAI-compatible client
    ///     streams over HTTP/SSE against it (no scripted in-process client).
    /// </summary>
    public static ProviderConfig MockProvider(Uri baseUri) => new()
    {
        Id = "mock",
        DisplayName = "Mock LLM (Load)",
        BaseUrl = baseUri.ToString(),
        ApiType = "openai-compatible",
        AuthType = "bearer",
        AuthEnvVar = "MOCK_API_KEY",
        Models = [TestModel],
    };

    /// <summary>
    ///     The canonical echo reply for a user prompt: SHA-256 of the prompt,
    ///     first 12 hex chars, prefixed with <c>echo-</c> — byte-identical to
    ///     what <see cref="MockLlmServer" /> echo mode streams back.
    /// </summary>
    public static string ExpectedEcho(string userPrompt)
    {
        string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userPrompt)));
        return "echo-" + hash[..12].ToLowerInvariant();
    }

    /// <summary>Allow-all ruleset — no permission denials under load.</summary>
    public static AgentDefinition Agent(string name) => new(
        AgentName.Create(name),
        name,
        "Load-test agent",
        "test-model",
        "mock",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));
}

/// <summary>Auth resolver that always yields the fixed test key (ROP-A ПР.6 seam).</summary>
public sealed class FixedAuthResolver(string key) : IAuthResolver
{
    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(key));
}

/// <summary>Catalog serving exactly the mock model, no HTTP.</summary>
public sealed class StaticModelCatalog : IModelCatalog
{
    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(ProviderConfig config, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([LoadTestFakes.TestModel]));
}

/// <summary>
///     Registry exposing a single pre-built client for every provider id
///     (the load suite registers exactly one mock provider).
/// </summary>
public sealed class SingleProviderRegistry(ILlmClient client) : IProviderRegistry
{
    public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => [client.ProviderId];

    public Result<ILlmClient> GetClient(ProviderId providerId) => Result.Success(client);

    public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default) =>
        client.GetModelsAsync(cancellationToken);

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default) =>
        client.GetModelsAsync(cancellationToken);

    public void Register(ProviderId providerId, Func<ILlmClient> factory) { }

    public Result Unregister(ProviderId providerId) => Result.Failure("SingleProviderRegistry does not support unregister.");
}

/// <summary>
///     Compaction is out of scope for the load suite — the scripted
///     transcripts are far below the context window. Returns the real
///     service's "no compaction needed" answer without token estimation cost.
/// </summary>
public sealed class NoCompaction : ICompactionService
{
    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => false;

    public Task<Result<CompactionResult>> CompactAsync(
        string sessionId,
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<CompactionResult>("NoCompaction never compacts."));
}

/// <summary>
///     Store-backed per-run session context: appends persist to the shared
    ///     <see cref="ISessionStore" /> so corruption assertions read the
///     persisted transcript INDEPENDENTLY of the in-memory message list.
    ///     One context serves ONE run at a time (the ISessionContext contract:
///     the message list is not safe for concurrent runs — load runs within a
///     session are strictly sequential).
/// </summary>
public sealed class LoadSessionContext : ISessionContext
{
    private readonly ISessionStore _store;
    private readonly List<AgentMessage> _messages = [];

    public LoadSessionContext(ISessionStore store, Session session)
    {
        _store = store;
        Session = session;
    }

    public Session Session { get; }

    public IReadOnlyList<AgentMessage> Messages => _messages;

    public Channel<AgentMessage> SteeringQueue { get; } = Channel.CreateUnbounded<AgentMessage>();

    public async Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default)
    {
        lock (_messages)
        {
            _messages.Add(message);
        }

        Result result = await _store.AppendMessageAsync(Session.Id, message, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"store.AppendMessageAsync failed for session {Session.Id}: {result.Error}");
        }
    }

    public Task UpdateStatsAsync(Usage usage, CancellationToken ct = default) => Task.CompletedTask;

    public void EnqueueSteering(params AgentMessage[] messages)
    {
        foreach (AgentMessage message in messages)
        {
            SteeringQueue.Writer.TryWrite(message);
        }
    }
}

/// <summary>
///     Deterministic token-bucket admission control for LLM calls. The
    ///     bucket starts full with <paramref name="capacity" /> tokens; each
///     in-flight LLM stream holds one token and refunds it on completion.
    ///     Refill is driven by ACTUAL COMPLETIONS, not wall-clock timers — so
///     the shape of the load (who streams when) is a pure function of the
///     system's own progress, with zero real-time sleeps in the harness.
/// </summary>
public sealed class TokenBucketRateLimiter : IDisposable
{
    private readonly SemaphoreSlim _tokens;
    private int _inFlight;
    private int _peakInFlight;

    public TokenBucketRateLimiter(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        _tokens = new SemaphoreSlim(capacity, capacity);
    }

    public int Capacity { get; }

    /// <summary>Highest number of simultaneously in-flight streams observed.</summary>
    public int PeakInFlight => Volatile.Read(ref _peakInFlight);

    /// <summary>Total admissions granted so far.</summary>
    public int TotalAdmissions => Volatile.Read(ref _totalAdmissions);
    private int _totalAdmissions;

    /// <summary>Blocks asynchronously until a token is available.</summary>
    public async ValueTask AcquireAsync(CancellationToken ct = default)
    {
        await _tokens.WaitAsync(ct).ConfigureAwait(false);
        int now = Interlocked.Increment(ref _inFlight);
        Interlocked.Increment(ref _totalAdmissions);

        int peak = Volatile.Read(ref _peakInFlight);
        while (now > peak &&
               Interlocked.CompareExchange(ref _peakInFlight, now, peak) != peak)
        {
            peak = Volatile.Read(ref _peakInFlight);
        }
    }

    /// <summary>Refund the token held by a completed (or faulted) stream.</summary>
    public void Refund()
    {
        Interlocked.Decrement(ref _inFlight);
        _tokens.Release();
    }

    public void Dispose() => _tokens.Dispose();
}

/// <summary>
///     <see cref="ILlmClient" /> decorator that gates every streaming call
    ///     through a <see cref="TokenBucketRateLimiter" /> and records the
///     concurrency the bucket actually admitted (proof the shaping worked).
/// </summary>
public sealed class RateLimitedLlmClient : ILlmClient
{
    private readonly ILlmClient _inner;
    private readonly TokenBucketRateLimiter _limiter;

    public RateLimitedLlmClient(ILlmClient inner, TokenBucketRateLimiter limiter)
    {
        _inner = inner;
        _limiter = limiter;
        ProviderId = inner.ProviderId;
    }

    public ProviderId ProviderId { get; }

    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _limiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (LlmEvent evt in _inner.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            _limiter.Refund();
        }
    }

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) =>
        _inner.GetModelsAsync(cancellationToken);
}

/// <summary>
///     Thread-safe collector of bus-level signals: how many AgentStart /
    ///     AgentEnd events were published, dispatch exceptions inside UiStore
///     subscribers, and per-session UiStore final states.
/// </summary>
public sealed class LoadSignals
{
    private readonly ConcurrentDictionary<string, byte> _dispatchErrors = new();
    private int _agentStarts;
    private int _agentEnds;

    public int AgentStarts => Volatile.Read(ref _agentStarts);
    public int AgentEnds => Volatile.Read(ref _agentEnds);
    public IReadOnlyCollection<string> DispatchErrors => [.. _dispatchErrors.Keys];

    public IDisposable SubscribeBus(IEventBus bus)
    {
        return bus.Subscribe((AgentEvent evt, CancellationToken _) =>
        {
            switch (evt)
            {
                case AgentStartEvent:
                    Interlocked.Increment(ref _agentStarts);
                    break;
                case AgentEndEvent:
                    Interlocked.Increment(ref _agentEnds);
                    break;
            }

            return ValueTask.CompletedTask;
        });
    }

    public IDisposable SubscribeUiStore(IEventBus bus, UiStore store)
    {
        return bus.Subscribe((AgentEvent evt, CancellationToken _) =>
        {
            try
            {
                store.Dispatch(evt);
            }
            catch (Exception ex)
            {
                // The bus must never observe a subscriber throw; record the
                // failure for the assertions instead of crashing the stream.
                _dispatchErrors.TryAdd("dispatch:" + ex.GetType().Name + ":" + ex.Message, 0);
            }

            return ValueTask.CompletedTask;
        });
    }
}
