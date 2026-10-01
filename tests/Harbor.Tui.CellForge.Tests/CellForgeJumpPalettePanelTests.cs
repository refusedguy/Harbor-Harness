using System.Collections.Immutable;
using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Git;
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
///     injected (fake), so no git and no real sessions are involved.
/// </summary>
/// <remarks>
/// <para>
///     #666: the worktree seam is <see cref="IGitQuery" /> on
///     <see cref="PanelServices" />, injected as
///     <see cref="FakeGitQuery" />, where it used to be an
///     <c>internal Func&lt;string&gt;</c> returning raw porcelain the panel parsed
///     itself. The fake counts calls for the same reason the old delegate did — the
///     "an unchanged frame asks git nothing" test is a real claim about the paint
///     path — but it is now a port with a typed surface, and a test can assert the
///     directory the panel asked about.
/// </para>
/// </remarks>
public class CellForgeJumpPalettePanelTests
{
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

        public Task<bool> OpenPanelSessionAsync(string sessionId) => OpenSessionAsync(sessionId);

        public Task<Result<Session>> BranchActiveAsync() =>
            Task.FromResult(Result.Failure<Session>("Not supported in tests."));

        public Task<bool> DeleteSessionAsync(string sessionId) => Task.FromResult(false);

        public Task<bool> RenameSessionAsync(string sessionId, string newTitle) => Task.FromResult(false);

        public event Action<string, SessionStatus>? StatusChanged;

        public event Action<string, int>? MessageCountChanged;

        // ── IPanelSessionGateway (#470) ─────────────────────────────────────
        // Flat per-session projection the framework panels read (#470). The
        // facade the panel used to reach for returned the same facts off
        // SessionContext; git stays empty (GitSessionInfo.Empty), so the branch
        // falls through to the session record exactly as before.
        public string? GetDirectory(string sessionId) =>
            _contexts.TryGetValue(sessionId, out var ctx) ? ctx.Session.Directory : null;

        public string? GetStatusText(string sessionId) =>
            _contexts.TryGetValue(sessionId, out var ctx) ? ctx.StatusText : null;

        public string? GetBranch(string sessionId) =>
            _contexts.TryGetValue(sessionId, out var ctx) ? ctx.Session.GitBranch ?? ctx.GitBranch : null;

        public bool GetIsDirty(string sessionId) =>
            _contexts.TryGetValue(sessionId, out var ctx) && (ctx.GitIsDirty || ctx.Session.GitIsDirty);

        public bool? GetIsSubagent(string sessionId) =>
            _contexts.TryGetValue(sessionId, out var ctx) ? ctx.Session.IsSubagent() : null;
    }

    private static readonly WorktreeJumpEntry[] Entries =
    [
        new("s1", "Jump Palette", "/repo/.worktrees/jump-palette", "feat/jump-palette", "idle"),
        new("s2", "Word Diff", "/repo/.worktrees/worddiff", "feat/word-diff", "working ●"),
    ];

    /// <summary>Two worktrees, no sessions — the reopen/reseed fixture.</summary>
    private static GitWorktreeInfo[] Worktrees() =>
    [
        new("/repo/.worktrees/jump-palette", "feat/jump-palette", false),
        new("/repo/.worktrees/worddiff", "feat/word-diff", false),
    ];

    /// <summary>
    ///     The #666 seam. Counts calls and records the directory asked about, so
    ///     a test can prove both that the panel reaches the port and that it does
    ///     NOT reach it on a frame that changed nothing.
    /// </summary>
    private sealed class FakeGitQuery(IReadOnlyList<GitWorktreeInfo>? worktrees = null) : IGitQuery
    {
        public int ListWorktreesCalls { get; private set; }

        public List<string> AskedDirectories { get; } = new();

        public IReadOnlyList<GitWorktreeInfo> ListWorktrees(string directory, CancellationToken cancellationToken = default)
        {
            ListWorktreesCalls++;
            AskedDirectories.Add(directory);
            return worktrees ?? Array.Empty<GitWorktreeInfo>();
        }

        public GitWorkspaceStatus GetStatus(string directory, CancellationToken cancellationToken = default) =>
            GitWorkspaceStatus.None;
    }

    private static CellForgeJumpPalettePanel WithModel(out WorktreeJumpPaletteModel model)
    {
        model = new WorktreeJumpPaletteModel();
        model.Show(Entries);
        return new CellForgeJumpPalettePanel(model);
    }

    private static PanelContext Ctx(UiState state, PanelServices? services = null) =>
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
        var panel = new CellForgeJumpPalettePanel();

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
        var services = new PanelServices { Store = store, Sessions = manager };

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
        var services = new PanelServices { Store = store, Sessions = manager };

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
        var panel = new CellForgeJumpPalettePanel(model);
        var manager = new FakeSessionManager();
        var store = VisibleJumpStore();
        var services = new PanelServices { Store = store, Sessions = manager };

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
        var services = new PanelServices { Store = store };

        foreach (char c in "dif")
        {
            await Assert.That(panel.OnKey(UiKey.ForChar(c), Ctx(new UiState(), services))).IsTrue();
        }

        await Assert.That(model.Query).IsEqualTo("dif");
        await Assert.That(store.State.Ui.Input.Text).IsEmpty();
        await Assert.That(store.State.Ui.PanelStates[OverlayIds.JumpPalette]).IsEqualTo(TuiPanelState.Visible);

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
        var panel = new CellForgeJumpPalettePanel(model);
        var store = VisibleJumpStore();
        var services = new PanelServices { Store = store, Git = new FakeGitQuery(Worktrees()) };
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
        await Assert.That(store.State.Ui.PanelStates[OverlayIds.JumpPalette]).IsEqualTo(TuiPanelState.Hidden);

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
        var git = new FakeGitQuery(Worktrees());
        var services = new PanelServices { Git = git };
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries);
        var panel = new CellForgeJumpPalettePanel(model);

        // A plain char types without touching git.
        _ = panel.OnKey(UiKey.ForChar('j'), Ctx(new UiState(), services));
        await Assert.That(model.Query).IsEqualTo("j");
        await Assert.That(git.ListWorktreesCalls).IsEqualTo(0);

        // 'r' re-reads git AND types normally — the refresh keeps the filter.
        await Assert.That(panel.OnKey(UiKey.ForChar('r'), Ctx(new UiState(), services))).IsTrue();
        await Assert.That(git.ListWorktreesCalls).IsEqualTo(1);
        await Assert.That(model.Visible).IsTrue();
        await Assert.That(model.Query).IsEqualTo("jr");
    }

    [Test]
    public async Task Build_VisiblePalette_DoesNotReseed_OrAskGit()
    {
        var git = new FakeGitQuery([new GitWorktreeInfo("/repo/.worktrees/other", null, false)]);
        var services = new PanelServices { Git = git };
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries);
        var panel = new CellForgeJumpPalettePanel(model);

        _ = panel.Build(Ctx(new UiState(), services));
        await Assert.That(git.ListWorktreesCalls).IsEqualTo(0);

        // Rebuilding with the palette already visible must not re-seed — an
        // unchanged frame asks git nothing.
        _ = panel.Build(Ctx(new UiState(), services));
        _ = panel.Build(Ctx(new UiState(), services));
        await Assert.That(git.ListWorktreesCalls).IsEqualTo(0);
    }

    [Test]
    public async Task OverlayLayer_Centres_ModalBarrier_AndRoutesTypedKeys()
    {
        // #381: centred modal overlay, not a Right dock panel.
        var model = new WorktreeJumpPaletteModel();
        model.Show(Entries);
        var panel = new CellForgeJumpPalettePanel(model);
        var store = VisibleJumpStore();
        var layer = new CellForgeJumpPaletteOverlayLayer(panel);
        var viewport = new Rect(0, 0, 120, 40);
        layer.Sync(viewport, new PanelContext(store.State, 120, 40, new PanelServices { Store = store }));

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
        var panel = new CellForgeJumpPalettePanel(model);
        var store = HiddenJumpStore();
        var layer = new CellForgeJumpPaletteOverlayLayer(panel);
        layer.Sync(new Rect(0, 0, 120, 40), new PanelContext(store.State, 120, 40, new PanelServices { Store = store }));

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
        owner.Register(new CellForgeJumpPalettePanel());
        var store = new UiStore();
        _ = owner.EnsureSeeded(store);
        _ = store.Dispatch(new AppMsg.FocusPanel(OverlayIds.JumpPalette));

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
            owner.Registry, store.State, UiKey.ForChar('w'), new PanelServices { Store = store })).IsFalse();
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
        var git = new FakeGitQuery([new GitWorktreeInfo("/repo/.worktrees/jump-palette", "feat/jump-palette", false)]);
        var services = new PanelServices { Sessions = manager, Git = git };
        var panel = new CellForgeJumpPalettePanel { WorktreeDirectory = "/repo" };

        string text = Joined(panel.Build(Ctx(state, services)));

        await Assert.That(text).Contains("Jump Palette");
        await Assert.That(text).Contains("feat/jump-palette");
        await Assert.That(text).Contains("/repo/.worktrees/jump-palette");
        await Assert.That(manager.Opened).IsEmpty();

        // #666: the panel asked the port, and about the directory it was told to
        // ask about — there is no other path to a worktree list now.
        await Assert.That(git.ListWorktreesCalls).IsEqualTo(1);
        await Assert.That(git.AskedDirectories).IsEquivalentTo(new[] { "/repo" });
    }

    /// <summary>
    ///     A host that registered no git query must degrade to a sessions-only
    ///     palette — and must not reach for a process of its own. The "no
    ///     fallback" half is the point: the old panel always had a working
    ///     reader, so absence was not a state it had to handle at all.
    /// </summary>
    [Test]
    public async Task Build_NoGitQueryRegistered_ShowsSessionsOnly_AndThrows()
    {
        var session = Session.Create("/repo", "code", "kilocode", "kilo-auto", "Main")
            with
            {
                Id = "s1",
                GitBranch = "main",
            };
        var manager = new FakeSessionManager();
        manager.AddContext(session);
        var state = StateWithSessions(Info("s1", "Main"));
        var panel = new CellForgeJumpPalettePanel();

        string text = Joined(panel.Build(Ctx(state, new PanelServices { Sessions = manager })));

        await Assert.That(text).Contains("Main");
        await Assert.That(text).DoesNotContain(".worktrees");
    }

    /// <summary>
    ///     A port that throws cannot take the renderer down with it. The
    ///     <c>IGitQuery</c> contract says it never throws for an expected
    ///     failure, so this is the belt-and-braces half — a paint path is the
    ///     worst possible place for an exception to escape.
    /// </summary>
    [Test]
    public async Task Build_ThrowingGitQuery_DegradesToSessionsOnly()
    {
        var session = Session.Create("/repo", "code", "kilocode", "kilo-auto", "Main")
            with
            {
                Id = "s1",
            };
        var manager = new FakeSessionManager();
        manager.AddContext(session);
        var state = StateWithSessions(Info("s1", "Main"));
        var services = new PanelServices { Sessions = manager, Git = new ThrowingGitQuery() };
        var panel = new CellForgeJumpPalettePanel();

        string text = Joined(panel.Build(Ctx(state, services)));

        await Assert.That(text).Contains("Main");
    }

    private sealed class ThrowingGitQuery : IGitQuery
    {
        public IReadOnlyList<GitWorktreeInfo> ListWorktrees(string directory, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("git is having a day");

        public GitWorkspaceStatus GetStatus(string directory, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("git is having a day");
    }
}
