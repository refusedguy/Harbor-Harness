using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Sessions;
using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Reducers;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Contract tests for <see cref="CellForgeSubagentsPanel" /> (opencode
///     Subagents-overlay equivalent): identity (<c>subagents</c> id), pure
///     <see cref="SubagentsModel" /> row selection (parent→children tree +
///     sub-agent kind), status/age formatting, read-only transcript view
///     (Enter opens, Esc returns, no composer/input anywhere), and the
///     <see cref="SessionsReducer" /> sub-agent flag propagation that hides
///     these sessions from the jump palette.
/// </summary>
public class CellForgeSubagentsPanelTests
{
    private sealed class FakeServices : IServiceProvider
    {
        private readonly Dictionary<Type, object> _map = new();

        public FakeServices Add<T>(T instance)
            where T : class
        {
            _map[typeof(T)] = instance;
            return this;
        }

        public object? GetService(Type serviceType) =>
            _map.TryGetValue(serviceType, out var value) ? value : null;
    }

    private sealed class FakeSessionManager : ISessionManager
    {
        private readonly Dictionary<string, SessionContext> _contexts = new(StringComparer.Ordinal);

        public void AddContext(Session session, SessionStatus status = SessionStatus.Idle)
        {
            var ctx = new SessionContext(session) { Status = status };
            _contexts[session.Id] = ctx;
        }

        public Session? Active => ActiveContext?.Session;

        public SessionContext? ActiveContext { get; set; }

        public SessionContext? GetContext(string sessionId) =>
            _contexts.TryGetValue(sessionId, out var ctx) ? ctx : null;

        public SessionStatus GetStatus(string sessionId) =>
            _contexts.TryGetValue(sessionId, out var ctx) ? ctx.Status : SessionStatus.Idle;

        public void SetStatus(string sessionId, SessionStatus status)
        {
        }

        public void NotifyMessageCount(string sessionId, int count)
        {
        }

        public GitSessionInfo GetGitInfo(string sessionId) => GitSessionInfo.Empty;

        public void RefreshGitInfo(string sessionId, string directory)
        {
        }

        public Task EnsureDefaultSessionAsync() => Task.CompletedTask;

        public Task RebindFromCommonConfigAsync() => Task.CompletedTask;

        public Task<Result<Session>> NewSessionAsync(
            string? agentName = null,
            string? providerId = null,
            string? modelId = null,
            string? workingDirectory = null) =>
            Task.FromResult(Result.Failure<Session>("Not supported in tests."));

        public Task<bool> OpenSessionAsync(string sessionId) => Task.FromResult(true);

        public Task<Result<Session>> BranchActiveAsync() =>
            Task.FromResult(Result.Failure<Session>("Not supported in tests."));

        public Task<bool> DeleteSessionAsync(string sessionId) => Task.FromResult(false);

        public Task<bool> RenameSessionAsync(string sessionId, string newTitle) => Task.FromResult(false);

        public event Action<string, SessionStatus>? StatusChanged;

        public event Action<string, int>? MessageCountChanged;
    }

    /// <summary>Multi-session store fake with write counters (read-only assertions).</summary>
    private sealed class RecordingStore : ISessionStore
    {
        private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<AgentMessage>> _messages;
        private int _appends;
        private int _updates;
        private int _messageUpdates;
        private int _deletes;

        public int Appends => Volatile.Read(ref _appends);
        public int Updates => Volatile.Read(ref _updates);
        public int MessageUpdates => Volatile.Read(ref _messageUpdates);
        public int Deletes => Volatile.Read(ref _deletes);

        public RecordingStore(
            IEnumerable<Session> sessions,
            Dictionary<string, List<AgentMessage>>? messages = null)
        {
            foreach (var session in sessions)
                _sessions[session.Id] = session;
            _messages = messages ?? new Dictionary<string, List<AgentMessage>>(StringComparer.Ordinal);
        }

        public Task<Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId, CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<Session>("Not supported in tests."));

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default) =>
            _sessions.TryGetValue(sessionId, out var s)
                ? Task.FromResult(Result.Success(s))
                : Task.FromResult(Result.Failure<Session>($"Session '{sessionId}' not found."));

        public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<Session>>(new List<Session>(_sessions.Values)));

        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _appends);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _messageUpdates);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default) =>
            _messages.TryGetValue(sessionId, out var list)
                ? Task.FromResult(Result.Success<IReadOnlyList<AgentMessage>>(new List<AgentMessage>(list)))
                : Task.FromResult(Result.Failure<IReadOnlyList<AgentMessage>>($"Session '{sessionId}' not found."));

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _deletes);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _updates);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(SessionMetadata.Empty));

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(0));
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static Session Sess(
        string id,
        string title,
        string? parent,
        SessionKind kind,
        SessionStatus status,
        DateTimeOffset updated) =>
        Session.Create("/repo", "explore", "test", "m", title) with
        {
            Id = id,
            ParentSessionId = parent,
            Kind = kind,
            Status = status,
            CreatedAt = updated,
            UpdatedAt = updated,
        };

    private static UserMessage User(string sessionId, string text) => new(
        Guid.NewGuid().ToString("N"), sessionId, Now, text, "explore", "m");

    private static AssistantMessage Assistant(string sessionId, params ContentPart[] parts) => new(
        Guid.NewGuid().ToString("N"), sessionId, Now, parts,
        StopReason.Stop, new Usage(0, 0), "m");

    private static PanelContext Ctx(UiState state, IServiceProvider? services = null) =>
        new(state, 120, 40, services);

    private static UiState StateWithActive(string activeSessionId) =>
        new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                ActiveSessionId = SessionId.Create(activeSessionId)
            }
        };

    private static string Joined(object? widget) => widget switch
    {
        null => string.Empty,
        IReadOnlyList<string> rows => string.Join("\n", rows),
        _ => widget.ToString() ?? string.Empty,
    };

    private static UiStore SeededSubagentsStore()
    {
        var store = new UiStore();
        _ = store.Dispatch(new AppMsg.SeedPanels(
            ImmutableArray.Create(OverlayIds.Subagents),
            ImmutableDictionary<string, TuiPanelState>.Empty.Add(OverlayIds.Subagents, TuiPanelState.Visible),
            ImmutableDictionary<string, int>.Empty.Add(OverlayIds.Subagents, 48)));
        return store;
    }

    [Test]
    public async Task Contract_Id_Title_Placement_Size()
    {
        var panel = new CellForgeSubagentsPanel();

        await Assert.That(panel.Id).IsEqualTo("subagents");
        await Assert.That(panel.Id).IsEqualTo(OverlayIds.Subagents);
        await Assert.That(panel.Title).IsEqualTo("Subagents");
        await Assert.That(panel.DefaultPlacement).IsEqualTo(TuiPanelPlacement.Right);
        await Assert.That(panel.DefaultSize).IsEqualTo(48);
    }

    [Test]
    public async Task BuildRows_ParentChildrenTree_SelectsChildrenAndAllSubagents()
    {
        var all = new List<Session>
        {
            Sess("parent", "Parent Work", null, SessionKind.User, SessionStatus.Idle, Now),
            Sess("fork", "Parent Work (fork)", "parent", SessionKind.User, SessionStatus.Idle, Now),
            Sess("sub1", "task(explore): dig", "parent", SessionKind.Subagent, SessionStatus.Working, Now),
            Sess("other", "Other Work", null, SessionKind.User, SessionStatus.Idle, Now),
            Sess("sub2", "task(plan): think", "other", SessionKind.Subagent, SessionStatus.Done, Now),
        };

        var rows = SubagentsModel.BuildRows(all, "parent", Now);

        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(rows[0].SessionId).IsEqualTo("fork");
        await Assert.That(rows[1].SessionId).IsEqualTo("sub1");
        await Assert.That(rows[2].SessionId).IsEqualTo("sub2");
        await Assert.That(rows[1].Status).IsEqualTo("running");
        await Assert.That(rows[2].Status).IsEqualTo("done");
    }

    [Test]
    public async Task BuildRows_LegacySubagentShape_ClassifiedWithoutKindStamp()
    {
        // Records persisted before Session.Kind existed: parent linkage plus
        // the task(…) title marker still classify as sub-agent.
        var legacy = Sess("legacy", "task(explore): old run", "parent", SessionKind.User, SessionStatus.Done, Now);

        await Assert.That(legacy.IsSubagent()).IsTrue();
        await Assert.That(Sess("f", "Parent (fork)", "parent", SessionKind.User, SessionStatus.Idle, Now).IsSubagent()).IsFalse();

        var rows = SubagentsModel.BuildRows(new List<Session> { legacy }, "unrelated", Now);

        await Assert.That(rows.Count).IsEqualTo(1);
    }

    [Test]
    public async Task FormatAge_And_StatusText_CoverVocabulary()
    {
        await Assert.That(SubagentsModel.FormatAge(TimeSpan.FromSeconds(5))).IsEqualTo("5s");
        await Assert.That(SubagentsModel.FormatAge(TimeSpan.FromSeconds(90))).IsEqualTo("1m");
        await Assert.That(SubagentsModel.FormatAge(TimeSpan.FromHours(3))).IsEqualTo("3h");
        await Assert.That(SubagentsModel.FormatAge(TimeSpan.FromDays(2))).IsEqualTo("2d");
        await Assert.That(SubagentsModel.FormatAge(TimeSpan.FromSeconds(-1))).IsEqualTo("0s");

        await Assert.That(SubagentsModel.StatusText(SessionStatus.Working)).IsEqualTo("running");
        await Assert.That(SubagentsModel.StatusText(SessionStatus.Done)).IsEqualTo("done");
        await Assert.That(SubagentsModel.StatusText(SessionStatus.Error)).IsEqualTo("error");
        await Assert.That(SubagentsModel.StatusText(SessionStatus.Aborted)).IsEqualTo("aborted");
        await Assert.That(SubagentsModel.StatusText(SessionStatus.Idle)).IsEqualTo("idle");
    }

    [Test]
    public async Task TranscriptRows_RendersRoles_WithReadOnlyHeader()
    {
        var messages = new List<AgentMessage>
        {
            User("s1", "do the thing"),
            Assistant("s1", new ToolCallPart("tc1", "read", default)),
            new ToolResultMessage(Guid.NewGuid().ToString("N"), "s1", Now,
                [new ToolResultEntry("tc1", "read", "file contents", false)]),
            Assistant("s1", new TextPart("did it")),
        };

        var rows = SubagentsModel.TranscriptRows("task(explore): dig", messages, 120);
        string text = string.Join("\n", rows);

        await Assert.That(text).Contains("(read-only)");
        await Assert.That(text).Contains("you: do the thing");
        await Assert.That(text).Contains("agent: [tool_call: read]");
        await Assert.That(text).Contains("tool: read: ok file contents");
        await Assert.That(text).Contains("agent: did it");
    }

    [Test]
    public async Task Build_ListsRows_WithLiveStatusOverride()
    {
        var stored = Sess("sub1", "task(explore): dig", "parent", SessionKind.Subagent, SessionStatus.Done, Now);
        var store = new RecordingStore(new[] { stored });
        var manager = new FakeSessionManager();
        manager.AddContext(stored, SessionStatus.Working);
        var services = new FakeServices().Add<ISessionStore>(store).Add<ISessionManager>(manager);
        var panel = new CellForgeSubagentsPanel();

        string text = Joined(panel.Build(Ctx(StateWithActive("parent"), services)));

        await Assert.That(text).Contains("Subagents (1)");
        await Assert.That(text).Contains("▸ task(explore): dig  explore  working");
        await Assert.That(store.Appends).IsEqualTo(0);
        await Assert.That(store.Updates).IsEqualTo(0);
    }

    [Test]
    public async Task Enter_OpensTranscript_Esc_ReturnsToList()
    {
        var session = Sess("sub1", "task(explore): dig", "parent", SessionKind.Subagent, SessionStatus.Done, Now);
        var messages = new Dictionary<string, List<AgentMessage>>(StringComparer.Ordinal)
        {
            ["sub1"] = [User("sub1", "do the thing"), Assistant("sub1", new TextPart("did it"))],
        };
        var store = new RecordingStore(new[] { session }, messages);
        var services = new FakeServices().Add<ISessionStore>(store);
        var panel = new CellForgeSubagentsPanel();
        var ctx = Ctx(StateWithActive("parent"), services);

        string list = Joined(panel.Build(ctx));
        await Assert.That(list).Contains("task(explore): dig");

        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Enter), ctx)).IsTrue();
        string transcript = Joined(panel.Build(ctx));
        await Assert.That(transcript).Contains("(read-only)");
        await Assert.That(transcript).Contains("you: do the thing");
        await Assert.That(transcript).Contains("agent: did it");
        await Assert.That(transcript).DoesNotContain("▸ task(explore): dig");

        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Escape), ctx)).IsTrue();
        string back = Joined(panel.Build(ctx));
        await Assert.That(back).Contains("▸ task(explore): dig");
    }

    [Test]
    public async Task ReadOnly_DrivingAllKeys_WritesNothing_AndDispatchesNoInput()
    {
        var session = Sess("sub1", "task(explore): dig", "parent", SessionKind.Subagent, SessionStatus.Done, Now);
        var messages = new Dictionary<string, List<AgentMessage>>(StringComparer.Ordinal)
        {
            ["sub1"] = [User("sub1", "do the thing"), Assistant("sub1", new TextPart("did it"))],
        };
        var store = new RecordingStore(new[] { session }, messages);
        var uiStore = SeededSubagentsStore();
        var services = new FakeServices().Add<ISessionStore>(store).Add<UiStore>(uiStore);
        var panel = new CellForgeSubagentsPanel();
        var ctx = Ctx(StateWithActive("parent"), services);
        var inputBefore = uiStore.State.Ui.Input;
        long revisionBefore = uiStore.State.Revision;

        // List mode: navigation, transcript entry, refresh.
        _ = panel.Build(ctx);
        _ = panel.OnKey(new UiKey(UiKeyCode.Down), ctx);
        _ = panel.OnKey(new UiKey(UiKeyCode.Up), ctx);
        _ = panel.OnKey(UiKey.ForChar('r'), ctx);
        _ = panel.OnKey(new UiKey(UiKeyCode.Enter), ctx);
        _ = panel.Build(ctx);
        // Transcript mode: scroll, reload, Enter (must not submit), back.
        _ = panel.OnKey(UiKey.ForChar('j'), ctx);
        _ = panel.OnKey(UiKey.ForChar('k'), ctx);
        _ = panel.OnKey(new UiKey(UiKeyCode.Down), ctx);
        _ = panel.OnKey(new UiKey(UiKeyCode.Up), ctx);
        _ = panel.OnKey(UiKey.ForChar('r'), ctx);
        _ = panel.OnKey(new UiKey(UiKeyCode.Enter), ctx);
        _ = panel.OnKey(new UiKey(UiKeyCode.Escape), ctx);
        _ = panel.Build(ctx);

        await Assert.That(store.Appends).IsEqualTo(0);
        await Assert.That(store.Updates).IsEqualTo(0);
        await Assert.That(store.MessageUpdates).IsEqualTo(0);
        await Assert.That(store.Deletes).IsEqualTo(0);
        await Assert.That(uiStore.State.Ui.Input).IsEqualTo(inputBefore);
        await Assert.That(uiStore.State.Revision).IsEqualTo(revisionBefore);
    }

    [Test]
    public async Task Escape_InListMode_HidesPanel_WithoutSwitchingSession()
    {
        var session = Sess("sub1", "task(explore): dig", "parent", SessionKind.Subagent, SessionStatus.Done, Now);
        var store = new RecordingStore(new[] { session });
        var manager = new FakeSessionManager();
        var uiStore = SeededSubagentsStore();
        var services = new FakeServices()
            .Add<ISessionStore>(store)
            .Add<ISessionManager>(manager)
            .Add<UiStore>(uiStore);
        var panel = new CellForgeSubagentsPanel();
        // Explicit store: the host wires ctx.Store for state transitions (#63).
        var ctx = new PanelContext(StateWithActive("parent"), 120, 40, services, uiStore);

        _ = panel.Build(ctx);
        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Escape), ctx)).IsTrue();

        await Assert.That(uiStore.State.Ui.PanelStates[OverlayIds.Subagents]).IsEqualTo(TuiPanelState.Hidden);
        await Assert.That(store.Appends).IsEqualTo(0);
        await Assert.That(store.Updates).IsEqualTo(0);
    }

    [Test]
    public async Task Reducer_AgentStartEvent_PropagatesSubagentFlag()
    {
        var flagged = SessionsReducer.Reduce(
            new AgentStartEvent("s1", Array.Empty<AgentMessage>(), null, SessionKind.Subagent),
            new SessionsViewState());
        var plain = SessionsReducer.Reduce(
            new AgentStartEvent("s2", Array.Empty<AgentMessage>()),
            new SessionsViewState());

        await Assert.That(flagged.Sessions.Count).IsEqualTo(1);
        await Assert.That(flagged.Sessions[0].IsSubagent).IsTrue();
        await Assert.That(plain.Sessions[0].IsSubagent).IsFalse();
    }
}
