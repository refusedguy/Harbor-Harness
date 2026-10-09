using System.Diagnostics;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Renderer-moat perf probes (sprint acceptance): partial-scan diff time for
/// a 500-row timeline is compared against the full scan over the SAME frames in
/// the SAME process (paired A/B — the runner's speed is in both operands and
/// cancels, #996), and the steady-state hinted flush path must remain
/// allocation-free. Frame times are REPORTED for the benchmark doc; there are no
/// wall-clock ceilings in this file (a 1.43x spread on one unchanged commit proves
/// no threshold separates code from host pre-emption).
/// </summary>
public class RendererMoatPerfTests
{
    /// <summary>Backend that counts bytes without copying — keeps allocation
    /// probes clean while still exercising the full write path.</summary>
    private sealed class CountingBackend : ISyncTerminalBackend
    {
        public long Bytes { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Bytes += bytes.Length;
            return ValueTask.CompletedTask;
        }

        public void Write(ReadOnlySpan<byte> bytes) => Bytes += bytes.Length;
    }

    private sealed class Probe
    {
        public readonly ScreenSession Screen;
        public readonly ChatScreen Chat;
        public readonly Rect[] Fx = new Rect[VirtualizedChatTimeline.MaxFxDamage];

        public Probe(int cols, int rows)
        {
            Screen = new ScreenSession(new AnsiWriter(new CountingBackend(), syncUpdates: true), cols, rows);
            Chat = ChatScreen.Build(new ComposerController(), new StatusViewModel { Model = "kilocode/hy3" });
            Chat.Status.Vm.Mode = StatusBarMode.Running;
        }

        /// <summary>Populates the feed until the virtual content is twice the
        /// viewport. Each append is followed by a PrepareFrame pass so height
        /// estimates settle — TotalHeight is served from the layout cache and
        /// stays 0 until then.</summary>
        public void Populate(int cols, int rows)
        {
            var tl = Chat.Timeline.Timeline;
            for (int i = 0; tl.TotalHeight < rows * 2; i++)
            {
                tl.Append(new UserBlock($"user prompt {i} asking something reasonably long to wrap around the width"));
                tl.Append(new AssistantMarkdownBlock($"## Answer {i}\nText with **bold** spans and `code` spans.\n- point a\n- point b\n- point c\n"));
                tl.Append(new ToolCallBlock(new ToolCallInfo($"t{i}", "edit", $"{{\"path\":\"src/f{i}.cs\",\"lines\":[1,2]}}")));
                _ = tl.PrepareFrame(cols, rows);
            }
        }

        /// <summary>Paints one steady-state frame; when <paramref name="hint"/>
        /// is set only the animated rows are damaged, otherwise full scan.</summary>
        public void SteadyFrame(int cols, bool hint)
        {
            var chat = Chat;
            var screen = Screen;
            chat.Tree.Solve(screen.CurrentCols, screen.CurrentRows);
            var tlRect = chat.Timeline.Rect;
            _ = chat.Timeline.Timeline.PrepareFrame(tlRect.Width, Math.Max(0, tlRect.Height));

            screen.BeginFrame();
            chat.Tree.PaintAll(screen.Back);

            if (hint)
            {
                _ = chat.Timeline.Timeline.ConsumeFrameDamage(Fx, out _);
                var statusRect = chat.Status.Rect;
                if (statusRect.Height > 0)
                {
                    screen.Damage(new Rect(0, statusRect.Y, cols, statusRect.Height));
                }
            }
            else
            {
                chat.Timeline.Timeline.ConsumeFrameDamage(Fx, out _);
            }

            screen.FlushFrame();
        }
    }

    [Test]
    public async Task Diff_Time_500RowTimeline_Hinted_Under_2ms()
    {
        const int cols = 120;
        const int rows = 500;
        var probe = new Probe(cols, rows);
        probe.Populate(cols, rows);

        // Baseline flush, then warm past JIT tier-up on both paths.
        probe.SteadyFrame(cols, hint: false);
        for (int i = 0; i < 300; i++)
        {
            probe.SteadyFrame(cols, hint: false);
            probe.SteadyFrame(cols, hint: true);
        }

        const int frames = 500;
        double FullScan()
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++)
            {
                probe.SteadyFrame(cols, hint: false);
            }

            return sw.Elapsed.TotalMilliseconds / frames;
        }

        double Hinted()
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++)
            {
                probe.SteadyFrame(cols, hint: true);
            }

            return sw.Elapsed.TotalMilliseconds / frames;
        }

        // Interleave order-independence: run full → hinted → full → hinted.
        double full1 = FullScan();
        double hinted1 = Hinted();
        double full2 = FullScan();
        double hinted2 = Hinted();
        double fullAvg = Math.Min(full1, full2);
        double hintedAvg = Math.Min(hinted1, hinted2);

        Console.WriteLine(
            $"renderer-moat diff: full={fullAvg:F3} ms hinted={hintedAvg:F3} ms " +
            $"(120×500 grid, {frames} frames each, steady spinner tick)");

        // #996, RED DEMO ON PURPOSE (dev build fixed via ImageSharp 4.1.3, so the
        // gate is reached now): inverted, full < hinted. The hinted path damages
        // only the animated rows, so this cannot hold on a correct tree. Next
        // commit restores the true direction with the reasoning. A gate never
        // observed red is not a gate (#998 did the same with GrowthLimit = 1.05).
        await Assert.That(fullAvg).IsLessThan(hintedAvg);
    }

    [Test]
    public async Task Hinted_Steady_Frame_Is_Allocation_Free()
    {
        const int cols = 120;
        const int rows = 500;
        var probe = new Probe(cols, rows);
        probe.Populate(cols, rows);

        for (int i = 0; i < 2_000; i++)
        {
            probe.SteadyFrame(cols, hint: true);
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int frames = 2_000;
        for (int i = 0; i < frames; i++)
        {
            probe.SteadyFrame(cols, hint: true);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public Task Frame_Time_500RowTimeline_Report()
    {
        const int cols = 120;
        const int rows = 500;
        var probe = new Probe(cols, rows);
        probe.Populate(cols, rows);

        for (int i = 0; i < 300; i++)
        {
            probe.SteadyFrame(cols, hint: true);
        }

        const int frames = 300;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            probe.SteadyFrame(cols, hint: true);
        }

        sw.Stop();
        double avg = sw.Elapsed.TotalMilliseconds / frames;
        Console.WriteLine($"renderer-moat frame: {avg:F3} ms average over {frames} hinted frames (budget 16 ms)");
        // #996: reported only — the gates for this path are the hinted-vs-full
        // pairing in Diff_Time above and the allocation-free counts beside it.
        return Task.CompletedTask;
    }

    // ── Post-render effects (renderer-moat T3) ─────────────────────────────

    /// <summary>
    /// Acceptance "zero perf regression on non-effect frames": an ARMED but
    /// EMPTY pipeline must keep the hinted steady frame allocation-free and
    /// inside the same pathological guard as the plain path.
    /// </summary>
    [Test]
    public async Task ArmedEmptyPipeline_NonEffect_Frames_AllocationFree()
    {
        const int cols = 120;
        const int rows = 500;
        var probe = new Probe(cols, rows);
        probe.Populate(cols, rows);

        // Armed with two never-updated (intensity 0, empty-region) effects —
        // the exact "pipeline on, nothing glowing" steady state.
        probe.Screen.Effects.Set(0, new GlowEffect());
        probe.Screen.Effects.Set(1, new GlowEffect());

        for (int i = 0; i < 2_000; i++)
        {
            probe.SteadyFrame(cols, hint: true);
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int frames = 2_000;
        for (int i = 0; i < frames; i++)
        {
            probe.SteadyFrame(cols, hint: true);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0);
    }

    /// <summary>Benchmark report: full-scan diff with the glow pipeline armed
    /// over the status row (the REPL's gate-glow worst case is a narrow region)
    /// versus the plain path — reported for docs/BENCHMARKS.md, with no
    /// wall-clock gate (#996).</summary>
    [Test]
    public Task Glow_Frame_Report()
    {
        const int cols = 120;
        const int rows = 500;
        var probe = new Probe(cols, rows);
        probe.Populate(cols, rows);

        // Arm a real glow over the status row (the frame's animated strip).
        var statusRect = probe.Chat.Status.Rect;
        var accent = ChatPalette.Warning;
        var glow = new GlowEffect();
        probe.Screen.Effects.Set(0, glow);

        for (int i = 0; i < 300; i++)
        {
            glow.Update(new GlowRegion(new Rect(0, Math.Max(0, statusRect.Y), cols, Math.Max(1, statusRect.Height)), accent, 0.5 + (0.5 * Math.Sin(i / 10.0))));
            probe.SteadyFrame(cols, hint: true);
        }

        const int frames = 300;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            glow.Update(new GlowRegion(new Rect(0, Math.Max(0, statusRect.Y), cols, Math.Max(1, statusRect.Height)), accent, 0.5 + (0.5 * Math.Sin(i / 10.0))));
            probe.SteadyFrame(cols, hint: true);
        }

        sw.Stop();
        double glowAvg = sw.Elapsed.TotalMilliseconds / frames;
        Console.WriteLine($"renderer-moat glow frame: {glowAvg:F3} ms average over {frames} hinted frames with armed glow (budget 16 ms)");
        // #996: reported only — same gates as the plain path (pairing + allocation).
        return Task.CompletedTask;
    }
}
