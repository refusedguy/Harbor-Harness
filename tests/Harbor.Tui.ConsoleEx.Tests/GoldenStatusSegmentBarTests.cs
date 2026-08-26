using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// CE-3 widget goldens: status rows at several widths painted through the
/// real DiffEngine into a recording backend; three-layer dump compared against
/// tests/fixtures/celldiff/ce3-status-*.golden.txt.
/// </summary>
public class GoldenStatusSegmentBarTests
{
    [Test]
    public async Task StatusRow_Widths_80_and_24_Golden()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var engine = new DiffEngine(80, 2);
        var back = new ScreenBuffer(80, 2);

        var vm = new StatusViewModel
        {
            Model = "kilocode/tencent/hy3:free",
            Mode = StatusBarMode.Running,
        };
        vm.SetContext(7200, 10_000);
        vm.SetUsage(120_000, 45_500, 0.0042m);

        var ws = new StatusSeg[8];
        int n80 = vm.BuildSegments(ws);
        int kept80 = StatusBarLayout.Fit(ws.AsSpan()[..n80], 80);
        StatusBarWidget.Paint(back, new Rect(0, 0, 80, 1), ws.AsSpan()[..kept80]);

        // Second row squeezed hard — flexible segments gone, ctx bar truncated.
        var ws2 = new StatusSeg[8];
        int n24 = vm.BuildSegments(ws2);
        int kept24 = StatusBarLayout.Fit(ws2.AsSpan()[..n24], 24);
        StatusBarWidget.Paint(back, new Rect(0, 1, 24, 1), ws2.AsSpan()[..kept24]);

        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        string doc = GoldenDoc.Build("ce3-status-widths", back, backend);
        string expected = Golden.Verify("ce3-status-widths", doc, GridDump.ToSvg(back));
        await Assert.That(doc).IsEqualTo(expected);
        await Assert.That(engine.FrontMatches(back)).IsTrue();
    }
}
