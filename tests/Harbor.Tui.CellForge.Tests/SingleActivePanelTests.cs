using System.Collections.Immutable;
using Harbor.Tui.CellForge.Panels;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// UX1 (#261, part of epic #260): fixed slot layout
/// (feed / composer / ONE active panel / statusline).
/// <see cref="PanelArbiter"/> picks the single winner (focused, else pinned,
/// else first visible in registration order); <see cref="ChatScreenPanelDock"/>
/// attaches at most one dock leaf and the bottom-stack fallback paints the
/// winner only — secondary visible panels go modal (never painted here).
/// </summary>
public class SingleActivePanelTests
{
    private static ChatScreen BuildScreen() =>
        ChatScreen.Build(new ComposerController(), new StatusViewModel(), includeSidebar: false);

    private static UiStore SeededStore(CellForgePanelRegistry owner)
    {
        var store = new UiStore(new UiState
        {
            Lines = ImmutableArray.Create(new ChatLine(ChatRole.ToolResult, "[ ] Write code")),
            Cost = new CostSnapshot(1500, 300, 0.0042m),
        });
        _ = owner.EnsureSeeded(store);
        return store;
    }

    private static void PaintAll(ChatScreen screen, ScreenBuffer buffer)
    {
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(buffer);
        }
    }

    private static int CountDocks(ChatScreen screen)
    {
        int n = 0;
        foreach (var panel in screen.Tree.Panels)
        {
            if (panel is CellForgeDockPanel)
            {
                n++;
            }
        }

        return n;
    }

    private static bool HasDock(ChatScreen screen, string id)
    {
        foreach (var panel in screen.Tree.Panels)
        {
            if (panel is CellForgeDockPanel dock && dock.Id == id)
            {
                return true;
            }
        }

        return false;
    }

    [Test]
    public async Task Arbiter_NoVisible_ReturnsNull()
    {
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeTodoListPanel());
        var store = SeededStore(owner);

        var winner = PanelArbiter.ResolveActive(owner.Registry, store.State);

        await Assert.That(winner).IsNull();
    }

    [Test]
    public async Task Arbiter_Focused_Beats_Visible()
    {
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeTodoListPanel());
        owner.Register(new CellForgeTokenBreakdownPanel());
        var store = SeededStore(owner);
        _ = store.Dispatch(new UiMsg.TogglePanel("todo-list"));
        _ = store.Dispatch(new UiMsg.TogglePanel("token-breakdown"));
        _ = store.Dispatch(new UiMsg.FocusPanel("token-breakdown"));

        var winner = PanelArbiter.ResolveActive(owner.Registry, store.State);

        await Assert.That(winner).IsNotNull();
        await Assert.That(winner!.Id).IsEqualTo("token-breakdown");
    }

    [Test]
    public async Task Arbiter_Pinned_Beats_Visible()
    {
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeTodoListPanel());
        owner.Register(new CellForgeTokenBreakdownPanel());
        var state = new UiState
        {
            PanelStates = ImmutableDictionary.Create<string, TuiPanelState>(StringComparer.Ordinal)
                .Add("todo-list", TuiPanelState.Pinned)
                .Add("token-breakdown", TuiPanelState.Visible),
        };

        var winner = PanelArbiter.ResolveActive(owner.Registry, state);

        await Assert.That(winner).IsNotNull();
        await Assert.That(winner!.Id).IsEqualTo("todo-list");
    }

    [Test]
    public async Task Arbiter_Visible_Falls_Back_To_Registration_Order()
    {
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeTodoListPanel());
        owner.Register(new CellForgeTokenBreakdownPanel());
        var store = SeededStore(owner);
        _ = store.Dispatch(new UiMsg.TogglePanel("todo-list"));
        _ = store.Dispatch(new UiMsg.TogglePanel("token-breakdown"));

        var winner = PanelArbiter.ResolveActive(owner.Registry, store.State);

        await Assert.That(winner).IsNotNull();
        await Assert.That(winner!.Id).IsEqualTo("todo-list");
    }

    [Test]
    public async Task AttachPanels_TwoVisible_Attaches_Single_Dock_For_Focused()
    {
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeTodoListPanel());
        owner.Register(new CellForgeTokenBreakdownPanel());
        var store = SeededStore(owner);
        _ = store.Dispatch(new UiMsg.TogglePanel("todo-list"));
        _ = store.Dispatch(new UiMsg.TogglePanel("token-breakdown"));
        _ = store.Dispatch(new UiMsg.FocusPanel("token-breakdown"));

        var screen = BuildScreen();
        ChatScreenPanelDock.AttachPanels(
            screen, owner.Registry, store.State,
            services: null, viewportWidth: 100, viewportHeight: 40);

        await Assert.That(CountDocks(screen)).IsEqualTo(1);
        await Assert.That(HasDock(screen, CellForgeDockPanel.BottomId)).IsTrue();

        var buffer = new ScreenBuffer(100, 40);
        PaintAll(screen, buffer);
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("Token Breakdown");
        await Assert.That(art).DoesNotContain("Todo List");
    }

    [Test]
    public async Task AttachPanels_TwoVisible_Registration_Order_Wins_Without_Focus()
    {
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeTodoListPanel());
        owner.Register(new CellForgeTokenBreakdownPanel());
        var store = SeededStore(owner);
        _ = store.Dispatch(new UiMsg.TogglePanel("todo-list"));
        _ = store.Dispatch(new UiMsg.TogglePanel("token-breakdown"));

        var screen = BuildScreen();
        ChatScreenPanelDock.AttachPanels(
            screen, owner.Registry, store.State,
            services: null, viewportWidth: 100, viewportHeight: 40);

        await Assert.That(CountDocks(screen)).IsEqualTo(1);
        await Assert.That(HasDock(screen, CellForgeDockPanel.RightId)).IsTrue();

        var buffer = new ScreenBuffer(100, 40);
        PaintAll(screen, buffer);
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("Todo List");
        await Assert.That(art).DoesNotContain("Token Breakdown");
    }

    [Test]
    public async Task PaintBottomStack_TwoVisible_Paints_Only_Winner()
    {
        var owner = new CellForgePanelRegistry();
        owner.Register(new CellForgeTodoListPanel());
        owner.Register(new CellForgeTokenBreakdownPanel());
        var store = SeededStore(owner);
        _ = store.Dispatch(new UiMsg.TogglePanel("todo-list"));
        _ = store.Dispatch(new UiMsg.TogglePanel("token-breakdown"));

        var screen = BuildScreen();
        screen.Tree.Solve(100, 40);
        await Assert.That(ChatScreenPanelDock.HasDocks(screen)).IsFalse();

        var buffer = new ScreenBuffer(100, 40);
        int painted = ChatScreenPanelDock.PaintBottomStack(
            buffer, screen.Timeline.Rect, owner.Registry, store.State, services: null);

        await Assert.That(painted > 0).IsTrue();
        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("Todo List");
        await Assert.That(art).DoesNotContain("Token Breakdown");
    }
}
