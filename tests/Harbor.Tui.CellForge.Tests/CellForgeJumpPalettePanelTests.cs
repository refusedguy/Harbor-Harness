using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Overlays;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Slice-2 contract tests for <see cref="CellForgeJumpPalettePanel" />:
///     identity (<c>jump</c> id), <c>Build</c> rendering
///     <see cref="WorktreeJumpEntry.RowText" /> rows with a selected marker, and
///     <c>OnKey</c> routing (Up/Down move, Enter switches via the existing
///     <c>ISessionManager.OpenSessionAsync</c>, Esc closes). The model is
///     injected (fake), so no git and no real sessions are involved — except
///     the seeding test, which pins the porcelain reader.
/// </summary>
public class CellForgeJumpPalettePanelTests
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

        public List<string> Opened { get; } = new();

        public void AddContext(Session session) => _contexts[session.Id] = new SessionContext(session);

        public Session? Active => null;

        public SessionContext? GetContext(string sessionId) =>
            _contexts.TryGetValue(sessionId, out var ctx) ? ctx : null;

        public SessionStatus GetStatus(string sessionId) => SessionStatus.Idle;

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

        public Task<bool> OpenSessionAsync(string sessionId)
        {
            Opened.Add(sessionId);
            return Task.FromResult(true);
        }

        public Task<Result<Session>> BranchActiveAsync() =>
            Task.FromResult(Result.Failure<Session>("Not supported in tests."));

        public Task<bool> DeleteSessionAsync(string sessionId) => Task.FromResult(false);

        public Task<bool> RenameSessionAsync(string sessionId, string newTitle) => Task.FromResult(false);

        public event Action<string, SessionStatus>? StatusChanged;

        public event Action<string, int>? MessageCountChanged;
    }

    private static readonly WorktreeJumpEntry[] Entries =
    [
        new("s1", "Jump Palette", "/repo/.worktrees/jump-palette", "feat/jump-palette", "idle"),
        new("s2", "Word Diff", "/repo/.worktrees/worddiff", "feat/word-diff", "working ●"),
    ];

    private static CellForgeJumpPalettePanel WithModel(out WorktreeJumpPaletteModel model)
    {
        model = new WorktreeJumpPaletteModel();
        model.Show(Entries);
        var panel = new CellForgeJumpPalettePanel(model)
        {
            WorktreePorcelainReader = () => string.Empty,
        };
        return panel;
    }

    private static PanelContext Ctx(UiState state, IServiceProvider? services = null) =>
        new(state, 120, 40, services);

    private static UiState StateWithSessions(params SessionInfo[] sessions) =>
        new UiState { Sessions = ImmutableArray.Create(sessions) };

    private static SessionInfo Info(string id, string title) => new(
        SessionId.Create(id),
        title,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        "active");

    private static UiStore VisibleJumpStore()
    {
        var store = new UiStore();
        _ = store.Dispatch(new UiMsg.SeedPanels(
            ImmutableArray.Create(OverlayIds.JumpPalette),
            ImmutableDictionary<string, TuiPanelState>.Empty.Add(OverlayIds.JumpPalette, TuiPanelState.Visible),
            ImmutableDictionary<string, int>.Empty.Add(OverlayIds.JumpPalette, 48)));
        return store;
    }

    private static string Joined(object? widget) => widget switch
    {
        null => string.Empty,
        IReadOnlyList<string> rows => string.Join("\n", rows),
        _ => widget.ToString() ?? string.Empty,
    };

    [Test]
    public async Task Contract_Id_Title_Placement_Size()
    {
        var panel = new CellForgeJumpPalettePanel
        {
            WorktreePorcelainReader = () => string.Empty,
        };

        await Assert.That(panel.Id).IsEqualTo("jump");
        await Assert.That(panel.Id).IsEqualTo(OverlayIds.JumpPalette);
        await Assert.That(panel.Title).IsEqualTo("Jump");
        await Assert.That(panel.DefaultPlacement).IsEqualTo(TuiPanelPlacement.Right);
        await Assert.That(panel.DefaultSize).IsEqualTo(48);
    }

    [Test]
    public async Task Build_Renders_RowText_WithSelectedMarker()
    {
        var panel = WithModel(out _);

        string text = Joined(panel.Build(Ctx(new UiState())));

        await Assert.That(text).Contains("▸ " + Entries[0].RowText);
        await Assert.That(text).Contains("  " + Entries[1].RowText);
    }

    [Test]
    public async Task OnKey_DownUp_MovesSelection()
    {
        var panel = WithModel(out var model);

        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Down), Ctx(new UiState()))).IsTrue();
        await Assert.That(model.SelectedIndex).IsEqualTo(1);

        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Up), Ctx(new UiState()))).IsTrue();
        await Assert.That(model.SelectedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task OnKey_Enter_SwitchesSession_AndHides()
    {
        var panel = WithModel(out var model);
        var manager = new FakeSessionManager();
        var store = VisibleJumpStore();
        var services = new FakeServices().Add<UiStore>(store).Add<ISessionManager>(manager);

        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Enter), Ctx(new UiState(), services))).IsTrue();

        await Assert.That(manager.Opened).Contains("s1");
        await Assert.That(model.Visible).IsFalse();
        await Assert.That(store.State.PanelStates[OverlayIds.JumpPalette]).IsEqualTo(TuiPanelState.Hidden);
    }

    [Test]
    public async Task OnKey_Escape_Hides_WithoutSwitch()
    {
        var panel = WithModel(out var model);
        var manager = new FakeSessionManager();
        var store = VisibleJumpStore();
        var services = new FakeServices().Add<UiStore>(store).Add<ISessionManager>(manager);

        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Escape), Ctx(new UiState(), services))).IsTrue();

        await Assert.That(manager.Opened).IsEmpty();
        await Assert.That(model.Visible).IsFalse();
        await Assert.That(store.State.PanelStates[OverlayIds.JumpPalette]).IsEqualTo(TuiPanelState.Hidden);
    }

    [Test]
    public async Task OnKey_Enter_WorktreeOnlyRow_SkipsSwitch_StillHides()
    {
        var model = new WorktreeJumpPaletteModel();
        model.Show([new WorktreeJumpEntry(string.Empty, "detached-wt", "/repo/.worktrees/detached-wt", null, "no-session")]);
        var panel = new CellForgeJumpPalettePanel(model)
        {
            WorktreePorcelainReader = () => string.Empty,
        };
        var manager = new FakeSessionManager();
        var store = VisibleJumpStore();
        var services = new FakeServices().Add<UiStore>(store).Add<ISessionManager>(manager);

        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Enter), Ctx(new UiState(), services))).IsTrue();

        await Assert.That(manager.Opened).IsEmpty();
        await Assert.That(store.State.PanelStates[OverlayIds.JumpPalette]).IsEqualTo(TuiPanelState.Hidden);
    }

    [Test]
    public async Task OnKey_UnrelatedKey_NotConsumed()
    {
        var panel = WithModel(out _);

        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Left), Ctx(new UiState()))).IsFalse();
        await Assert.That(panel.OnKey(UiKey.ForChar('x'), Ctx(new UiState()))).IsFalse();
    }

    [Test]
    public async Task Build_Seeds_FromSessions_AndWorktrees_ReadOnly()
    {
        var session = Session.Create("/repo/.worktrees/jump-palette", "code", "kilocode", "kilo-auto", "Jump Palette")
            with
        {
            Id = "s1",
            GitBranch = "feat/jump-palette",
        };
        var manager = new FakeSessionManager();
        manager.AddContext(session);
        var state = StateWithSessions(Info("s1", "Jump Palette"));
        var services = new FakeServices().Add<ISessionManager>(manager);
        var panel = new CellForgeJumpPalettePanel
        {
            WorktreePorcelainReader = () => "worktree /repo/.worktrees/jump-palette\nbranch refs/heads/feat/jump-palette\n",
        };

        string text = Joined(panel.Build(Ctx(state, services)));

        await Assert.That(text).Contains("Jump Palette");
        await Assert.That(text).Contains("feat/jump-palette");
        await Assert.That(text).Contains("/repo/.worktrees/jump-palette");
        await Assert.That(manager.Opened).IsEmpty();
    }
}
