using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework;
using Harbor.Ui.Framework.Converters;

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
    public async Task Pill_Maps_All_States()
    {
        await Assert.That(StatusMappers.ToolCallStateToPill(ToolCallState.Running)).IsEqualTo("running");
        await Assert.That(StatusMappers.ToolCallStateToPill(ToolCallState.Success)).IsEqualTo("ok");
        await Assert.That(StatusMappers.ToolCallStateToPill(ToolCallState.Error)).IsEqualTo("err");
    }

    [Test]
    public async Task BrushKey_Maps_All_States()
    {
        await Assert.That(StatusMappers.ToolCallStateToBrushKey(ToolCallState.Running)).IsEqualTo("MochaYellow");
        await Assert.That(StatusMappers.ToolCallStateToBrushKey(ToolCallState.Success)).IsEqualTo("MochaGreen");
        await Assert.That(StatusMappers.ToolCallStateToBrushKey(ToolCallState.Error)).IsEqualTo("MochaRed");
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
        await Assert.That(block.Status).IsEqualTo(ToolCallState.Running);
        await Assert.That(block.StatusPill).IsEqualTo("running");
        await Assert.That(block.StatusBrushKey).IsEqualTo("MochaYellow");
    }

    [Test]
    public async Task Completed_Block_Pill_Ok_Brush_Green()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "read", "src/a.cs"));
        block.Complete(new ToolResultBody("body", isError: false, TimeSpan.FromMilliseconds(850)));
        await Assert.That(block.Status).IsEqualTo(ToolCallState.Success);
        await Assert.That(block.StatusPill).IsEqualTo("ok");
        await Assert.That(block.StatusBrushKey).IsEqualTo("MochaGreen");
    }

    [Test]
    public async Task Error_Block_Pill_Err_Brush_Red()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t2", "edit", ""));
        block.Complete(new ToolResultBody("boom", isError: true, TimeSpan.FromMilliseconds(5)));
        await Assert.That(block.Status).IsEqualTo(ToolCallState.Error);
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
        await Assert.That(block.Status).IsEqualTo(ToolCallState.Error);
        await Assert.That(block.Body.Value.Output).IsEqualTo("boom");
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

    // ── #567: the states the old `_ =>` arms invented as "running" ──────────

    [Test]
    public async Task Cancelled_Block_Does_Not_Render_As_Running()
    {
        // Before #567 a cancelled/timed-out call had no state to hold, so it fell
        // into `_ => Running` and the card spun forever. These are the assertions
        // that make that unrepresentable rather than merely unlikely.
        var block = new ToolCallBlock(new ToolCallInfo("t9", "bash", "sleep 999"));
        block.Stop(ToolCallState.Cancelled, "cancelled by user");

        await Assert.That(block.Status).IsEqualTo(ToolCallState.Cancelled);
        await Assert.That(block.Status.IsTerminal()).IsTrue();
        await Assert.That(block.StatusPill).IsEqualTo("cancelled");
        await Assert.That(block.StatusPill).IsNotEqualTo("running");
        await Assert.That(block.StatusBrushKey).IsNotEqualTo("MochaYellow");
        await Assert.That(block.StatusBrushKey).IsNotEqualTo(StatusMappers.ToolCallStateToBrushKey(ToolCallState.Running));
        await Assert.That(block.ViewModel.Status).IsEqualTo(ToolCallState.Cancelled);

        string art = PaintHeader(block);
        await Assert.That(art).DoesNotContain("⚙");
        await Assert.That(art).Contains("[cancelled]");
    }

    [Test]
    public async Task TimedOut_Block_Does_Not_Render_As_Running()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t10", "webfetch", "https://example.test"));
        block.Stop(ToolCallState.TimedOut);

        await Assert.That(block.Status).IsEqualTo(ToolCallState.TimedOut);
        await Assert.That(block.Status.IsTerminal()).IsTrue();
        await Assert.That(block.StatusPill).IsEqualTo("timeout");
        await Assert.That(block.StatusPill).IsNotEqualTo("running");
        await Assert.That(PaintHeader(block)).DoesNotContain("⚙");
    }

    [Test]
    public async Task Stop_NonTerminal_State_Throws()
    {
        // The one place a value arrives from outside the class, so it cannot be
        // routed through the throw-on-unnamed-domain switch the paint path
        // uses: guard it explicitly.
        var block = new ToolCallBlock(new ToolCallInfo("t11", "bash", "ls"));
        await Assert.That(() => block.Stop(ToolCallState.Running))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(block.Status).IsEqualTo(ToolCallState.Running);
    }

    [Test]
    public async Task Stop_After_Complete_Is_A_NoOp()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t12", "read", "a.cs"));
        block.Complete(new ToolResultBody("ok", isError: false, TimeSpan.FromMilliseconds(3)));
        block.Stop(ToolCallState.Cancelled, "late abort");
        await Assert.That(block.Status).IsEqualTo(ToolCallState.Success);
        await Assert.That(block.StatusPill).IsEqualTo("ok");
    }

    [Test]
    public async Task Complete_After_Stop_Does_Not_Resurrect()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t13", "bash", "sleep 999"));
        block.Stop(ToolCallState.Cancelled, "cancelled by user");
        block.Complete(new ToolResultBody("late", isError: false, TimeSpan.FromMilliseconds(9)));
        await Assert.That(block.Status).IsEqualTo(ToolCallState.Cancelled);
        await Assert.That(block.StatusPill).IsEqualTo("cancelled");
    }

    /// <summary>
    /// The paint half of the #567 guard. Every declared state must resolve to a
    /// glyph the card can actually paint, and a terminal one must never look
    /// live. The class-wide gate over the enum's full membership lives in
    /// <c>Harbor.Ui.Framework.Tests/ToolCallStateGuardTests</c>; this asserts the
    /// thing only a rendered card can prove — that the glyph reaches the cell
    /// grid.
    /// </summary>
    [Test]
    public async Task Every_State_Has_Pill_And_Brush_And_A_Painted_Glyph()
    {
        foreach (ToolCallState state in Enum.GetValues<ToolCallState>())
        {
            string pill = StatusMappers.ToolCallStateToPill(state);
            string brush = StatusMappers.ToolCallStateToBrushKey(state);

            await Assert.That(pill).IsNotEmpty().Because($"{state} needs a pill label");
            await Assert.That(pill).IsNotEqualTo("?").Because($"{state} fell into the old unknown-state arm");
            await Assert.That(brush).IsNotEmpty().Because($"{state} needs a brush key");

            var block = new ToolCallBlock(new ToolCallInfo($"g{(int)state}", "bash", "ls"));
            if (state.IsTerminal())
            {
                block.Stop(state, "terminal");
                await Assert.That(block.StatusPill).IsEqualTo(pill);
            }
            else
            {
                // Pending / Running are the only states that may present as live.
                await Assert.That(pill).IsNotEqualTo("cancelled");
                await Assert.That(pill).IsNotEqualTo("timeout");
            }

            string art = PaintHeader(block);
            await Assert.That(art).IsNotEmpty();
        }
    }

    [Test]
    public async Task Terminal_States_Are_Classified_Once()
    {
        foreach (ToolCallState state in Enum.GetValues<ToolCallState>())
        {
            bool expected = state is ToolCallState.Success or ToolCallState.Error
                or ToolCallState.Cancelled or ToolCallState.TimedOut;
            await Assert.That(state.IsTerminal()).IsEqualTo(expected).Because($"{state} classification drifted");
        }
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
