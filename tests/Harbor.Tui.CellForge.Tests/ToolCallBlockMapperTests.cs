using Harbor.TestKit;
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

    // #1810: the two mapper tables are ARRAYS, and the array is the whole
    // truth about which members exist: the rows come from it, and the guard
    // below proves it names every member of the enum. Before, each table
    // named three of the six — Pending, Cancelled and TimedOut had no row
    // here at all, so a change to what those states paint was not caught by
    // this file.

    /// <summary>The ToolCallState table of pill labels.</summary>
    public static (string Name, ToolCallState State, string Expected)[] ToolCallStatePillCases =>
    [
        ("Pill_Maps_All_States_Pending", ToolCallState.Pending, "pending"),
        ("Pill_Maps_All_States_Running", ToolCallState.Running, "running"),
        ("Pill_Maps_All_States_Success", ToolCallState.Success, "ok"),
        ("Pill_Maps_All_States_Error", ToolCallState.Error, "err"),
        ("Pill_Maps_All_States_Cancelled", ToolCallState.Cancelled, "cancelled"),
        ("Pill_Maps_All_States_TimedOut", ToolCallState.TimedOut, "timeout"),
    ];

    [Test]
    [MethodDataSource(nameof(ToolCallStatePillCases))]
    public async Task Pill_Maps_All_States(string name, ToolCallState state, string expectedPill)
    {
        await Assert.That(StatusMappers.ToolCallStateToPill(state)).IsEqualTo(expectedPill)
            .Because($"{name} must hold for {state}");
    }

    /// <summary>The ToolCallState table of brush keys.</summary>
    public static (string Name, ToolCallState State, string Expected)[] ToolCallStateBrushCases =>
    [
        ("BrushKey_Maps_All_States_Pending", ToolCallState.Pending, "MochaOverlay2"),
        ("BrushKey_Maps_All_States_Running", ToolCallState.Running, "MochaYellow"),
        ("BrushKey_Maps_All_States_Success", ToolCallState.Success, "MochaGreen"),
        ("BrushKey_Maps_All_States_Error", ToolCallState.Error, "MochaRed"),
        ("BrushKey_Maps_All_States_Cancelled", ToolCallState.Cancelled, "MochaOverlay1"),
        ("BrushKey_Maps_All_States_TimedOut", ToolCallState.TimedOut, "MochaRed"),
    ];

    [Test]
    [MethodDataSource(nameof(ToolCallStateBrushCases))]
    public async Task BrushKey_Maps_All_States(string name, ToolCallState state, string expectedBrush)
    {
        await Assert.That(StatusMappers.ToolCallStateToBrushKey(state)).IsEqualTo(expectedBrush)
            .Because($"{name} must hold for {state}");
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

    /// <summary>
    /// #1810: the totality check for the ToolCallState pill table. The array
    /// above names every member of the enum, and this says so — a member
    /// added to <c>ToolCallState</c> has no row until it gets one.
    /// </summary>
    /// <remarks>
    /// The guard above walks <c>Enum.GetValues</c> over the PRODUCT and
    /// proves every state paints something. This one walks the TABLE and
    /// proves every state is NAMED. A table that forgot a member would leave
    /// the first guard green and this one red.
    /// </remarks>
    [Test]
    public async Task Every_State_Has_A_Row_In_The_Pill_Table()
    {
        await Assert.That(ToolCallStatePillCases.Length).IsGreaterThan(0).Because("the table has rows to check");
        Exhaustive.AssertAllCases<ToolCallState>(ToolCallStatePillCases.Select(c => c.State).ToHashSet(), "every ToolCallState must have a row in the pill table");
    }

    /// <summary>
    /// #1810: the totality check for the ToolCallState brush table.
    /// </summary>
    [Test]
    public async Task Every_State_Has_A_Row_In_The_Brush_Table()
    {
        await Assert.That(ToolCallStateBrushCases.Length).IsGreaterThan(0).Because("the table has rows to check");
        Exhaustive.AssertAllCases<ToolCallState>(ToolCallStateBrushCases.Select(c => c.State).ToHashSet(), "every ToolCallState must have a row in the brush table");
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
    public async Task RawText_Completed_Call_ExactShape()
    {
        // #1097: pins the RawText shape (glyph, name, arrow, outcome,
        // duration) that the span rewrite must preserve byte-for-byte.
        var block = new ToolCallBlock(new ToolCallInfo("t1", "read", "f.cs"));
        block.Complete(new ToolResultBody("body", isError: false, TimeSpan.FromMilliseconds(250)));
        await Assert.That(block.RawText()).IsEqualTo("✔ read → ok 250ms");
    }

    [Test]
    public async Task RawText_Running_Call_HasNoOutcome()
    {
        // #1097: the no-body branch stays header-only (no arrow, no outcome).
        var block = new ToolCallBlock(new ToolCallInfo("t1", "read", "f.cs"));
        await Assert.That(block.RawText()).IsEqualTo("⚙ read");
    }

    [Test]
    public async Task ToolCallBlock_UsesNoHeapBuilder_RelativeGate()
    {
        // #1097 (relative gate, one site): ToolCallBlock.cs must compose its
        // plain-text line without a heap builder — zero `new StringBuilder`
        // in this file. Span-based (interpolated handler), not pooled, on
        // purpose: Harbor.Extensions is not on this assembly's matrix row, so
        // Presentation renders without the Application-layer pool. Scoped to
        // this file: TableBlock, OnboardingFlow, ... keep their own slices.
        string path = FindRepoFile(Path.Combine("src", "Harbor.Tui.CellForge", "Chat", "Widgets", "ToolCallBlock.cs"));
        string source = await File.ReadAllTextAsync(path);
        await Assert.That(source.Contains("new StringBuilder")).IsFalse();
    }

    /// <summary>Locate a repo-relative source file by walking up from the test assembly directory.</summary>
    private static string FindRepoFile(string relativePath)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new FileNotFoundException($"Repo file not found: {relativePath}");
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
