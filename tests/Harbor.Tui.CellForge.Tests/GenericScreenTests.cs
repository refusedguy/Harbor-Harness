using System.Collections.Immutable;
using System.Text;
using Harbor.Tui.CellForge.Generic;
using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

[NotInParallel("pty")]
public class GenericScreenTests
{
    [Test]
    public async Task Build_SingleNode_PaintsTitle()
    {
        var screen = GenericScreen.Build([
            new GenericNodeSpec { Id = "a", Title = "hello generic" },
        ]);
        await Assert.That(screen.Count).IsEqualTo(1);
        var buffer = TestRender.Buffer(30, 6);
        screen.Solve(30, 6);
        screen.PaintAll(buffer);
        await Assert.That(ScreenFixtures.Art(buffer).Contains("hello generic")).IsTrue();
    }

    [Test]
    public async Task Build_TwoNodes_WithSplit_PaintsBoth()
    {
        var screen = GenericScreen.Build(
            [
                new GenericNodeSpec { Id = "top", Title = "top pane" },
                new GenericNodeSpec { Id = "bottom", Title = "bottom pane" },
            ],
            [
                new GenericSplitSpec { TargetId = "top", Direction = Harbor.Tui.CellForge.Rendering.SplitDir.Vertical, Ratio = 0.5f, NewPanelId = "bottom", Gap = 0 },
            ]);
        await Assert.That(screen.Count).IsEqualTo(2);
        var buffer = TestRender.Buffer(30, 10);
        screen.Solve(30, 10);
        screen.PaintAll(buffer);
        string art = ScreenFixtures.Art(buffer);
        await Assert.That(art.Contains("top pane")).IsTrue();
        await Assert.That(art.Contains("bottom pane")).IsTrue();
    }

    [Test]
    public async Task HandleKey_Tab_CyclesFocus()
    {
        var screen = GenericScreen.Build([
            new GenericNodeSpec { Id = "one", Title = "one" },
            new GenericNodeSpec { Id = "two", Title = "two" },
        ]);
        await Assert.That(screen.FocusedId).IsEqualTo("one");
        await Assert.That(screen.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsTrue();
        await Assert.That(screen.FocusedId).IsEqualTo("two");
        await Assert.That(screen.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsTrue();
        await Assert.That(screen.FocusedId).IsEqualTo("one");
    }

    [Test]
    public async Task HandleKey_Typing_ReachesHostedField()
    {
        var field = new GenericTextField();
        var screen = GenericScreen.Build([
            new GenericNodeSpec { Id = "f", Title = "form", Field = field },
        ]);
        await Assert.That(screen.HandleKey(KeyEvent.Char(new Rune('z')))).IsTrue();
        await Assert.That(field.Text).IsEqualTo("z");
    }

    [Test]
    public async Task Renderer_Render_PaintsScreenWithoutChat()
    {
        var screen = GenericScreen.Build([
            new GenericNodeSpec { Id = "main", Title = "neutral screen", Lines = ["row one", "row two"] },
        ]);
        using var renderer = new GenericTuiRenderer(new UiStore(), screen);
        var buffer = renderer.Render(40, 8);
        string art = ScreenFixtures.Art(buffer);
        await Assert.That(art.Contains("neutral screen")).IsTrue();
        await Assert.That(art.Contains("row one")).IsTrue();
        await Assert.That(renderer.ShouldQuit).IsFalse();
    }

    [Test]
    public async Task Renderer_Dispatch_GenericFocus_ReachesStore()
    {
        var store = new UiStore();
        _ = store.Dispatch(new AppMsg.SeedPanels(
            ["p1", "p2"],
            ImmutableDictionary<string, TuiPanelState>.Empty
                .Add("p1", TuiPanelState.Visible)
                .Add("p2", TuiPanelState.Visible),
            ImmutableDictionary<string, int>.Empty));
        var screen = GenericScreen.Build([
            new GenericNodeSpec { Id = "p1", Title = "p1" },
            new GenericNodeSpec { Id = "p2", Title = "p2" },
        ]);
        using var renderer = new GenericTuiRenderer(store, screen);
        _ = renderer.FocusPanel("p2");
        await Assert.That(renderer.FocusedPanelId).IsEqualTo("p2");
        _ = renderer.TogglePanel("p2");
        await Assert.That(store.State.Ui.PanelStates["p2"]).IsEqualTo(TuiPanelState.Hidden);
    }

    [Test]
    public async Task Renderer_Quit_SetsShouldQuit()
    {
        using var renderer = new GenericTuiRenderer(new UiStore());
        _ = renderer.Quit();
        await Assert.That(renderer.ShouldQuit).IsTrue();
    }

    [Test]
    public async Task Mode_DefaultsToRunning()
    {
        var screen = GenericScreen.Build([
            new GenericNodeSpec { Id = "a", Title = "mode" },
        ]);
        await Assert.That(screen.Mode).IsEqualTo(GenericScreenMode.Running);
        await Assert.That(screen.IsTerminal).IsFalse();
    }

    [Test]
    public async Task HandleKey_Escape_CompletesCancelled()
    {
        var screen = GenericScreen.Build([
            new GenericNodeSpec { Id = "a", Title = "mode" },
        ]);
        await Assert.That(screen.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsTrue();
        await Assert.That(screen.Mode).IsEqualTo(GenericScreenMode.Cancelled);
        await Assert.That(screen.IsTerminal).IsTrue();
    }

    [Test]
    public async Task Complete_Done_SetsTerminal_AndRunningIsRejected()
    {
        var screen = GenericScreen.Build([
            new GenericNodeSpec { Id = "a", Title = "mode" },
        ]);
        screen.Complete(GenericScreenMode.Done);
        await Assert.That(screen.Mode).IsEqualTo(GenericScreenMode.Done);
        await Assert.That(screen.IsTerminal).IsTrue();
        screen.Complete(GenericScreenMode.Cancelled);
        await Assert.That(screen.Mode).IsEqualTo(GenericScreenMode.Done);
        await Assert.That(() => screen.Complete(GenericScreenMode.Running)).Throws<ArgumentOutOfRangeException>();
    }
}
