using System.Collections.Immutable;
using Harbor.Tui.CellForge.Panels;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// CF-E-002 wiring (TOP-1 #27): the renderer owns a <see cref="CellForgePanelRegistry"/>
/// with the 7 cell-native builtins in Spectre Alt+1..9 slot order,
/// <c>InitializeAsync</c> seeds them into <see cref="UiState"/>,
/// <see cref="ChatScreenPanelDock"/> draws visible panels through
/// <see cref="CellForgePanelAdapter"/> into <see cref="LayoutTree"/> dock regions
/// (or the bottom-stack fallback), and focused-panel keys route to <c>OnKey</c>.
/// </summary>
public class PanelWiringTests
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

    private static readonly string[] ExpectedOrder =
    [
        "help",
        "todo-list",
        "diff-preview",
        "file-tree",
        "token-breakdown",
        "diagnostics",
        "logs",
    ];

    private static CellForgeTuiRenderer Create(RecordingBackend backend) =>
        new(NullLogger<CellForgeTuiRenderer>.Instance, backend);

    private static ChatScreen BuildScreen() =>
        ChatScreen.Build(new ComposerController(), new StatusViewModel(), includeSidebar: false);

    private static void PaintAll(ChatScreen screen, ScreenBuffer buffer)
    {
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(buffer);
        }
    }

    private static CellForgeDockPanel FindDock(ChatScreen screen, string id)
    {
        foreach (var panel in screen.Tree.Panels)
        {
            if (panel is CellForgeDockPanel dock && dock.Id == id)
            {
                return dock;
            }
        }

        throw new InvalidOperationException($"dock leaf '{id}' not attached");
    }

    [Test]
    public async Task Registry_Has_Seven_Builtins_In_Spectre_Slot_Order()
    {
        var backend = new RecordingBackend();
        using var renderer = Create(backend);

        var all = renderer.Panels.Registry.All;
        await Assert.That(all.Count).IsEqualTo(ExpectedOrder.Length);
        for (int i = 0; i < ExpectedOrder.Length; i++)
        {
            await Assert.That(all[i].Id).IsEqualTo(ExpectedOrder[i]);
        }
    }

    [Test]
    public async Task InitializeAsync_Seeds_Panels_Hidden_With_Default_Sizes()
    {
        var backend = new RecordingBackend();
        using var renderer = Create(backend);
        await renderer.InitializeAsync();

        var state = renderer.Store.State;
        await Assert.That(state.RegisteredPanelIds.Length).IsEqualTo(ExpectedOrder.Length);
        for (int i = 0; i < ExpectedOrder.Length; i++)
        {
            await Assert.That(state.RegisteredPanelIds[i]).IsEqualTo(ExpectedOrder[i]);
        }

        foreach (string id in ExpectedOrder)
        {
            var provider = renderer.Panels.Registry.Get(id);
            await Assert.That(provider).IsNotNull();
            await Assert.That(state.PanelStates[id]).IsEqualTo(TuiPanelState.Hidden);
            await Assert.That(state.PanelSizes[id]).IsEqualTo(provider!.DefaultSize);
        }
    }

    [Test]
    public async Task TogglePanel_Flips_Seeded_Visibility()
    {
        var backend = new RecordingBackend();
        using var renderer = Create(backend);
        await renderer.InitializeAsync();

        _ = renderer.Store.Dispatch(new UiMsg.TogglePanel("help"));
        await Assert.That(renderer.Store.State.PanelStates["help"]).IsEqualTo(TuiPanelState.Visible);

        _ = renderer.Store.Dispatch(new UiMsg.TogglePanel("help"));
        await Assert.That(renderer.Store.State.PanelStates["help"]).IsEqualTo(TuiPanelState.Hidden);
    }

    [Test]
    public async Task AttachPanels_BottomDock_Paints_RenderToRows_Rows_In_Frame()
    {
        var backend = new RecordingBackend();
        using var renderer = Create(backend);
        await renderer.InitializeAsync();

        _ = renderer.Store.Dispatch(new UiMsg.TogglePanel("token-breakdown"));

        var screen = BuildScreen();
        await Assert.That(ChatScreenPanelDock.HasDocks(screen)).IsFalse();

        ChatScreenPanelDock.AttachPanels(
            screen, renderer.Panels.Registry, renderer.Store.State,
            services: null, viewportWidth: 100, viewportHeight: 40);

        await Assert.That(ChatScreenPanelDock.HasDocks(screen)).IsTrue();
        var dock = FindDock(screen, CellForgeDockPanel.BottomId);
        await Assert.That(dock.Providers.Count).IsEqualTo(1);

        var buffer = new ScreenBuffer(100, 40);
        PaintAll(screen, buffer);
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("Token Breakdown");
    }

    [Test]
    public async Task AttachPanels_RightDock_Paints_Side_Panel_Rows()
    {
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeTodoListPanel());
        var store = new UiStore(new UiState
        {
            Lines = ImmutableArray.Create(new ChatLine(ChatRole.ToolResult, "[ ] Write code")),
        });
        _ = owner.EnsureSeeded(store);
        _ = store.Dispatch(new UiMsg.TogglePanel("todo-list"));

        var screen = BuildScreen();
        ChatScreenPanelDock.AttachPanels(
            screen, owner.Registry, store.State,
            services: null, viewportWidth: 100, viewportHeight: 40);

        var dock = FindDock(screen, CellForgeDockPanel.RightId);
        await Assert.That(dock.Providers.Count).IsEqualTo(1);

        var buffer = new ScreenBuffer(100, 40);
        PaintAll(screen, buffer);
        await Assert.That(GridDump.Art(buffer)).Contains("Todo List");
    }

    [Test]
    public async Task UpdatePanels_Refreshes_Leaf_Without_Tree_Surgery()
    {
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeTokenBreakdownPanel());
        var store = new UiStore(new UiState { Cost = new CostSnapshot(1500, 300, 0.0042m) });
        _ = owner.EnsureSeeded(store);
        _ = store.Dispatch(new UiMsg.TogglePanel("token-breakdown"));

        var screen = BuildScreen();
        ChatScreenPanelDock.AttachPanels(
            screen, owner.Registry, store.State,
            services: null, viewportWidth: 100, viewportHeight: 40);

        var buffer = new ScreenBuffer(100, 40);
        PaintAll(screen, buffer);
        await Assert.That(GridDump.Art(buffer)).Contains("1.5K");

        // A fresh snapshot (same visible set, new numbers) refreshes the leaf
        // payload in place — no re-attach, no tree surgery — and repaints.
        var next = store.State with { Cost = new CostSnapshot(2500, 100, 0.01m) };
        ChatScreenPanelDock.UpdatePanels(screen, owner.Registry, next, services: null);

        var after = new ScreenBuffer(100, 40);
        PaintAll(screen, after);
        string art = GridDump.Art(after);
        await Assert.That(art).Contains("2.5K");
        await Assert.That(art).DoesNotContain("1.5K");
    }

    [Test]
    public async Task RouteKey_Help_QuestionMark_Toggles_Panel()
    {
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeHelpPanel());
        var store = new UiStore();
        _ = owner.EnsureSeeded(store);
        _ = store.Dispatch(new UiMsg.TogglePanel("help"));
        _ = store.Dispatch(new UiMsg.FocusPanel("help"));
        var services = new FakeServices().Add<UiStore>(store);

        bool consumed = ChatScreenPanelDock.RoutePanelKey(
            owner.Registry, store.State, UiKey.ForChar('?'), services);

        await Assert.That(consumed).IsTrue();
        await Assert.That(store.State.PanelStates["help"]).IsEqualTo(TuiPanelState.Hidden);
    }

    [Test]
    public async Task RouteKey_Logs_F12_Toggles_Panel()
    {
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeLogsPanel());
        var store = new UiStore();
        _ = owner.EnsureSeeded(store);
        _ = store.Dispatch(new UiMsg.TogglePanel("logs"));
        _ = store.Dispatch(new UiMsg.FocusPanel("logs"));
        var services = new FakeServices().Add<UiStore>(store);

        bool consumed = ChatScreenPanelDock.RoutePanelKey(
            owner.Registry, store.State, new UiKey(UiKeyCode.F12), services);

        await Assert.That(consumed).IsTrue();
        await Assert.That(store.State.PanelStates["logs"]).IsEqualTo(TuiPanelState.Hidden);
    }

    [Test]
    public async Task RouteKey_Unfocused_Panel_Is_Not_Routed()
    {
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeHelpPanel());
        var store = new UiStore();
        _ = owner.EnsureSeeded(store);
        _ = store.Dispatch(new UiMsg.TogglePanel("help")); // Visible, never focused
        var services = new FakeServices().Add<UiStore>(store);

        bool consumed = ChatScreenPanelDock.RoutePanelKey(
            owner.Registry, store.State, UiKey.ForChar('?'), services);

        await Assert.That(consumed).IsFalse();
        await Assert.That(store.State.PanelStates["help"]).IsEqualTo(TuiPanelState.Visible);
    }

    [Test]
    public async Task RouteKey_Unknown_Registry_Returns_False()
    {
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeHelpPanel());
        var store = new UiStore();
        _ = owner.EnsureSeeded(store);
        _ = store.Dispatch(new UiMsg.FocusPanel("help"));
        var services = new FakeServices().Add<UiStore>(store);

        bool consumed = ChatScreenPanelDock.RoutePanelKey(
            new PanelRegistry(), store.State, UiKey.ForChar('?'), services);

        await Assert.That(consumed).IsFalse();
    }

    [Test]
    public async Task RouteKey_Pure_Panel_Delegates_To_Provider()
    {
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeTodoListPanel());
        var store = new UiStore();
        _ = owner.EnsureSeeded(store);
        _ = store.Dispatch(new UiMsg.FocusPanel("todo-list"));
        var services = new FakeServices().Add<UiStore>(store);

        // Todo-list is non-interactive: OnKey returns false, routing must surface it.
        bool consumed = ChatScreenPanelDock.RoutePanelKey(
            owner.Registry, store.State, UiKey.ForChar('j'), services);

        await Assert.That(consumed).IsFalse();
    }

    [Test]
    public async Task PaintBottomStack_Fallback_Paints_Visible_Panels_Under_Timeline()
    {
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeTokenBreakdownPanel());
        var store = new UiStore(new UiState { Cost = new CostSnapshot(1500, 300, 0.0042m) });
        _ = owner.EnsureSeeded(store);
        _ = store.Dispatch(new UiMsg.TogglePanel("token-breakdown"));

        var screen = BuildScreen();
        screen.Tree.Solve(100, 40);
        await Assert.That(ChatScreenPanelDock.HasDocks(screen)).IsFalse();

        var buffer = new ScreenBuffer(100, 40);
        int painted = ChatScreenPanelDock.PaintBottomStack(
            buffer, screen.Timeline.Rect, owner.Registry, store.State, services: null);

        await Assert.That(painted > 0).IsTrue();
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("Token Breakdown");
        await Assert.That(art).Contains("1.5K");
    }
}
