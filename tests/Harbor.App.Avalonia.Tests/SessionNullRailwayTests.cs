using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Memory;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Regression tests for issue #100 (null-railway migration):
///     <see cref="SessionFactory"/> (CreateDefault/CreateNew/CreateBranch) and the
///     <see cref="SessionManager"/> call sites return <see cref="Result{Session}"/>
///     instead of null, so store failures surface honestly instead of
///     degrading to silent nulls.
/// </summary>
public class SessionNullRailwayTests
{
    private sealed class FakeLogger<T> : ILogger<T>, ILogger
    {
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable BeginScope<TState>(TState state) => null!;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class FakeAgent : IAgent
    {
        public CancellationTokenSource AbortSource { get; } = new();
        public AgentState State { get; set; } = AgentState.Idle("none", AgentDefinition.CodeDefault("m", "p"));
        public Task<Result> PromptAsync(string text, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ResetAbortSource() { }
        public void Initialize(Session session, AgentDefinition agent) { }
        public void Steer(AgentMessage message) { }
        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) => NullDisposable.Instance;
        public void Dispose() { }
    }

    private sealed class FakeAgentRegistry : IAgentRegistry
    {
        public List<AgentDefinition> Agents { get; } = new() { AgentDefinition.CodeDefault("test-model", "test-provider") };
        public IReadOnlyList<AgentDefinition> GetAllAgents() => Agents;
        public Result<AgentDefinition> GetAgent(AgentName name)
        {
            var found = Agents.FirstOrDefault(a => a.Name.Value == name.Value);
            return found is null
                ? Result.Failure<AgentDefinition>($"Agent '{name.Value}' not registered.")
                : Result.Success(found);
        }
        public Result Register(AgentDefinition agent)
        {
            Agents.Add(agent);
            return Result.Success();
        }
        public Result Unregister(AgentName name)
        {
            int removed = Agents.RemoveAll(a => a.Name.Value == name.Value);
            return removed > 0
                ? Result.Success()
                : Result.Failure($"Agent '{name.Value}' not registered.");
        }
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object?> _services = new();
        public void Add<T>(T instance) where T : class => _services[typeof(T)] = instance;
        public object? GetService(Type serviceType) => _services.TryGetValue(serviceType, out var s) ? s : null;
    }

    private sealed class FakeChatViewBinder : IChatViewBinder
    {
        public void Rebind(UiStore store) { }
    }

    /// <summary>
    ///     <see cref="ISessionStore"/> decorator over <see cref="MemorySessionStore"/>
    ///     with per-operation failure injection for the honest-error paths.
    /// </summary>
    private sealed class ControllableStore : ISessionStore
    {
        private readonly MemorySessionStore _inner = new();
        public bool FailCreate { get; set; }
        public bool FailGetMessages { get; set; }
        public bool FailAppend { get; set; }

        public Task<Result<Session>> CreateAsync(string directory, string agentName, string providerId, string modelId, CancellationToken ct = default) =>
            FailCreate
                ? Task.FromResult(Result.Failure<Session>("boom-create"))
                : _inner.CreateAsync(directory, agentName, providerId, modelId, ct);

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default) =>
            _inner.GetAsync(sessionId, ct);

        public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default) =>
            _inner.ListAsync(projectId, ct);

        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) =>
            FailAppend
                ? Task.FromResult(Result.Failure("boom-append"))
                : _inner.AppendMessageAsync(sessionId, message, ct);

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) =>
            _inner.UpdateMessageAsync(sessionId, message, ct);

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default) =>
            FailGetMessages
                ? Task.FromResult(Result.Failure<IReadOnlyList<AgentMessage>>("boom-history"))
                : _inner.GetMessagesAsync(sessionId, ct);

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default) =>
            _inner.DeleteAsync(sessionId, ct);

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default) =>
            _inner.UpdateAsync(session, ct);

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default) =>
            _inner.GetStatsAsync(sessionId, ct);

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default) =>
            _inner.UpdateStatsAsync(sessionId, metadata, ct);

        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default) =>
            _inner.DeleteMessagesAfterAsync(sessionId, messageId, ct);
    }

    private static (SessionManager Manager, SessionFactory Factory, ControllableStore Store) CreateGraph(ControllableStore? store = null)
    {
        var sessionStore = store ?? new ControllableStore();
        var agent = new FakeAgent();
        var services = new FakeServiceProvider();
        var agents = new FakeAgentRegistry();
        services.Add<IAgentRegistry>(agents);
        var factory = new SessionFactory(services, agents, agent, sessionStore, new FakeLogger<SessionFactory>());
        var switcher = new SessionSwitcher(agent, sessionStore, agents, new FakeLogger<SessionSwitcher>());
        var manager = new SessionManager(
            services,
            agents,
            agent,
            sessionStore,
            new UiStore(),
            factory,
            switcher,
            new SessionGitTracker(),
            new SessionStatusTracker(),
            new FakeChatViewBinder(),
            new FakeLogger<SessionManager>());
        return (manager, factory, sessionStore);
    }

    private static UserMessage TestUserMessage(string sessionId, string content) => new(
        Guid.NewGuid().ToString("N"),
        sessionId,
        DateTimeOffset.UtcNow,
        content,
        "code",
        "test-model");

    [Test]
    public async Task CreateDefault_Success_ReturnsSession()
    {
        var (_, factory, store) = CreateGraph();

        var result = await factory.CreateDefaultAsync().ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        var stored = await store.GetAsync(result.Value.Id).ConfigureAwait(false);
        await Assert.That(stored.IsSuccess).IsTrue();
    }

    [Test]
    public async Task CreateDefault_StoreFailure_ReturnsFailureWithStoreError()
    {
        var (_, factory, _) = CreateGraph(new ControllableStore { FailCreate = true });

        var result = await factory.CreateDefaultAsync().ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error.Contains("boom-create")).IsTrue();
    }

    [Test]
    public async Task CreateNew_Success_AppliesOverrides()
    {
        var (_, factory, _) = CreateGraph();

        var result = await factory.CreateNewAsync("code", "prov-x", "model-x", "/tmp").ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ProviderId).IsEqualTo("prov-x");
        await Assert.That(result.Value.Model).IsEqualTo("model-x");
    }

    [Test]
    public async Task CreateNew_StoreFailure_ReturnsFailureWithStoreError()
    {
        var (_, factory, _) = CreateGraph(new ControllableStore { FailCreate = true });

        var result = await factory.CreateNewAsync().ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error.Contains("boom-create")).IsTrue();
    }

    [Test]
    public async Task CreateBranch_Success_CopiesAndReparentsMessages()
    {
        var (_, factory, store) = CreateGraph();
        var source = (await store.CreateAsync("/tmp", "code", "p", "m").ConfigureAwait(false)).Value;
        await store.AppendMessageAsync(source.Id, TestUserMessage(source.Id, "hello")).ConfigureAwait(false);
        await store.AppendMessageAsync(source.Id, TestUserMessage(source.Id, "world")).ConfigureAwait(false);

        var result = await factory.CreateBranchAsync(source).ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        var branch = result.Value;
        await Assert.That(branch.Id).IsNotEqualTo(source.Id);
        await Assert.That(branch.Title).IsEqualTo(source.Title + " (branch)");
        var copied = (await store.GetMessagesAsync(branch.Id).ConfigureAwait(false)).Value;
        await Assert.That(copied.Count).IsEqualTo(2);
        await Assert.That(copied.All(m => m.SessionId == branch.Id)).IsTrue();
        var sourceIds = (await store.GetMessagesAsync(source.Id).ConfigureAwait(false)).Value.Select(m => m.Id).ToHashSet();
        await Assert.That(copied.All(m => !sourceIds.Contains(m.Id))).IsTrue();
    }

    [Test]
    public async Task CreateBranch_CreateFailure_ReturnsFailure()
    {
        var (_, factory, store) = CreateGraph(new ControllableStore { FailCreate = true });
        var source = Session.Create("/tmp", "code", "p", "m");

        var result = await factory.CreateBranchAsync(source).ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error.Contains("boom-create")).IsTrue();
        _ = store;
    }

    [Test]
    public async Task CreateBranch_HistoryReadFailure_ReturnsFailureInsteadOfSilentEmptyBranch()
    {
        var (_, factory, store) = CreateGraph(new ControllableStore { FailGetMessages = true });
        var source = (await store.CreateAsync("/tmp", "code", "p", "m").ConfigureAwait(false)).Value;

        var result = await factory.CreateBranchAsync(source).ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error.Contains("boom-history")).IsTrue();
    }

    [Test]
    public async Task CreateBranch_HistoryCopyFailure_ReturnsFailure()
    {
        var controllable = new ControllableStore();
        var (_, factory, store) = CreateGraph(controllable);
        var source = (await store.CreateAsync("/tmp", "code", "p", "m").ConfigureAwait(false)).Value;
        await store.AppendMessageAsync(source.Id, TestUserMessage(source.Id, "hello")).ConfigureAwait(false);
        controllable.FailAppend = true;

        var result = await factory.CreateBranchAsync(source).ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error.Contains("boom-append")).IsTrue();
    }

    [Test]
    public async Task Manager_NewSession_Success_SetsActiveAndIdleStatus()
    {
        var (manager, _, _) = CreateGraph();

        var result = await manager.NewSessionAsync(workingDirectory: "/tmp").ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(manager.Active).IsNotNull();
        await Assert.That(manager.Active!.Id).IsEqualTo(result.Value.Id);
        await Assert.That(manager.GetStatus(result.Value.Id)).IsEqualTo(SessionStatus.Idle);
    }

    [Test]
    public async Task Manager_NewSession_FactoryFailure_ReturnsFailureAndKeepsActiveNull()
    {
        var (manager, _, _) = CreateGraph(new ControllableStore { FailCreate = true });

        var result = await manager.NewSessionAsync(workingDirectory: "/tmp").ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error.Contains("boom-create")).IsTrue();
        await Assert.That(manager.Active).IsNull();
    }

    [Test]
    public async Task Manager_BranchActive_NoActiveSession_ReturnsFailure()
    {
        var (manager, _, _) = CreateGraph();

        var result = await manager.BranchActiveAsync().ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(manager.Active).IsNull();
    }

    [Test]
    public async Task Manager_BranchActive_Success_SwitchesActiveToBranch()
    {
        var (manager, _, _) = CreateGraph();
        var created = await manager.NewSessionAsync(workingDirectory: "/tmp").ConfigureAwait(false);
        await Assert.That(created.IsSuccess).IsTrue();

        var result = await manager.BranchActiveAsync().ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(manager.Active).IsNotNull();
        await Assert.That(manager.Active!.Id).IsEqualTo(result.Value.Id);
    }

    [Test]
    public async Task Manager_BranchActive_FactoryFailure_ReturnsFailureAndKeepsActive()
    {
        var controllable = new ControllableStore();
        var (manager, _, _) = CreateGraph(controllable);
        var created = await manager.NewSessionAsync(workingDirectory: "/tmp").ConfigureAwait(false);
        await Assert.That(created.IsSuccess).IsTrue();
        controllable.FailCreate = true;

        var result = await manager.BranchActiveAsync().ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(manager.Active!.Id).IsEqualTo(created.Value.Id);
    }

    [Test]
    public async Task Manager_EnsureDefault_Success_SetsActive()
    {
        var (manager, _, _) = CreateGraph();

        await manager.EnsureDefaultSessionAsync().ConfigureAwait(false);

        await Assert.That(manager.Active).IsNotNull();
    }

    [Test]
    public async Task Manager_EnsureDefault_StoreFailure_LeavesActiveNull()
    {
        var (manager, _, _) = CreateGraph(new ControllableStore { FailCreate = true });

        await manager.EnsureDefaultSessionAsync().ConfigureAwait(false);

        await Assert.That(manager.Active).IsNull();
    }
}
