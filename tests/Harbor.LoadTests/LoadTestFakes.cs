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
using Harbor.TestKit;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.LoadTests;

/// <summary>
///     The load harness's own composition facts: the mock model every
///     load-test agent runs on, the provider config that points at the mock
///     server, the canonical echo reply and the allow-all ruleset.
/// </summary>
/// <remarks>
///     <para>
///         The doubles this file used to carry — the single-provider registry,
///         the never-compacting compaction service, and the rate-limited LLM
///         client with its token bucket — moved to the shared kit in #1825.
///         They were not load-specific: the registry is the kit's
///         <c>FakeProviderRegistry</c>, the compaction service is the kit's
///         <c>FakeCompactionService</c>, and the rate limiter is the kit's
///         <c>RateLimitedLlmClient</c> / <c>TokenBucketRateLimiter</c>.
///     </para>
///     <para>
///         The two doubles that REMAIN here are load-specific, and each says
///         why below: the model catalogue serves the load suite's mock model,
///         and the session context persists to the store so the corruption
///         assertions can read the persisted transcript.
///     </para>
/// </remarks>
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
/// <summary>
///     Catalog serving exactly the mock model, no HTTP.
/// </summary>
/// <remarks>
///     This one stays local: <c>IModelCatalog</c> is declared in
///     <c>Harbor.Providers.OpenAiCompatible</c>, a product assembly. A shared
///     double for it would have to reach that assembly, and the closure-neutral
///     part of the shared kit may not — it is the one part every test project
///     can take without gaining a product assembly (#1825).
/// </remarks>
public sealed class StaticModelCatalog : IModelCatalog
{
    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(ProviderConfig config, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([LoadTestFakes.TestModel]));
}

/// <summary>
///     Store-backed per-run session context: appends persist to the shared
    ///     <see cref="ISessionStore" /> so corruption assertions read the
///     persisted transcript INDEPENDENTLY of the in-memory message list.
    ///     One context serves ONE run at a time (the ISessionContext contract:
///     the message list is not safe for concurrent runs — load runs within a
///     session are strictly sequential).
/// </summary>
/// <remarks>
///     This one stays local: the shared kit's session context is an in-memory
///     list, and the load suite needs the DURABLE transcript — that is the
///     thing its corruption assertions read (#1825).
/// </remarks>
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

    public Task UpdateStatsAsync(Usage usage, Pricing pricing, CancellationToken ct = default) => Task.CompletedTask;

    public void EnqueueSteering(params AgentMessage[] messages)
    {
        foreach (AgentMessage message in messages)
        {
            SteeringQueue.Writer.TryWrite(message);
        }
    }
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
                store.Dispatch(new ChatAppMsg.Agent(evt));
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
