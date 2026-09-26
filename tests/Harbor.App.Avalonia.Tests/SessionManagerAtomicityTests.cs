using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.TestKit;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Regression tests for issue #89 (SessionManager atomicity/staleness):
///     single-transition hydrate replay, rename updating in-memory contexts,
///     and delete parking (tombstoning) contexts for background events.
/// </summary>
public class SessionManagerAtomicityTests
{
    private sealed class TestServiceProvider(AgentDefinition agentDef) : IServiceProvider
    {
        private readonly FakeAgentRegistry _agents = new(agentDef);

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IAgentRegistry) ? _agents : null;
    }

    private sealed class TestSessionStore : ISessionStore
    {
        private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<AgentMessage>> _messages = new(StringComparer.Ordinal);

        public void Seed(Session session, IEnumerable<AgentMessage>? messages = null)
        {
            _sessions[session.Id] = session;
            _messages[session.Id] = messages is null ? [] : [.. messages];
        }

        public Task<Result<Session>> CreateAsync(string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
        {
            var created = Session.Create(directory, agentName, providerId, modelId);
            Seed(created);
            return Task.FromResult(Result.Success(created));
        }

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(_sessions.TryGetValue(sessionId, out var s)
                ? Result.Success(s)
                : Result.Failure<Session>($"Session '{sessionId}' was not found."));

        public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<Session>>([.. _sessions.Values]));

        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        {
            if (!_messages.TryGetValue(sessionId, out var list))
                return Task.FromResult(Result.Failure($"Session '{sessionId}' was not found."));
            list.Add(message);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(_messages.TryGetValue(sessionId, out var list)
                ? Result.Success<IReadOnlyList<AgentMessage>>([.. list])
                : Result.Failure<IReadOnlyList<AgentMessage>>($"Session '{sessionId}' was not found."));

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
        {
            if (!_sessions.Remove(sessionId))
                return Task.FromResult(Result.Failure($"Session '{sessionId}' was not found."));
            _messages.Remove(sessionId);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
        {
            if (!_sessions.ContainsKey(session.Id))
                return Task.FromResult(Result.Failure($"Session '{session.Id}' was not found."));
            _sessions[session.Id] = session;
            return Task.FromResult(Result.Success());
        }

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(SessionMetadata.Empty));

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(0));
    }

    private sealed class NopBinder : IChatViewBinder
    {
        public void Rebind(UiStore store) { }
    }

    private sealed class NopLogger<T> : ILogger<T>
    {
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        public bool IsEnabled(LogLevel logLevel) => false;
        public IDisposable BeginScope<TState>(TState state) => null!;
    }

    private static SessionManager CreateManager(TestSessionStore store, AgentDefinition agentDef)
    {
        var services = new TestServiceProvider(agentDef);
        var agent = new FakeAgent(AgentState.Idle("none", agentDef));
        var factory = new SessionFactory(services, agent, store, new NopLogger<SessionFactory>());
        var switcher = new SessionSwitcher(agent, store, services, new NopLogger<SessionSwitcher>());
        return new SessionManager(
            services, agent, store, new UiStore(), factory, switcher,
            new SessionGitTracker(), new SessionStatusTracker(),
            new NopBinder(), new NopLogger<SessionManager>());
    }

    private static (TestSessionStore Store, Session Session) SeededStore(AgentDefinition agentDef, int messageCount = 3)
    {
        var store = new TestSessionStore();
        var session = Session.Create("/tmp/issue89", agentDef.Name.Value, agentDef.ProviderId, agentDef.Model);
        var messages = new List<AgentMessage>();
        for (int i = 0; i < messageCount; i++)
            messages.Add(i % 2 == 0
                ? TestMessages.User($"u{i}", session.Id)
                : TestMessages.Assistant($"a{i}", session.Id));
        store.Seed(session, messages);
        return (store, session);
    }

    [Test]
    public async Task OpenSession_Replay_IsSingleStoreTransition()
    {
        var agentDef = TestAgents.AllowAll();
        (var store, var session) = SeededStore(agentDef);
        var manager = CreateManager(store, agentDef);

        await Assert.That(await manager.OpenSessionAsync(session.Id)).IsTrue();
        var ctx = manager.GetContext(session.Id);
        await Assert.That(ctx).IsNotNull();

        // Second open hits the re-hydrate path (StoreWasHydrated == true):
        // the whole replay must ride exactly ONE store transition so a racing
        // background event can only land before or after it, never mid-history.
        int transitions = 0;
        ctx!.Store.Changed += (_, _) => transitions++;

        await Assert.That(await manager.OpenSessionAsync(session.Id)).IsTrue();
        await Assert.That(transitions).IsEqualTo(1);
        await Assert.That(ctx.Store.State.Lines.Length).IsEqualTo(3);
        await Assert.That(ctx.Store.State.Lines[0].Text).IsEqualTo("u0");
        await Assert.That(ctx.Store.State.Lines[1].Text).IsEqualTo("a1");
        await Assert.That(ctx.Store.State.Lines[2].Text).IsEqualTo("u2");
        await Assert.That(ctx.Store.State.Model).IsEqualTo(session.Model);
        await Assert.That(ctx.Store.State.Provider).IsEqualTo(session.ProviderId);
        await Assert.That(ctx.Store.State.AgentName).IsEqualTo(session.Agent);
    }

    [Test]
    public async Task Switcher_FirstOpen_IsSingleStoreTransition()
    {
        var agentDef = TestAgents.AllowAll();
        (var store, var session) = SeededStore(agentDef, messageCount: 2);
        var services = new TestServiceProvider(agentDef);
        var agent = new FakeAgent(AgentState.Idle("none", agentDef));
        var switcher = new SessionSwitcher(agent, store, services, new NopLogger<SessionSwitcher>());
        var target = new UiStore();

        int transitions = 0;
        target.Changed += (_, _) => transitions++;

        await Assert.That(await switcher.OpenAsync(session, target)).IsTrue();
        await Assert.That(transitions).IsEqualTo(1);
        await Assert.That(target.State.Lines.Length).IsEqualTo(2);
    }

    [Test]
    public async Task RenameSession_UpdatesInMemoryContexts()
    {
        var agentDef = TestAgents.AllowAll();
        (var store, var session) = SeededStore(agentDef, messageCount: 0);
        var manager = CreateManager(store, agentDef);

        await Assert.That(await manager.OpenSessionAsync(session.Id)).IsTrue();
        await Assert.That(await manager.RenameSessionAsync(session.Id, "New Title")).IsTrue();

        // Both the per-session context and the active record serve the new title…
        await Assert.That(manager.GetContext(session.Id)!.Session.Title).IsEqualTo("New Title");
        await Assert.That(manager.Active!.Title).IsEqualTo("New Title");
        // …and it is the same value that was persisted to the store.
        var reloaded = await store.GetAsync(session.Id);
        await Assert.That(reloaded.IsSuccess).IsTrue();
        await Assert.That(reloaded.Value.Title).IsEqualTo("New Title");
    }

    [Test]
    public async Task DeleteSession_ParksContextForBackgroundEvents()
    {
        var agentDef = TestAgents.AllowAll();
        var store = new TestSessionStore();
        var sessionA = Session.Create("/tmp/issue89a", agentDef.Name.Value, agentDef.ProviderId, agentDef.Model);
        var sessionB = Session.Create("/tmp/issue89b", agentDef.Name.Value, agentDef.ProviderId, agentDef.Model);
        store.Seed(sessionA);
        store.Seed(sessionB);
        var manager = CreateManager(store, agentDef);

        await Assert.That(await manager.OpenSessionAsync(sessionA.Id)).IsTrue();
        var ctxA = manager.GetContext(sessionA.Id);
        await Assert.That(await manager.OpenSessionAsync(sessionB.Id)).IsTrue();

        await Assert.That(await manager.DeleteSessionAsync(sessionA.Id)).IsTrue();

        // The deleted session keeps a parked context: background events still
        // have a home instead of dropping silently…
        var parked = manager.GetContext(sessionA.Id);
        await Assert.That(parked).IsNotNull();
        await Assert.That(ReferenceEquals(parked, ctxA)).IsTrue();
        await Assert.That(manager.Active!.Id).IsEqualTo(sessionB.Id);

        // …and a late event for the deleted session does not leak into the
        // newly-active session's transcript.
        parked!.Store.Dispatch(new UiMsg.AppendLine(ChatRole.User, "late-event"));
        await Assert.That(parked.Store.State.Lines.Any(l => l.Text == "late-event")).IsTrue();
        var ctxB = manager.GetContext(sessionB.Id);
        await Assert.That(ctxB!.Store.State.Lines.Any(l => l.Text == "late-event")).IsFalse();
    }

    [Test]
    public async Task DeleteActiveSession_SwitchesAndParksDeleted()
    {
        var agentDef = TestAgents.AllowAll();
        var store = new TestSessionStore();
        var sessionA = Session.Create("/tmp/issue89a", agentDef.Name.Value, agentDef.ProviderId, agentDef.Model);
        var sessionB = Session.Create("/tmp/issue89b", agentDef.Name.Value, agentDef.ProviderId, agentDef.Model);
        store.Seed(sessionA);
        store.Seed(sessionB);
        var manager = CreateManager(store, agentDef);

        await Assert.That(await manager.OpenSessionAsync(sessionA.Id)).IsTrue();
        await Assert.That(await manager.OpenSessionAsync(sessionB.Id)).IsTrue();

        await Assert.That(await manager.DeleteSessionAsync(sessionB.Id)).IsTrue();

        // Switch completed to the remaining session…
        await Assert.That(manager.Active!.Id).IsEqualTo(sessionA.Id);
        // …while the deleted session's context is parked, not dropped.
        await Assert.That(manager.GetContext(sessionB.Id)).IsNotNull();
    }

    [Test]
    public async Task ConcurrentGetContext_DuringDelete_NeverObservesGap()
    {
        // Issue #81: GetContext runs on the EventBus publisher (tool) thread
        // while DeleteSessionAsync runs on the UI thread. Park-before-remove
        // means readers must never observe the gap where the session is
        // neither live nor parked. Deterministic: bounded spin on task
        // completion, no sleeps, no timeouts.
        var agentDef = TestAgents.AllowAll();
        var store = new TestSessionStore();
        var sessionA = Session.Create("/tmp/issue81a", agentDef.Name.Value, agentDef.ProviderId, agentDef.Model);
        var sessionB = Session.Create("/tmp/issue81b", agentDef.Name.Value, agentDef.ProviderId, agentDef.Model);
        store.Seed(sessionA);
        store.Seed(sessionB);
        var manager = CreateManager(store, agentDef);

        await Assert.That(await manager.OpenSessionAsync(sessionA.Id)).IsTrue();
        var ctxA = manager.GetContext(sessionA.Id);
        await Assert.That(await manager.OpenSessionAsync(sessionB.Id)).IsTrue();

        var delete = manager.DeleteSessionAsync(sessionA.Id);
        long nulls = 0;
        var readers = new Task[8];
        for (int i = 0; i < readers.Length; i++)
        {
            readers[i] = Task.Run(() =>
            {
                for (int n = 0; n < 10_000 && !delete.IsCompleted; n++)
                {
                    if (manager.GetContext(sessionA.Id) is null)
                        Interlocked.Increment(ref nulls);
                }
            });
        }

        await Assert.That(await delete).IsTrue();
        await Task.WhenAll(readers);

        await Assert.That(nulls).IsEqualTo(0);
        await Assert.That(ReferenceEquals(manager.GetContext(sessionA.Id), ctxA)).IsTrue();
    }

    [Test]
    public async Task ConcurrentOpen_SameSession_CompletesCoherently()
    {
        // Issue #81: concurrent opens of the same session (double-click /
        // restore race) share one context via GetOrAdd instead of tearing
        // the map or forking two UiStores.
        var agentDef = TestAgents.AllowAll();
        (var store, var session) = SeededStore(agentDef);
        var manager = CreateManager(store, agentDef);

        var opens = new Task<bool>[8];
        for (int i = 0; i < opens.Length; i++)
            opens[i] = manager.OpenSessionAsync(session.Id);

        var results = await Task.WhenAll(opens);

        await Assert.That(results.All(r => r)).IsTrue();
        await Assert.That(manager.GetContext(session.Id)).IsNotNull();
        await Assert.That(manager.Active!.Id).IsEqualTo(session.Id);
        // Both opens hydrate the SAME store with the same payload
        // (replace semantics): no duplicated replay, no torn transcript.
        await Assert.That(manager.GetContext(session.Id)!.Store.State.Lines.Length).IsEqualTo(3);
    }
}
