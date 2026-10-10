using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Ambient mascot footer wiring (sprint UI-V2 P6.1): the status panel frames
/// the mascot at the trailing edge on wide rows only — narrow rows keep the
/// full width for status segments. Deterministic ticks, no wall clock
/// (the sleeping mood needs 60s of idle uptime and is not asserted here).
/// </summary>
public class StatusPanelMascotTests
{
    /// <summary>
    /// Frames the rewritten latch tests paint with the clock held still
    /// (#1026): 100 past the legacy 150-frame budget, so a frame-keyed latch
    /// (the #170 bug) has expired while zero milliseconds have elapsed on
    /// the injected clock and a time latch must still hold. Same 250 the
    /// <c>Latch_Expires_ByTime_NotByTicks</c> test asserts at.
    /// </summary>
    private const int FramesPastBudget = 250;

    /// <summary>
    /// Manual clock (#1026): the same shape <c>MascotReviveTests</c> owns for
    /// <c>MascotDirector</c> (#1007) — one timestamp tick is one millisecond,
    /// so every deadline is exact integer arithmetic. Threaded through
    /// <c>ChatScreen.Build</c> into both mascot directors and never advanced
    /// here, so a latch assertion is a function of declared (zero) time and
    /// not of how fast 250 paints finish on the runner.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => ElapsedMs;

        /// <summary>Milliseconds this clock has been advanced by the test.</summary>
        public long ElapsedMs { get; private set; }
    }

    private static string PaintStatusRow(int cols, StatusBarMode mode)
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = mode };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false);
        var buffer = new ScreenBuffer(cols, 8);
        screen.Tree.Solve(cols, 8);
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(buffer);
        }

        return GridDump.Art(buffer);
    }

    [Test]
    public async Task WideRow_Running_ShowsWorkingMascotAtTrailingEdge()
    {
        string art = PaintStatusRow(120, StatusBarMode.Running);

        // First paint → Tick = 1 → WorkingFrames[1].
        await Assert.That(art).Contains(AmbientMascot.WorkingFrames[1]);
    }

    [Test]
    public async Task WideRow_AwaitingApproval_ShowsAwaitingMascot()
    {
        string art = PaintStatusRow(120, StatusBarMode.AwaitingApproval);

        await Assert.That(art).Contains(AmbientMascot.AwaitingFrames[1]);
    }

    [Test]
    public async Task NarrowRow_NeverPaintsMascot()
    {
        foreach (var mode in Enum.GetValues<StatusBarMode>())
        {
            string art = PaintStatusRow(72, mode);
            foreach (var frame in AmbientMascot.WorkingFrames
                         .Concat(AmbientMascot.AwaitingFrames)
                         .Concat(AmbientMascot.IdleFrames)
                         .Concat(AmbientMascot.SleepingFrames))
            {
                await Assert.That(art.Contains(frame)).IsFalse();
            }
        }
    }

    [Test]
    public async Task WideRow_Idle_ShowsIdleMascot_BeforeSleepThreshold()
    {
        string art = PaintStatusRow(120, StatusBarMode.Idle);

        await Assert.That(art).Contains(AmbientMascot.IdleFrames[1]);
    }

    [Test]
    public async Task WideRow_ThinkingPhase_ShowsThinkingMascot()
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Running, Phase = AgentPhase.Thinking };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false);
        var buffer = new ScreenBuffer(120, 8);
        screen.Tree.Solve(120, 8);

        string art = PaintFrames(screen, buffer, 4);
        await Assert.That(art).Contains(AmbientMascot.ThinkingFrames[1]);
        await Assert.That(art).Contains(AmbientMascot.ThinkingFrames[3]);
    }

    [Test]
    public async Task WideRow_ToolCallPhase_ShowsToolCallMascot()
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Running, Phase = AgentPhase.ToolCall };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false);
        var buffer = new ScreenBuffer(120, 8);
        screen.Tree.Solve(120, 8);

        string art = PaintFrames(screen, buffer, 4);
        await Assert.That(art).Contains(AmbientMascot.ToolCallFrames[1]);
        await Assert.That(art).Contains(AmbientMascot.ToolCallFrames[2]);
    }

    [Test]
    public async Task ErroredPhase_Latch_IsTimeBased_HoldsAcrossFastFrames()
    {
        var clock = new ManualTimeProvider();
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Idle, Phase = AgentPhase.Errored };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false, timeProvider: clock);
        var buffer = new ScreenBuffer(120, 8);
        screen.Tree.Solve(120, 8);

        string first = PaintLastFrame(screen, buffer, 1);
        await Assert.That(first).Contains(AmbientMascot.ErrorFrames[1]);

        // Frozen clock (#1026): 250 paints run 100 frames past the old
        // 150-frame budget with zero milliseconds elapsed, so a time latch
        // (#170) must still hold. The old comment's race — "150 rapid paints
        // take milliseconds, far below the ~12 s latch" — assumed the runner
        // wins; now there is nothing to win, and a frame-keyed latch fails
        // exactly here.
        string fast = PaintLastFrame(screen, buffer, FramesPastBudget);
        await Assert.That(fast).Contains(AmbientMascot.ErrorFrames[(1 + FramesPastBudget) % AmbientMascot.ErrorFrames.Length]);
    }

    [Test]
    public async Task SucceededPhase_Latch_IsTimeBased_HoldsAcrossFastFrames()
    {
        var clock = new ManualTimeProvider();
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Idle, Phase = AgentPhase.Succeeded };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false, timeProvider: clock);
        var buffer = new ScreenBuffer(120, 8);
        screen.Tree.Solve(120, 8);

        string first = PaintLastFrame(screen, buffer, 1);
        await Assert.That(first).Contains(AmbientMascot.SuccessFrames[1]);

        // Frozen clock (#1026): same 250-frames-past-budget hold as the
        // errored phase above — the success latch is the same wall-clock
        // mechanism, and a frame-keyed latch fails exactly here.
        string fast = PaintLastFrame(screen, buffer, FramesPastBudget);
        await Assert.That(fast).Contains(AmbientMascot.SuccessFrames[(1 + FramesPastBudget) % AmbientMascot.SuccessFrames.Length]);
    }

    /// <summary>Paints all panels <paramref name="frames" /> times, returning the concatenated art.</summary>
    private static string PaintFrames(ChatScreen screen, ScreenBuffer buffer, int frames)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < frames; i++)
        {
            foreach (var panel in screen.Tree.Panels)
            {
                panel.Paint(buffer);
            }

            sb.Append(GridDump.Art(buffer));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Paints all panels <paramref name="frames" /> times, returning only the LAST frame's art.</summary>
    private static string PaintLastFrame(ChatScreen screen, ScreenBuffer buffer, int frames)
    {
        for (int i = 0; i < frames - 1; i++)
        {
            foreach (var panel in screen.Tree.Panels)
            {
                panel.Paint(buffer);
            }
        }

        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(buffer);
        }

        return GridDump.Art(buffer);
    }
}
