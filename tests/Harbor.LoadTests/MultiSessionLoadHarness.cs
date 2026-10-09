using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.E2E.Framework;
using Harbor.Providers.OpenAiCompatible;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Storage.Sqlite;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.Registries.Agents;
using Harbor.Registries.Tools;
using Harbor.Registries.Events;

namespace Harbor.LoadTests;

/// <summary>
///     In-process multi-session load harness: composes the REAL agent stack
    ///     (shared <see cref="AgentLoop" /> singleton + shared
///     <see cref="InMemoryEventBus" /> + real <see cref="OpenAiCompatibleLlmClient" />
///     over HTTP/SSE + <see cref="MemorySessionStore" />) and drives
///     <c>sessionCount × agentsPerSession</c> agent runs against one
    ///     <see cref="MockLlmServer" /> in echo mode.
/// </summary>
/// <remarks>
///     <para>
///         <b>Determinism:</b> the only pacing mechanism is the
///         <see cref="TokenBucketRateLimiter" /> (refund-on-completion — no
///         wall-clock refill) and <see cref="MockLlmServer.SetChunkDelay" />
///         time dilation. The harness itself never sleeps on real time.
///     </para>
///     <para>
///         <b>Shape:</b> sessions run concurrently; the runs WITHIN one
///         session are sequential (the ISessionContext message list is not
///         safe for concurrent runs per its contract). With 10 sessions and 3
///         agents per session the suite keeps 10 runs in flight, shaped down
///         to the bucket capacity so streams interleave on the shared bus.
///     </para>
///     <para>
///         <b>Store backends (#420):</b> the durable store is selected via
///         <see cref="LoadStoreBackend" /> — memory (default, no I/O),
///         JSONL (one file per session under a temp dir) or SQLite (one temp
///         .db file). File-backed stores are created under
///         <c>Path.GetTempPath()</c> and deleted on dispose. Product code is
///         untouched: this switch lives entirely in the test harness.
///     </para>
/// </remarks>
public sealed class MultiSessionLoadHarness : IAsyncDisposable
{
    private const string Model = "test-model";

    private readonly MockLlmServer _server;
    private readonly InMemoryEventBus _bus;
    private readonly ISessionStore _store;
    private readonly string? _ownedRoot;
    private readonly AgentLoop _loop;
    private readonly TokenBucketRateLimiter _limiter;
    private readonly LoadSignals _signals;
    private readonly int _agentsPerSession;
    private readonly List<AgentDefinition> _agentDefs = [];
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<UiStore> _uiStores = [];
    private readonly List<LoadSessionContext> _contexts = [];

    private MultiSessionLoadHarness(
        MockLlmServer server,
        InMemoryEventBus bus,
        ISessionStore store,
        string? ownedRoot,
        AgentLoop loop,
        TokenBucketRateLimiter limiter,
        LoadSignals signals,
        int agentsPerSession)
    {
        _server = server;
        _bus = bus;
        _store = store;
        _ownedRoot = ownedRoot;
        _loop = loop;
        _limiter = limiter;
        _signals = signals;
        _agentsPerSession = agentsPerSession;
    }

    public LoadSignals Signals => _signals;

    public TokenBucketRateLimiter Limiter => _limiter;

    /// <summary>Backend selected for this harness instance (test matrix axis, #420).</summary>
    public LoadStoreBackend Backend { get; private set; } = LoadStoreBackend.Memory;

    /// <summary>Shared bus, exposed so ordering tests can record turn events (test-only seam).</summary>
    public IEventBus Bus => _bus;

    public IReadOnlyList<LoadSessionContext> Contexts => _contexts;

    /// <summary>Per-session UiStores bound to the shared bus (concurrent reducer dispatch under load).</summary>
    public IReadOnlyList<UiStore> Stores => _uiStores;

    /// <summary>
    ///     Spin up the whole stack: mock server (echo mode, dilated chunk
    ///     delay), shared bus, shared loop, bucket limiter, and one UiStore
    ///     subscribed per session (concurrent reducer dispatch under load).
    /// </summary>
    public static async Task<MultiSessionLoadHarness> StartAsync(
        int sessionCount,
        int agentsPerSession,
        int bucketCapacity = 6,
        TimeSpan? chunkDelay = null,
        LoadStoreBackend backend = LoadStoreBackend.Memory)
    {
        var server = new MockLlmServer();
        await server.StartAsync().ConfigureAwait(false);
        server.SetChunkDelay(chunkDelay ?? TimeSpan.FromMilliseconds(2));
        server.SetEchoResponse(Model);

        var bus = new InMemoryEventBus();
        (ISessionStore store, string? ownedRoot) = CreateStore(backend);
        var limiter = new TokenBucketRateLimiter(bucketCapacity);

        // Real OpenAI-compatible HTTP client → bucket → mock server.
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var config = LoadTestFakes.MockProvider(server.BaseUri);
        var inner = new OpenAiCompatibleLlmClient(
            http,
            config,
            new FixedAuthResolver("load-test-key"),
            new StaticModelCatalog(),
            NullLogger<OpenAiCompatibleLlmClient>.Instance);
        var client = new RateLimitedLlmClient(inner, limiter);

        var agents = new AgentRegistry();
        var agentDefs = new List<AgentDefinition>(agentsPerSession);
        for (int a = 0; a < agentsPerSession; a++)
        {
            AgentDefinition def = LoadTestFakes.Agent("agent-" + a);
            agentDefs.Add(def);
            agents.Register(def);
        }

        var loop = new AgentLoop(
            new SingleProviderRegistry(client),
            new ToolRegistry(),
            agents,
            new SystemPromptBuilder(NullLogger<SystemPromptBuilder>.Instance),
            new NoCompaction(),
            new TokenTracker(),
            new RetryPolicy(),
            bus,
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);

        var signals = new LoadSignals();
        var harness = new MultiSessionLoadHarness(server, bus, store, ownedRoot, loop, limiter, signals, agentsPerSession);
        harness.Backend = backend;
        harness._subscriptions.Add(signals.SubscribeBus(bus));
        harness._agentDefs.AddRange(agentDefs);
        for (int s = 0; s < sessionCount; s++)
        {
            // Each session gets its own UiStore bound to the shared bus — the
            // reducer's CAS loop must absorb 10 concurrent event streams.
            var uiStore = new UiStore();
            harness._uiStores.Add(uiStore);
            harness._subscriptions.Add(signals.SubscribeUiStore(bus, uiStore));
        }

        return harness;
    }

    /// <summary>
    ///     Run the matrix: <paramref name="sessionCount" /> concurrent session
    ///     pipelines, each running its agents sequentially. Returns per-session
    ///     run results (or the exception that failed the run).
    /// </summary>
    public async Task<SessionRunResult[]> RunAllAsync(CancellationToken ct = default)
    {
        var tasks = new Task<SessionRunResult>[_contexts.Count];
        for (int s = 0; s < _contexts.Count; s++)
        {
            LoadSessionContext ctx = _contexts[s];
            tasks[s] = Task.Run(() => RunSessionAsync(ctx, ct), ct);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Create <paramref name="sessionCount" /> sessions in the store.</summary>
    public async Task CreateSessionsAsync(int sessionCount, CancellationToken ct = default)
    {
        for (int s = 0; s < sessionCount; s++)
        {
            Result<Harbor.Abstractions.Models.Session> created =
                await _store.CreateAsync("/tmp/harbor-load-" + s, "agent-0", "mock", Model, ct)
                    .ConfigureAwait(false);
            if (created.IsFailure)
            {
                throw new InvalidOperationException("store.CreateAsync failed: " + created.Error);
            }

            _contexts.Add(new LoadSessionContext(_store, created.Value));
        }
    }

    /// <summary>Read the persisted transcript for a session (independent of the contexts).</summary>
    public Task<Result<IReadOnlyList<Harbor.Abstractions.Models.AgentMessage>>> ReadStoredAsync(string sessionId, CancellationToken ct = default) =>
        _store.GetMessagesAsync(sessionId, ct);

    private async Task<SessionRunResult> RunSessionAsync(LoadSessionContext ctx, CancellationToken ct)
    {
        var errors = new List<string>();
        int succeeded = 0;

        for (int a = 0; a < _agentsPerSession; a++)
        {
            AgentDefinition agent = AgentFor(a);
            string prompt = $"session-{ctx.Session.Id[..8]}-run-{a}-prompt";
            var message = new Harbor.Abstractions.Models.UserMessage(
                Guid.NewGuid().ToString("N"),
                ctx.Session.Id,
                DateTimeOffset.UtcNow,
                prompt,
                "user",
                Model);

            // Seed the prompt through the context (in-memory list + store) —
            // AgentLoop reads the request input from session.Messages and
            // persists only assistant messages itself.
            await ctx.AppendMessageAsync(message, ct).ConfigureAwait(false);

            CSharpFunctionalExtensions.Result result;
            try
            {
                result = await _loop.RunAsync(ctx, agent, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add($"run {a}: exception {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (result.IsSuccess)
            {
                succeeded++;
            }
            else
            {
                errors.Add($"run {a}: {result.Error}");
            }
        }

        return new SessionRunResult(ctx.Session.Id, succeeded, errors);
    }

    private AgentDefinition AgentFor(int index) => _agentDefs[index];

    /// <summary>
    ///     Backend switch for the #420 matrix. Memory is allocation-free;
    ///     file-backed backends each get an isolated temp root so parallel
    ///     test legs never share files or handles.
    /// </summary>
    private static (ISessionStore Store, string? OwnedRoot) CreateStore(LoadStoreBackend backend)
    {
        switch (backend)
        {
            case LoadStoreBackend.Memory:
                return (new MemorySessionStore(), null);
            case LoadStoreBackend.Jsonl:
            {
                string root = Path.Combine(Path.GetTempPath(), "harbor-load-" + Guid.NewGuid().ToString("N"));
                return (
                    new JsonlSessionStore(root, NullLogger<JsonlSessionStore>.Instance),
                    root);
            }
            case LoadStoreBackend.Sqlite:
            {
                string root = Path.Combine(Path.GetTempPath(), "harbor-load-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                return (
                    new SqliteSessionStore(
                        Path.Combine(root, "load.db"),
                        NullLogger<SqliteSessionStore>.Instance),
                    root);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown load store backend.");
        }
    }

    public static LoadStoreBackend ParseBackend(string name) =>
        name.ToLowerInvariant() switch
        {
            "memory" => LoadStoreBackend.Memory,
            "jsonl" => LoadStoreBackend.Jsonl,
            "sqlite" => LoadStoreBackend.Sqlite,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Expected memory|jsonl|sqlite."),
        };

    public async ValueTask DisposeAsync()
    {
        foreach (IDisposable sub in _subscriptions)
        {
            sub.Dispose();
        }

        _limiter.Dispose();
        await _server.StopAsync().ConfigureAwait(false);

        if (_ownedRoot is not null)
        {
            try
            {
                Directory.Delete(_ownedRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup: a lingering handle must not fail the test.
            }
            catch (UnauthorizedAccessException)
            {
                // Same — report-only cleanup, never a test failure.
            }
        }
    }
}

/// <summary>Durable-store axis of the #420 matrix (test-only; product code untouched).</summary>
public enum LoadStoreBackend
{
    Memory,
    Jsonl,
    Sqlite,
}

/// <summary>Per-session outcome of <see cref="MultiSessionLoadHarness.RunAllAsync" />.</summary>
public sealed record SessionRunResult(string SessionId, int SucceededRuns, List<string> Errors);
