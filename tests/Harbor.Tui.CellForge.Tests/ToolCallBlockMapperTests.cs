using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Converters;
using VmToolCallStatus = Harbor.Ui.Framework.ViewModels.ToolCallStatus;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Covers the <c>StatusMappers</c> converters directly plus the
/// <see cref="ToolCallBlock"/> surface built on them (pill / brush /
/// duration). The Running header must stay byte-identical (no pill, no
/// duration column) — pinned here so
/// <c>ChatBlockTests.FormatDuration_HumanBuckets</c> and the
/// <c>StreamingTests</c> Running-header assertions keep passing.
/// </summary>
public class ToolCallBlockMapperTests
{
    private static string PaintHeader(ToolCallBlock block, int width = 40, int height = 6)
    {
        var buffer = new ScreenBuffer(width, height);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, width, height), 0));
        return GridDump.Art(buffer);
    }

    [Test]
    public async Task Pill_Maps_All_Phases()
    {
        await Assert.That(StatusMappers.ToolCallStatusToPill(VmToolCallStatus.Running)).IsEqualTo("running");
        await Assert.That(StatusMappers.ToolCallStatusToPill(VmToolCallStatus.Success)).IsEqualTo("ok");
        await Assert.That(StatusMappers.ToolCallStatusToPill(VmToolCallStatus.Error)).IsEqualTo("err");
    }

    [Test]
    public async Task BrushKey_Maps_All_Phases()
    {
        await Assert.That(StatusMappers.ToolCallStatusToBrushKey(VmToolCallStatus.Running)).IsEqualTo("MochaYellow");
        await Assert.That(StatusMappers.ToolCallStatusToBrushKey(VmToolCallStatus.Success)).IsEqualTo("MochaGreen");
        await Assert.That(StatusMappers.ToolCallStatusToBrushKey(VmToolCallStatus.Error)).IsEqualTo("MochaRed");
    }

    [Test]
    public async Task DurationToText_Hides_SubMillisecond()
    {
        await Assert.That(StatusMappers.DurationToText(TimeSpan.FromMicroseconds(400))).IsEqualTo(string.Empty);
        await Assert.That(StatusMappers.DurationToText(TimeSpan.Zero)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task DurationToText_Milliseconds_Bucket()
    {
        await Assert.That(StatusMappers.DurationToText(TimeSpan.FromMilliseconds(250))).IsEqualTo("250ms");
        await Assert.That(StatusMappers.DurationToText(TimeSpan.FromMilliseconds(850))).IsEqualTo("850ms");
    }

    [Test]
    public async Task DurationToText_Seconds_Bucket()
    {
        await Assert.That(StatusMappers.DurationToText(TimeSpan.FromSeconds(2.34))).IsEqualTo("2.3s");
    }

    [Test]
    public async Task Running_Block_Pill_And_Brush()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "bash", "ls -la"));
        await Assert.That(block.Status).IsEqualTo(ToolCallStatus.Running);
        await Assert.That(block.StatusPill).IsEqualTo("running");
        await Assert.That(block.StatusBrushKey).IsEqualTo("MochaYellow");
    }

    [Test]
    public async Task Completed_Block_Pill_Ok_Brush_Green()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "read", "src/a.cs"));
        block.Complete(new ToolResultBody("body", isError: false, TimeSpan.FromMilliseconds(850)));
        await Assert.That(block.Status).IsEqualTo(ToolCallStatus.Ok);
        await Assert.That(block.StatusPill).IsEqualTo("ok");
        await Assert.That(block.StatusBrushKey).IsEqualTo("MochaGreen");
    }

    [Test]
    public async Task Error_Block_Pill_Err_Brush_Red()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t2", "edit", ""));
        block.Complete(new ToolResultBody("boom", isError: true, TimeSpan.FromMilliseconds(5)));
        await Assert.That(block.Status).IsEqualTo(ToolCallStatus.Error);
        await Assert.That(block.StatusPill).IsEqualTo("err");
        await Assert.That(block.StatusBrushKey).IsEqualTo("MochaRed");
    }

    [Test]
    public async Task Running_Header_Has_No_Pill_Or_Duration()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "bash", "ls -la"));
        string art = PaintHeader(block);
        await Assert.That(art).Contains("⚙ bash");
        await Assert.That(art).DoesNotContain("[");
        await Assert.That(art).DoesNotContain("(");
    }

    [Test]
    public async Task Completed_Ok_Header_Shows_Duration_And_Pill()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "read", "src/a.cs"));
        block.Complete(new ToolResultBody("l1\n", isError: false, TimeSpan.FromMilliseconds(850)));
        string art = PaintHeader(block);
        await Assert.That(art).Contains("✔ read (850ms)");
        await Assert.That(art).Contains("[ok]");
    }

    [Test]
    public async Task Completed_Error_Header_Shows_Err_Pill()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t2", "edit", ""));
        block.Complete(new ToolResultBody("boom", isError: true, TimeSpan.FromMilliseconds(5)));
        string art = PaintHeader(block);
        await Assert.That(art).Contains("✖ edit (5ms)");
        await Assert.That(art).Contains("[err]");
    }

    [Test]
    public async Task Instant_Call_Hides_Duration_Column_Keeps_Pill()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t3", "read", "f.cs"));
        block.Complete(new ToolResultBody("body", isError: false, TimeSpan.Zero));
        string art = PaintHeader(block);
        await Assert.That(art).DoesNotContain("(");
        await Assert.That(art).Contains("[ok]");
    }

    [Test]
    public async Task SubMs_Call_Hides_Duration_But_Shim_Keeps_Lt1ms()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t4", "glob", "*.cs"));
        block.Complete(new ToolResultBody("hit", isError: false, TimeSpan.FromMicroseconds(400)));
        string art = PaintHeader(block);
        await Assert.That(art).DoesNotContain("(");
        await Assert.That(ToolResultBody.FormatDuration(TimeSpan.FromMicroseconds(400))).IsEqualTo("<1ms");
    }

    [Test]
    public async Task Complete_First_Result_Wins()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t2", "edit", ""));
        block.Complete(new ToolResultBody("boom", isError: true, TimeSpan.FromMilliseconds(5)));
        block.Complete(new ToolResultBody("second", isError: false, TimeSpan.FromMilliseconds(9)));
        await Assert.That(block.Status).IsEqualTo(ToolCallStatus.Error);
        await Assert.That(block.Body!.Output).IsEqualTo("boom");
        await Assert.That(block.StatusPill).IsEqualTo("err");
    }

    [Test]
    public async Task Lifecycle_Running_To_Ok_Transitions()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "bash", "make"));
        await Assert.That(block.StatusPill).IsEqualTo("running");
        await Assert.That(block.StatusBrushKey).IsEqualTo("MochaYellow");
        block.Complete(new ToolResultBody("ok", isError: false, TimeSpan.FromMilliseconds(12)));
        await Assert.That(block.StatusPill).IsEqualTo("ok");
        await Assert.That(block.StatusBrushKey).IsEqualTo("MochaGreen");
    }

    [Test]
    public async Task RawText_Instant_Call_Shows_Lt1ms()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "read", "f.cs"));
        block.Complete(new ToolResultBody("body", isError: false, TimeSpan.Zero));
        await Assert.That(block.RawText()).Contains("<1ms");
    }

    [Test]
    public async Task Measure_Running_Single_Line_Completed_Grows()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "read", "f.cs"));
        await Assert.That((block.Measure(40).MinLines, block.Measure(40).MaxLines)).IsEqualTo((1, 1));
        block.Complete(new ToolResultBody("l1\nl2\n", isError: false, TimeSpan.FromMilliseconds(3)));
        await Assert.That(block.Measure(40).MinLines).IsGreaterThan(1);
    }
}
