using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class GenericLiveRegionTests
{
    private static string Art(GenericLiveRegion region, int width = 20)
    {
        var buffer = new ScreenBuffer(width, Math.Max(1, region.DesiredHeight));
        region.Paint(buffer, new Rect(0, 0, width, buffer.Rows));
        return GridDump.Art(buffer).TrimEnd('\n');
    }

    [Test]
    public async Task Task_Ratio_ClampsAndGuardsUnknownTotal()
    {
        await Assert.That(new LiveProgressTask(5, 10).Ratio).IsEqualTo(0.5);
        await Assert.That(new LiveProgressTask(-2, 10).Ratio).IsEqualTo(0);
        await Assert.That(new LiveProgressTask(12, 10).Ratio).IsEqualTo(1);
        await Assert.That(new LiveProgressTask(1, 0).Ratio).IsEqualTo(0);
        await Assert.That(new LiveProgressTask(1, -4).Ratio).IsEqualTo(0);
        await Assert.That(new LiveProgressTask(double.NaN, 10).Ratio).IsEqualTo(0);
    }

    [Test]
    public async Task Task_Remaining_NeverNegative()
    {
        await Assert.That(new LiveProgressTask(3, 10).Remaining).IsEqualTo(7);
        await Assert.That(new LiveProgressTask(12, 10).Remaining).IsEqualTo(0);
        await Assert.That(new LiveProgressTask(3, 0).Remaining).IsEqualTo(0);
    }

    [Test]
    public async Task Task_Labels_ReadPercentAndCount()
    {
        await Assert.That(new LiveProgressTask(0, 10).PercentText).IsEqualTo("0%");
        await Assert.That(new LiveProgressTask(5, 10).PercentText).IsEqualTo("50%");
        await Assert.That(new LiveProgressTask(3, 10).CountText).IsEqualTo("3/10");
        await Assert.That(new LiveProgressTask(3, 0).CountText).IsEqualTo("3/?");
        await Assert.That(new LiveProgressTask(10, 10).IsCompleted).IsTrue();
        await Assert.That(new LiveProgressTask(9, 10).IsCompleted).IsFalse();
    }

    [Test]
    public async Task Region_ClipHead_KeepsNewest()
    {
        var region = new GenericLiveRegion { MaxHeight = 2, Overflow = LiveRegionOverflow.ClipHead };
        region.Update(["a", "b", "c"]);
        await Assert.That(region.VisibleLines()).IsEquivalentTo(["b", "c"]);
        await Assert.That(region.RenderPlainText()).IsEqualTo("b\nc");
    }

    [Test]
    public async Task Region_ClipTail_KeepsOldest()
    {
        var region = new GenericLiveRegion { MaxHeight = 2, Overflow = LiveRegionOverflow.ClipTail };
        region.Update(["a", "b", "c"]);
        await Assert.That(region.VisibleLines()).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task Region_Ellipsize_SpendsLastRowOnMarker()
    {
        var region = new GenericLiveRegion { MaxHeight = 2, Overflow = LiveRegionOverflow.Ellipsize };
        region.Update(["a", "b", "c"]);
        IReadOnlyList<string> visible = region.VisibleLines();
        await Assert.That(visible.Count).IsEqualTo(2);
        await Assert.That(visible[0]).IsEqualTo("a");
        await Assert.That(visible[1].Contains("more")).IsTrue();
    }

    [Test]
    public async Task Region_AutoClear_EmptiesOnComplete()
    {
        var clearing = new GenericLiveRegion { AutoClear = true };
        clearing.Update("hello");
        clearing.Complete();
        await Assert.That(clearing.LineCount).IsEqualTo(0);

        var keeping = new GenericLiveRegion { AutoClear = false };
        keeping.Update("hello");
        keeping.Complete();
        await Assert.That(keeping.LineCount).IsEqualTo(1);
    }

    [Test]
    public async Task Region_MultilineUpdate_SplitsAndIgnoresTrailingNewline()
    {
        var region = new GenericLiveRegion();
        region.Update("a\nb\n");
        await Assert.That(region.VisibleLines()).IsEquivalentTo(["a", "b"]);
        await Assert.That(region.DesiredHeight).IsEqualTo(2);
    }

    [Test]
    public async Task Columns_Bar_MatchesProgressMath()
    {
        string bar = LiveColumns.Bar(new LiveProgressTask(5, 10), 10);
        await Assert.That(bar).IsEqualTo(new string(LiveColumns.Filled, 5) + new string(LiveColumns.Track, 5));
        await Assert.That(LiveColumns.Bar(new LiveProgressTask(0, 10), 4)).IsEqualTo(new string(LiveColumns.Track, 4));
        await Assert.That(LiveColumns.Bar(new LiveProgressTask(1, 10), 0)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Columns_Join_DropsEmpty()
    {
        var task = new LiveProgressTask(3, 10);
        string line = LiveColumns.Join(" ", LiveColumns.Spinner(0), string.Empty, LiveColumns.Percent(task));
        await Assert.That(line.Contains("30%")).IsTrue();
        await Assert.That(line.Contains("  ")).IsFalse();
        await Assert.That(LiveColumns.Spinner(0)).IsEqualTo("⠋");
        await Assert.That(LiveColumns.Spinner(10)).IsEqualTo("⠋");
    }

    [Test]
    public async Task Region_Paint_ShowsVisibleWindow()
    {
        var region = new GenericLiveRegion { MaxHeight = 2, Overflow = LiveRegionOverflow.ClipHead };
        region.Update(["a", "b", "c"]);
        string art = Art(region);
        await Assert.That(art.Contains("b")).IsTrue();
        await Assert.That(art.Contains("c")).IsTrue();
        await Assert.That(art.Contains("a")).IsFalse();
    }
}
