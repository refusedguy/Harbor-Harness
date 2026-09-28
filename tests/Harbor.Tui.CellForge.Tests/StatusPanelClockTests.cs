using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// ENG5 (issue #276): with an <see cref="AnimationClock"/> attached, the
/// status panel replays the clock's wall-clock tick instead of counting
/// paints — animation timing survives skipped frames. Detached (default) the
/// legacy per-paint increment applies (pinned by StatusPanelMascotTests).
/// Fully deterministic: the clock is stepped manually, never started.
/// </summary>
public class StatusPanelClockTests
{
    private static ChatScreen BuildScreen()
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Running };
        return ChatScreen.Build(composer, status, includeSidebar: false);
    }

    private static string PaintAll(ChatScreen screen, ScreenBuffer buffer)
    {
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(buffer);
        }

        return GridDump.Art(buffer);
    }

    [Test]
    public async Task AttachedClock_Mirrors_Tick_Instead_Of_Paint_Count()
    {
        var screen = BuildScreen();
        var buffer = new ScreenBuffer(120, 8);
        screen.Tree.Solve(120, 8);
        using var clock = new AnimationClock();
        screen.Status.AnimationClock = clock;

        string first = PaintAll(screen, buffer);
        string second = PaintAll(screen, buffer);

        // Two paints, zero clock steps: same tick, same spinner frame.
        await Assert.That(screen.Status.Tick).IsEqualTo(0);
        await Assert.That(second).IsEqualTo(first);
        await Assert.That(first).Contains(SpinnerStrip.WorkingFrames[0]);
    }

    [Test]
    public async Task AdvancedClock_Paint_Shows_Frame_For_Clock_Tick()
    {
        var screen = BuildScreen();
        var buffer = new ScreenBuffer(120, 8);
        screen.Tree.Solve(120, 8);
        using var clock = new AnimationClock();
        screen.Status.AnimationClock = clock;

        clock.Advance();
        clock.Advance();
        clock.Advance();
        string art = PaintAll(screen, buffer);

        await Assert.That(screen.Status.Tick).IsEqualTo(3);
        await Assert.That(art).Contains(SpinnerStrip.WorkingFrames[3]);
    }

    [Test]
    public async Task Detached_Panel_Keeps_Legacy_Per_Paint_Ticks()
    {
        var screen = BuildScreen();
        var buffer = new ScreenBuffer(120, 8);
        screen.Tree.Solve(120, 8);

        await Assert.That(screen.Status.AnimationClock).IsNull();
        PaintAll(screen, buffer);
        await Assert.That(screen.Status.Tick).IsEqualTo(1);
        PaintAll(screen, buffer);
        await Assert.That(screen.Status.Tick).IsEqualTo(2);
    }
}
