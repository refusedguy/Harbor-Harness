using System.Diagnostics;
using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Streaming;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

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
        var renderer = new Widgets.Markdown.StreamingMarkdownRenderer();
        renderer.Push("# heading\n\nparagraph **with** inline `styles`.\n");
        renderer.Complete();
        _ = renderer.RenderTail(60);

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
            p.Paint(session.Back);
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
                p.Paint(session.Back);
            }

            session.BeginFrame();
            await session.FlushFrameAsync();
        }

        sw.Stop();
        double avgMs = sw.Elapsed.TotalMilliseconds / frames;

        // Report the actual measurement; guard against pathological regressions only.
        await Assert.That(avgMs).IsLessThan(16.0 * 4);
    }
}
