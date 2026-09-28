using System.Collections.Immutable;
using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Tui.CellForge.Panels;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
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
///     Contract tests for <see cref="CellForgeJumpPalettePanel" />: identity
///     (<c>jump</c> id), <c>Build</c> rendering
///     <see cref="WorktreeJumpEntry.RowText" /> rows with a selected marker, and
///     <c>OnKey</c> routing. Issue #381 adds the typed fuzzy query
///     (printable → <c>SetQuery</c>, <c>Backspace</c> trims, <c>r</c> re-seeds)
///     and the centred-modal presentation
///     (<see cref="CellForgeJumpPaletteOverlayLayer" /> +
///     <see cref="ChatScreenPanelDock" /> releasing the dock slot). The model is
///     injected (fake), so no git and no real sessions are involved — except
///     the seeding tests, which pin the porcelain reader.
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

        public Session? Active => ActiveContext?.Session;

        public SessionContext? ActiveContext { get; set; }

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

    /// <summary>Two worktrees, no sessions — the reopen/reseed fixture.</summary>
    private static string Porcelain() =>
        "worktree /repo/.worktrees/jump-palette\nbranch refs/heads/feat/jump-palette\n"
        + "worktree /repo/.worktrees/worddiff\nbranch refs/heads/feat/word-diff\n";

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
        new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Sessions = ImmutableArray.Create(sessions)
            }
        };

    private static SessionInfo Info(string id, string title) => new(
        SessionId.Create(id),
        title,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        "active");

    private static UiStore VisibleJumpStore() => JumpStore(TuiPanelState.Visible);

    private static UiStore HiddenJumpStore() => JumpStore(TuiPanelState.Hidden);

    private static UiStore JumpStore(TuiPanelState state)
    {
        var store = new UiStore();
        _ = store.Dispatch(new AppMsg.SeedPanels(
            ImmutableArray.Create(OverlayIds.JumpPalette),
            ImmutableDictionary<string, TuiPanelState>.Empty.Add(OverlayIds.JumpPalette, state),
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

        // #381: the palette is a centred modal overlay, not a Right dock leaf —
        // the Right dock slot it used to hold is released.
        await Assert.That(panel.DefaultPlacement).IsEqualTo(TuiPanelPlacement.Center);
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
        await Assert.That(store.State.Ui.PanelStates[OverlayIds.JumpPalette]).IsEqualTo(TuiPanelState.Hidden);
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
        await Assert.That(store.State.Ui.PanelStates[OverlayIds.JumpPalette]).IsEqualTo(TuiPanelState.Hidden);
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
        await Assert.That(store.State.Ui.PanelStates[OverlayIds.JumpPalette]).IsEqualTo(TuiPanelState.Hidden);
    }

    [Test]
    public async Task OnKey_UnrelatedKey_NotConsumed()
    {
        var panel = WithModel(out _);

        // Navigation keys the palette does not model stay unconsumed so the host
        // keymap still sees them. (#381: printable chars are now consumed — they
        // feed the fuzzy query; see OnKey_TypedChar_FeedsQuery_Filters.)
        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Left), Ctx(new UiState()))).IsFalse();
        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Tab), Ctx(new UiState()))).IsFalse();
    }

    [Test]
    public async Task OnKey_TypedChar_FeedsQuery_Filters()
    {
        var panel = WithModel(out var model);

        foreach (char c in "diff")
        {
            await Assert.That(panel.OnKey(UiKey.ForChar(c), Ctx(new UiState()))).IsTrue();
        }

        // SetQuery is finally reached: the query is the typed text and the rows
        // are filtered down to the single matching worktree session.
        await Assert.That(model.Query).IsEqualTo("diff");
        await Assert.That(model.Results.Count).IsEqualTo(1);
        await Assert.That(model.Results[0].SessionId).IsEqualTo("s2");

        // The query is visible in the rendered rows.
        await Assert.That(Joined(panel.Build(Ctx(new UiState())))).Contains("Jump: diff");
    }

    [Test]
    public async Task OnKey_Backspace_TrimsQuery()
    {
        var panel = WithModel(out var model);

        foreach (char c in "dif")
        {
            _ = panel.OnKey(UiKey.ForChar(c), Ctx(new UiState()));
        }

        await Assert.That(model.Query).IsEqualTo("dif");
        await Assert.That(model.Results.Count).IsEqualTo(1);

        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Backspace), Ctx(new UiState()))).IsTrue();
        await Assert.That(model.Query).IsEqualTo("di");
        await Assert.That(model.Results.Count).IsEqualTo(1);

        // Backspacing the query away restores every row.
        _ = panel.OnKey(new UiKey(UiKeyCode.Backspace), Ctx(new UiState()));
        _ = panel.OnKey(new UiKey(UiKeyCode.Backspace), Ctx(new UiState()));
        await Assert.That(model.Query).IsEmpty();
        await Assert.That(model.Results.Count).IsEqualTo(2);

        // Backspace on an empty query is a no-op, not an exception.
        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Backspace), Ctx(new UiState()))).IsTrue();
        await Assert.That(model.Query).IsEmpty();
    }

    [Test]
    public async Task OnKey_TypedChar_NeverLeaksIntoComposer()
    {
        // #381 "no typing leak": every query key is CONSUMED, so a host that
        // routes keys to the palette first never lets it reach the composer /
        // chat transcript. The store's input box stays untouched throughout.
        var panel = WithModel(out var model);
        var store = VisibleJumpStore();
        var services = new FakeServices().Add<UiStore>(store);

        foreach (char c in "dif")
        {
            await Assert.That(panel.OnKey(UiKey.ForChar(c), Ctx(new UiState(), services))).IsTrue();
        }

        await Assert.That(model.Query).IsEqualTo("dif");
        await Assert.That(store.State.Input.Text).IsEmpty();
        await Assert.That(store.State.PanelStates[OverlayIds.JumpPalette]).IsEqualTo(TuiPanelState.Visible);

        // Modified chords stay unconsumed so Ctrl+J can still toggle the palette
        // closed and Alt+N can still switch panel slots from under it.
        await Assert.That(panel.OnKey(UiKey.ForChar('j', KeyModifierSet.Ctrl), Ctx(new UiState(), services))).IsFalse();
        await Assert.That(panel.OnKey(UiKey.ForChar('1', KeyModifierSet.Alt), Ctx(new UiState(), services))).IsFalse();
        await Assert.That(model.Query).IsEqualTo("dif");
    }

    [Test]
    public async Task OnKey_Escape_ClearsQuery_ReopenStartsEmpty()
    {
        var model = new WorktreeJumpPaletteModel();
        var panel = new CellForgeJumpPalettePanel(model)
        {
            WorktreePorcelainReader = () => Porcelain(),
        };
        var store = VisibleJumpStore();
        var services = new FakeServices().Add<UiStore>(store);
        var ctx = Ctx(new UiState(), services);

        _ = panel.Build(ctx);
        foreach (char c in "dif")
        {
            _ = panel.OnKey(UiKey.ForChar(c), ctx);
        }

        await Assert.That(model.Query).IsEqualTo("dif");
        await Assert.That(model.Results.Count).IsEqualTo(1);

        await Assert.That(panel.OnKey(new UiKey(UiKeyCode.Escape), ctx)).IsTrue();
        await Assert.That(model.Visible).IsFalse();
        await Assert.That(model.Query).IsEmpty();
        await Assert.That(store.State.PanelStates[OverlayIds.JumpPalette]).IsEqualTo(TuiPanelState.Hidden);

        // Reopening re-seeds and starts with an empty query and the top hit
        // selected (current Show semantics preserved).
        _ = panel.Build(ctx);
        await Assert.That(model.Visible).IsTrue();
        await Assert.That(model.Query).IsEmpty();
        await Assert.That(model.Results.Count).IsEqualTo(2);
        await Assert.That(model.SelectedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task OnKey_R_Reseeds_KeepingQuery()
    {
        int reads = 0;
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries);
        var panel = new CellForgeJumpPalettePanel(model)
        {
            WorktreePorcelainReader = () =>
            {
                reads++;
                return Porcelain();
            },
        };

        // A plain char types without touching git.
        _ = panel.OnKey(UiKey.ForChar('j'), Ctx(new UiState()));
        await Assert.That(model.Query).IsEqualTo("j");
        await Assert.That(reads).IsEqualTo(0);

        // 'r' re-reads git AND types normally — the refresh keeps the filter.
        await Assert.That(panel.OnKey(UiKey.ForChar('r'), Ctx(new UiState()))).IsTrue();
        await Assert.That(reads).IsEqualTo(1);
        await Assert.That(model.Visible).IsTrue();
        await Assert.That(model.Query).IsEqualTo("jr");
    }

    [Test]
    public async Task Build_VisiblePalette_DoesNotReseed_OrSpawnGit()
    {
        int reads = 0;
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries);
        var panel = new CellForgeJumpPalettePanel(model)
        {
            WorktreePorcelainReader = () =>
            {
                reads++;
                return "worktree /repo/.worktrees/other\n";
            },
        };

        _ = panel.Build(Ctx(new UiState()));
        await Assert.That(reads).IsEqualTo(0);

        // Rebuilding with the palette already visible must not re-seed — an
        // unchanged frame spawns no git process.
        _ = panel.Build(Ctx(new UiState()));
        _ = panel.Build(Ctx(new UiState()));
        await Assert.That(reads).IsEqualTo(0);
    }

    [Test]
    public async Task OverlayLayer_Centres_ModalBarrier_AndRoutesTypedKeys()
    {
        // #381: centred modal overlay, not a Right dock panel.
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries);
        var panel = new CellForgeJumpPalettePanel(model)
        {
            WorktreePorcelainReader = () => string.Empty,
        };
        var store = VisibleJumpStore();
        var layer = new CellForgeJumpPaletteOverlayLayer(panel);
        var viewport = new Rect(0, 0, 120, 40);
        layer.Sync(viewport, new PanelContext(store.State, 120, 40, null, store));

        await Assert.That(layer.Id).IsEqualTo(CellForgeJumpPaletteOverlayLayer.LayerId);
        await Assert.That(layer.Visible).IsTrue();
        await Assert.That(layer.IsModal).IsTrue();

        // Centred: the box is horizontally and vertically centred in the viewport.
        var box = layer.Bounds;
        await Assert.That(box.X).IsEqualTo((viewport.Width - box.Width) / 2);
        await Assert.That(box.Y).IsEqualTo((viewport.Height - box.Height) / 2);
        await Assert.That(box.Width).IsEqualTo(48);

        // The layer is modal: while it is up the z-stack reports a barrier, and
        // typed keys route into the query instead of leaking beneath.
        var overlays = new OverlayStack();
        overlays.Push(layer);
        await Assert.That(overlays.HasModalBarrier).IsTrue();
        await Assert.That(overlays.RouteKey(KeyEvent.Char(new Rune('w')))).IsTrue();
        await Assert.That(model.Query).IsEqualTo("w");
    }

    [Test]
    public async Task OverlayLayer_ClosedPalette_NoBarrier_AndNoPaint()
    {
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries);
        var panel = new CellForgeJumpPalettePanel(model)
        {
            WorktreePorcelainReader = () => string.Empty,
        };
        var store = HiddenJumpStore();
        var layer = new CellForgeJumpPaletteOverlayLayer(panel);
        layer.Sync(new Rect(0, 0, 120, 40), new PanelContext(store.State, 120, 40, null, store));

        await Assert.That(layer.Visible).IsFalse();
        var overlays = new OverlayStack();
        overlays.Push(layer);
        await Assert.That(overlays.HasModalBarrier).IsFalse();

        var buffer = new ScreenBuffer(120, 40);
        buffer.Fill(new Rect(0, 0, 120, 40), Cell.Blank);
        layer.Paint(buffer, new Rect(0, 0, 120, 40));
        await Assert.That(GridDump.Art(buffer).Trim()).IsEmpty();
    }

    [Test]
    public async Task AttachPanels_CenterPlacement_ReleasesDockSlot()
    {
        // The Right dock leaf the palette used to occupy is released: a Center
        // provider is the modal overlay plane and never docks.
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeJumpPalettePanel
        {
            WorktreePorcelainReader = () => string.Empty,
        });
        var store = new UiStore();
        _ = owner.EnsureSeeded(store);
        _ = store.Dispatch(new UiMsg.FocusPanel(OverlayIds.JumpPalette));

        var screen = ChatScreen.Build(new ComposerController(), new StatusViewModel(), includeSidebar: false);
        ChatScreenPanelDock.AttachPanels(
            screen, owner.Registry, store.State, services: null, viewportWidth: 100, viewportHeight: 40);

        await Assert.That(ChatScreenPanelDock.HasDocks(screen)).IsFalse();

        // ... and the dock neither paints it in the bottom-stack fallback nor
        // routes keys to it (the overlay layer owns both).
        var buffer = new ScreenBuffer(100, 40);
        int painted = ChatScreenPanelDock.PaintBottomStack(
            buffer, screen.Timeline.Rect, owner.Registry, store.State, services: null);
        await Assert.That(painted).IsEqualTo(0);
        await Assert.That(ChatScreenPanelDock.RoutePanelKey(
            owner.Registry, store.State, UiKey.ForChar('w'), null, store: store)).IsFalse();
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
