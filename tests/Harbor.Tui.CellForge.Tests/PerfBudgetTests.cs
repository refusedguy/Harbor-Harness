using System.Diagnostics;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// CE-3 final budgets (sprint goal: frame &lt; 16 ms with the feed, 0
/// steady-state allocations). Allocation numbers are hard assertions
/// (thread-scoped counter, immune to parallel traffic); frame times are
/// reported and guarded by a generous ceiling to catch pathological
/// regressions without flaking on slow CI.
/// </summary>
public class PerfBudgetTests
{
    [Test]
    public async Task TimelineFrame_SteadyState_IsAllocationFree()
    {
        var buffer = new ScreenBuffer(80, 24);
        var tl = new VirtualizedChatTimeline();
        for (int i = 0; i < 40; i++)
        {
            tl.Append(new UserBlock($"message number {i} with a few words to wrap around"));
        }

        _ = tl.PrepareFrame(80, 20);
        tl.Paint(buffer, new Rect(0, 0, 80, 20));

        // Warm past JIT tier-up thresholds: tier transitions charge one-off
        // runtime bookkeeping bytes to the measuring thread, masking the true
        // steady-state number.
        for (int w = 0; w < 100_000; w++)
        {
            _ = tl.PrepareFrame(80, 20);
            tl.Paint(buffer, new Rect(0, 0, 80, 20));
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int frames = 2_000;
        for (int f = 0; f < frames; f++)
        {
            _ = tl.PrepareFrame(80, 20);
            tl.Paint(buffer, new Rect(0, 0, 80, 20));
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task StreamingPushRender_SteadyTail_IsAllocationFree_AfterFreeze()
    {
        // Frozen document + empty tail: repeated renders must not allocate.
        var renderer = new Harbor.Ui.Framework.Rendering.Markdown.StreamingMarkdownRenderer();
        renderer.Push("# heading\n\nparagraph **with** inline `styles`.\n");
        renderer.Complete();
        _ = renderer.RenderTail(60);

        for (int w = 0; w < 100_000; w++)
        {
            _ = renderer.RenderTail(60);
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int iterations = 5_000;
        for (int i = 0; i < iterations; i++)
        {
            _ = renderer.RenderTail(60);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task StreamingPushRender_LiveTail_RepeatFrameRender_IsAllocationFree()
    {
        // #463: the frozen-tail moat above calls Complete(), so it always hit
        // the early return in StreamingMarkdownRenderer and never measured the
        // LIVE path. This is the live case: an open paragraph that never
        // freezes, re-rendered twice per frame (Measure + Paint) exactly the
        // way StreamingMarkdownBlock issues it.
        var renderer = new Harbor.Ui.Framework.Rendering.Markdown.StreamingMarkdownRenderer();
        renderer.Push("# heading\n\nan open paragraph that never freezes because no blank line follows it yet\n");

        // The first render at this width does the real work; every repeat
        // inside a frame is a no-op.
        _ = renderer.RenderTail(60);

        for (int w = 0; w < 100_000; w++)
        {
            _ = renderer.RenderTail(60);
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int frames = 5_000;
        for (int i = 0; i < frames; i++)
        {
            _ = renderer.RenderTail(60); // Measure
            _ = renderer.RenderTail(60); // Paint — same frame, same source
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Guard the premise: this must stay the LIVE (unfrozen) path.
        await Assert.That(renderer.IsComplete).IsFalse();
        await Assert.That(renderer.FrozenLineCount).IsLessThan(renderer.LineCount);
        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task StreamingPushRender_LiveTail_PushCostIsIndependentOfTailLength()
    {
        // #463 acceptance: a paragraph with no blank line never freezes, so the
        // tail IS the whole message. Before the fix every push re-parsed and
        // re-rendered that entire tail — ~10 allocations per line, O(N) per
        // push, O(N²) per answer. Each push below completes exactly one line,
        // so the cost must be O(1) and flat in the message length.
        static long PushCostBytes(int lines)
        {
            var source = new System.Text.StringBuilder();
            for (int i = 0; i < lines; i++)
            {
                source.Append("word ").Append(i).Append('\n');
            }

            var renderer = new Harbor.Ui.Framework.Rendering.Markdown.StreamingMarkdownRenderer();
            renderer.Push(source.ToString());
            _ = renderer.RenderTail(60);

            // Warm past JIT tier-up thresholds.
            for (int w = 0; w < 2_000; w++)
            {
                renderer.Push("y\n");
                _ = renderer.RenderTail(60);
            }

            GC.WaitForPendingFinalizers();
            long before = GC.GetAllocatedBytesForCurrentThread();

            const int pushes = 500;
            for (int i = 0; i < pushes; i++)
            {
                renderer.Push("y\n");
                _ = renderer.RenderTail(60);
            }

            return (GC.GetAllocatedBytesForCurrentThread() - before) / pushes;
        }

        long small = PushCostBytes(8);
        long large = PushCostBytes(800);
        Console.WriteLine($"#463 live-tail push cost: 8 lines = {small} B/push, 800 lines = {large} B/push");

        // 100x the message must not cost meaningfully more per push. The flat
        // slack keeps the assertion honest if the per-push floor ever lands at
        // zero (fully inlined span work), while still failing hard on the old
        // ~10-allocations-per-line behaviour.
        await Assert.That(large).IsLessThan(small * 4 + 2048);
    }

    [Test]
    public async Task StatusAndSpinner_SteadyState_AllocationFree()
    {
        var vm = new StatusViewModel { Model = "kilocode/hy3", Mode = StatusBarMode.Running };
        vm.SetContext(4300, 10_000);
        vm.SetUsage(12_400, 5_200, 0.0021m);
        var workspace = new StatusSeg[12];

        var buffer = new ScreenBuffer(80, 1);
        var panelRect = new Rect(0, 0, 80, 1);

        _ = vm.BuildSegments(workspace);
        StatusBarWidget.Paint(buffer, panelRect, workspace.AsSpan()[..5]);

        for (int w = 0; w < 100_000; w++)
        {
            int wn = vm.BuildSegments(workspace);
            Span<StatusSeg> wspan = workspace;
            int wkept = StatusBarLayout.Fit(wspan[..wn], 79);
            StatusBarWidget.Paint(buffer, panelRect, wspan[..wkept]);
            _ = SpinnerStrip.Frame(w, SpinnerRhythm.Working);
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int frames = 10_000;
        for (int f = 0; f < frames; f++)
        {
            int n = vm.BuildSegments(workspace);
            Span<StatusSeg> span = workspace;
            int kept = StatusBarLayout.Fit(span[..n], 79);
            StatusBarWidget.Paint(buffer, panelRect, span[..kept]);
            _ = SpinnerStrip.Frame(f, SpinnerRhythm.Working);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task FrameTime_WithFeed_UnderBudget()
    {
        var session = new ScreenSession(new AnsiWriter(new RecordingBackend(), syncUpdates: true), 100, 30);
        var screen = ChatScreen.Build(new ComposerController(), new StatusViewModel { Model = "m" });
        var tl = screen.Timeline.Timeline;

        // A realistic feed: mixed block types.
        for (int i = 0; i < 25; i++)
        {
            tl.Append(new UserBlock($"user prompt {i} asking something reasonably long"));
            tl.Append(new AssistantMarkdownBlock($"## Answer {i}\nText with **bold** and `code`.\n- point a\n- point b\n"));
            tl.Append(new ToolCallBlock(new ToolCallInfo($"t{i}", "read", $"{{\"path\":\"src/f{i}.cs\"}}")));
        }
        tl.Append(new DiffBlock("--- a/x.cs\n+++ b/x.cs\n@@ -1,3 +1,4 @@\n ctx\n-old\n+new\n ctx2"));

        // Warmup.
        screen.Tree.Solve(session.CurrentCols, session.CurrentRows);
        _ = tl.PrepareFrame(100, screen.Timeline.Rect.Height);
        foreach (var p in screen.Tree.Panels)
        {
            p.Paint(session.PaintBuffer);
        }
        await session.FlushFrameAsync();

        const int frames = 300;
        var sw = Stopwatch.StartNew();
        for (int f = 0; f < frames; f++)
        {
            screen.Tree.Solve(session.CurrentCols, session.CurrentRows);
            _ = tl.PrepareFrame(100, screen.Timeline.Rect.Height);
            foreach (var p in screen.Tree.Panels)
            {
                p.Paint(session.PaintBuffer);
            }

            session.BeginFrame();
            await session.FlushFrameAsync();
        }

        sw.Stop();
        double avgMs = sw.Elapsed.TotalMilliseconds / frames;
        Console.WriteLine($"ce3-frame-avg: {avgMs:F3} ms over {frames} frames (budget 16 ms)");

        // Report the actual measurement; guard against pathological regressions only.
        await Assert.That(avgMs).IsLessThan(16.0 * 4);
    }
}
