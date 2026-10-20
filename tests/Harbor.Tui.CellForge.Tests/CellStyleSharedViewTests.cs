using System.Reflection;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #1524 — the measured half of "the cell style getter builds a CellStyle per
///     access → Flyweight".
/// </summary>
/// <remarks>
///     <para>
///         The issue asked for a measurement FIRST and named its own gate: apply
///         a shared (flyweight) style representation only when the measurement
///         shows a win relative to "distinct styles &lt;&lt; cells in a frame",
///         otherwise record the measurement and keep the accepted shape.
///     </para>
///     <para>
///         The measurement says the shared form already exists and is not an
///         object. A cell carries its style PACKED (fg/bg/flags) inside its
///         16-byte protocol layout, and <see cref="Cell.Style" /> copies those
///         three fields out — it allocates NOTHING and hands out no instance
///         that could be mutated. The classic flyweight (one heap object per
///         distinct style, shared by the cells that use it) would ADD an
///         allocation per distinct style and would push the cell past the
///         16-byte budget the celldiff protocol pins. <see cref="Cell.HasSameStyle" />
///         is the form that rebuilds nothing: it compares the packed fields,
///         which IS the shared representation.
///     </para>
///     <para>
///         The tests below pin all of it: the distinct-vs-cells ratio on a
///         representative chat frame, the zero-allocation property of the style
///         getter (the flyweight premise), the equivalence of the packed
///         compare with the value compare, and the invariants that make the
///         accepted shape true (an immutable value type, and a 16-byte cell).
///     </para>
/// </remarks>
public class CellStyleSharedViewTests
{
    private const int FrameCols = 200;
    private const int FrameRows = 50;

    /// <summary>
    ///     The issue's relative gate: distinct styles must be an order of
    ///     magnitude below the cells that carry them, otherwise a shared style
    ///     representation cannot pay for itself.
    /// </summary>
    private const int MinimumCellsPerDistinctStyle = 10;

    /// <summary>
    ///     The gate number on a representative chat frame: how many cells carry
    ///     a style, and how many DISTINCT styles those cells use. The sharing
    ///     ratio is what decides whether a flyweight over styles can pay.
    /// </summary>
    [Test]
    public async Task RepresentativeFrame_CarriesFarFewerDistinctStylesThanCells()
    {
        var buffer = BuildRepresentativeFrame();
        int distinct = CountDistinctStyles(buffer, out int painted, out int total);

        double ratio = (double)painted / distinct;
        TestContext.Current?.Output.WriteLine(
            $"#1524 representative frame: {total} cells, {painted} painted, "
            + $"{distinct} distinct styles, {ratio:F1} cells per style");

        await Assert.That(painted).IsGreaterThan(500)
            .Because(
                "A frame that paints almost nothing cannot measure the distinct-vs-cells ratio — "
                + "the measurement would be vacuous.");

        await Assert.That(distinct).IsGreaterThan(1)
            .Because(
                "A frame with a single style would make the ratio meaningless: the gate needs a real "
                + "style mix (user prefix, assistant markdown, tool names, tool status, dim system text).");

        await Assert.That(ratio).IsGreaterThanOrEqualTo(MinimumCellsPerDistinctStyle)
            .Because(
                $"#1524 gate: distinct styles must be at least {MinimumCellsPerDistinctStyle}x below the "
                + $"cells carrying them ({painted} painted cells, {distinct} distinct styles).");
    }

    /// <summary>
    ///     The flyweight premise: if reading a cell's style allocated, equal
    ///     styles would cost one allocation each and a shared instance would
    ///     pay. It does not — the getter copies three packed fields out of the
    ///     cell, so there is nothing for a cache to save.
    /// </summary>
    [Test]
    public async Task StyleAccess_AllocatesNothing()
    {
        var buffer = BuildRepresentativeFrame();
        var cells = new Cell[FrameCols * FrameRows];
        int painted = 0;
        for (int y = 0; y < FrameRows; y++)
        {
            for (int x = 0; x < FrameCols; x++)
            {
                var cell = buffer.Get(x, y);
                cells[(y * FrameCols) + x] = cell;
                if (!(cell.Style == CellStyle.Plain && cell.Rune == ' '))
                {
                    painted++;
                }
            }
        }

        // Warm past JIT tier-up: the tier transition charges one-off runtime
        // bookkeeping bytes to the measuring thread.
        long sink = 0;
        for (int w = 0; w < 100_000; w++)
        {
            sink += cells[w % cells.Length].Style.Fg.Value;
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int iterations = 100_000;
        for (int i = 0; i < iterations; i++)
        {
            sink += cells[i % cells.Length].Style.Fg.Value;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        TestContext.Current?.Output.WriteLine(
            $"#1524 style access: {iterations} reads over a {painted}-cell painted frame, "
            + $"{allocated} B allocated, sink={sink}");

        await Assert.That(allocated).IsEqualTo(0)
            .Because(
                "#1524: the flyweight premise is that reading a style costs an allocation. It does not — "
                + "CellStyle is a readonly struct and Cell.Style copies three packed fields out of the "
                + "cell. A cache over styles would therefore have nothing to save, and the number here is "
                + "what proves it.");
    }

    /// <summary>
    ///     The packed compare and the value compare must agree, cell for cell:
    ///     <see cref="Cell.HasSameStyle" /> is only a cheaper spelling of
    ///     "the two styles are equal" if it says exactly that.
    /// </summary>
    [Test]
    public async Task HasSameStyle_AgreesWithTheCellStyleValueComparison()
    {
        var buffer = BuildRepresentativeFrame();

        // Every distinct style the frame uses, plus a hand-built set that
        // covers each field of the style: default, palette-indexed, truecolor,
        // attribute-only, and mixed.
        var cells = new List<Cell>();
        var seen = new HashSet<CellStyle>();
        for (int y = 0; y < FrameRows; y++)
        {
            for (int x = 0; x < FrameCols; x++)
            {
                var cell = buffer.Get(x, y);
                if (seen.Add(cell.Style))
                {
                    cells.Add(cell);
                }
            }
        }

        cells.Add(Cell.FromRaw('x', 0, 0, 0, Cell.Narrow));
        cells.Add(Cell.FromRaw('x', 1, 0, 0, Cell.Narrow));
        cells.Add(Cell.FromRaw('x', 0, 1, 0, Cell.Narrow));
        cells.Add(Cell.FromRaw('x', 0, 0, 1, Cell.Narrow));
        cells.Add(Cell.FromRaw('x', 0x00FF_FFFF, 0, 0, Cell.Narrow));
        cells.Add(Cell.FromRaw('x', 1, 2, 3, Cell.Narrow));

        var mismatches = new List<string>();
        int compared = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            for (int j = 0; j < cells.Count; j++)
            {
                var left = cells[i];
                var right = cells[j];
                bool byValue = left.Style == right.Style;
                bool byPacked = left.HasSameStyle(in right);

                if (byPacked != byValue && mismatches.Count < 8)
                {
                    mismatches.Add($"cell {i} ({left.Style}) vs cell {j} ({right.Style}): "
                                   + $"packed={byPacked} value={byValue}");
                }

                compared++;
            }
        }

        await Assert.That(mismatches).IsEmpty()
            .Because(
                "#1524: the packed compare must agree with the value compare on every pair. "
                + string.Join("; ", mismatches));

        await Assert.That(cells.Count).IsGreaterThanOrEqualTo(8)
            .Because(
                "The equivalence check must run over a real set of styles: the frame's own distinct styles "
                + "plus the hand-built per-field set. Fewer than eight would mean the frame painted almost "
                + "nothing.");

        await Assert.That(compared).IsEqualTo(cells.Count * cells.Count)
            .Because("Every style pair must be compared, not a sample of them.");
    }

    /// <summary>
    ///     The invariants that make the accepted shape true: the style is an
    ///     IMMUTABLE value type (so no shared instance can be mutated under a
    ///     caller), and the cell stays inside its 16-byte protocol budget.
    /// </summary>
    [Test]
    public async Task CellStyle_IsAnImmutableValueType_AndTheCellStaysSixteenBytes()
    {
        await Assert.That(typeof(CellStyle).IsValueType).IsTrue()
            .Because(
                "#1524: a flyweight needs a shared instance, and a shared instance is mutable state. "
                + "CellStyle is a value type, so the style a caller holds can never be mutated through "
                + "another holder.");

        foreach (FieldInfo field in typeof(CellStyle).GetFields(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            await Assert.That(field.IsInitOnly).IsTrue()
                .Because($"#1524: CellStyle.{field.Name} must stay readonly — a mutable style field is the "
                          + "bug a shared-instance flyweight would introduce.");

            await Assert.That(field.FieldType.IsValueType).IsTrue()
                .Because($"#1524: CellStyle.{field.Name} must hold a value type — a reference field would "
                          + "put shared mutable state inside the style.");
        }

        await Assert.That(typeof(Cell).IsValueType).IsTrue();
        await Assert.That(Cell.SizeBytes).IsEqualTo(16)
            .Because(
                "#1524: the cell carries its style packed, and the celldiff protocol pins the cell at 16 "
                + "bytes. A shared style OBJECT per cell would grow it and break the wire format.");
    }

    /// <summary>
    ///     The frame path with a rich style mix: a full repaint of a screen
    ///     that uses many distinct styles still allocates nothing, so the style
    ///     handling is free of the heap end to end.
    /// </summary>
    [Test]
    public async Task FullRepaint_WithManyDistinctStyles_AllocatesNothing()
    {
        var backend = new CountingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var engine = new DiffEngine(FrameCols, FrameRows);
        var back = new EngineCells.ScreenBuffer(FrameCols, FrameRows);

        // 16 distinct styles: palette-indexed colors crossed with attribute bits.
        var styles = new EngineCells.CellStyle[16];
        for (int i = 0; i < styles.Length; i++)
        {
            var attrs = (EngineCells.StyleAttr)((i % 2 == 0) ? 1 : 2);
            styles[i] = new EngineCells.CellStyle(
                EngineCells.PackedColor.Indexed((byte)(i * 7)),
                EngineCells.PackedColor.Indexed((byte)(255 - (i * 5))),
                attrs);
        }

        for (int y = 0; y < FrameRows; y++)
        {
            var style = styles[y % styles.Length];
            back.SetText(0, y, $"row {y} " + new string('.', FrameCols - 8), style);
        }

        // Warmup: JIT tier-up + the writer's frame buffer growth.
        for (int i = 0; i < 100; i++)
        {
            engine.Front.BlankAll();
            writer.BeginFrame();
            engine.Flush(back, writer);
            writer.EndFrame();
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int frames = 200;
        for (int i = 0; i < frames; i++)
        {
            engine.Front.BlankAll();
            writer.BeginFrame();
            engine.Flush(back, writer);
            writer.EndFrame();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        TestContext.Current?.Output.WriteLine(
            $"#1524 styled full repaint: {frames} frames of {FrameCols}x{FrameRows} with 16 distinct "
            + $"styles, {allocated} B allocated");

        await Assert.That(allocated).IsEqualTo(0)
            .Because(
                "#1524: the diff path resolves a style per changed cell. With 16 distinct styles across "
                + "the frame that must still be allocation-free — the style is a packed value, not a "
                + "rebuilt object.");
    }

    /// <summary>
    ///     The engine carries its own copy of the cell vocabulary (the engine is
    ///     a zero-Harbor-reference leaf), and the two copies are pinned by an
    ///     architecture rule. The shared compare must exist in BOTH, or the
    ///     hot path and the portable path would compute styles differently.
    /// </summary>
    [Test]
    public async Task TheEngineTwin_ExposesTheSamePackedStyleCompare()
    {
        MethodInfo? engineMethod = typeof(EngineCells.Cell).GetMethod(
            "HasSameStyle",
            BindingFlags.Instance | BindingFlags.Public);
        MethodInfo? sharedMethod = typeof(Cell).GetMethod(
            "HasSameStyle",
            BindingFlags.Instance | BindingFlags.Public);

        await Assert.That(engineMethod).IsNotNull()
            .Because("#1524: the engine's cell copy must expose the packed style compare too.");
        await Assert.That(sharedMethod).IsNotNull()
            .Because("#1524: the shared rendering cell must expose the packed style compare.");

        var left = EngineCells.Cell.FromRaw('a', 7, 9, 3, EngineCells.Cell.Narrow);
        var right = EngineCells.Cell.FromRaw('b', 7, 9, 3, EngineCells.Cell.Narrow);
        var other = EngineCells.Cell.FromRaw('a', 7, 9, 4, EngineCells.Cell.Narrow);

        bool engineSame = (bool)engineMethod!.Invoke(left, [right])!;
        bool engineDifferent = (bool)engineMethod.Invoke(left, [other])!;

        await Assert.That(engineSame).IsTrue()
            .Because("Same style, different rune — the compare is about the STYLE only.");
        await Assert.That(engineDifferent).IsFalse()
            .Because("Different attribute bits — the compare must see them.");
    }

    /// <summary>
    ///     Builds a representative chat frame: user prompts, assistant markdown
    ///     (headings, bold, inline code, list bullets) and tool calls, painted
    ///     into a real 200x50 grid through the real panels.
    /// </summary>
    private static ScreenBuffer BuildRepresentativeFrame()
    {
        var screen = ChatScreen.Build(
            new ComposerController(),
            new StatusViewModel { Model = "kilocode/hy3" });

        var timeline = screen.Timeline.Timeline;
        for (int i = 0; i < 8; i++)
        {
            timeline.Append(new UserBlock($"user prompt {i} asking something reasonably long"));
            timeline.Append(new AssistantMarkdownBlock(
                $"## Answer {i}\nText with **bold** and `code`.\n- point a\n- point b\n"));
            timeline.Append(new ToolCallBlock(
                new ToolCallInfo($"t{i}", "read", $"{{\"path\":\"src/f{i}.cs\"}}")));
        }

        var buffer = new ScreenBuffer(FrameCols, FrameRows);
        screen.Tree.Solve(FrameCols, FrameRows);
        _ = timeline.PrepareFrame(FrameCols, screen.Timeline.Rect.Height);
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(buffer);
        }

        int distinct = CountDistinctStyles(buffer, out _, out _);
        return buffer;
    }

    /// <summary>
    ///     Counts the distinct styles in a painted grid. A cell counts as
    ///     painted when it is not an untouched blank (a space with the plain
    ///     style), so the ratio is measured over the cells that actually carry
    ///     a style.
    /// </summary>
    private static int CountDistinctStyles(ScreenBuffer buffer, out int painted, out int total)
    {
        var distinct = new HashSet<CellStyle>();
        painted = 0;
        total = buffer.Cols * buffer.Rows;

        for (int y = 0; y < buffer.Rows; y++)
        {
            for (int x = 0; x < buffer.Cols; x++)
            {
                var cell = buffer.Get(x, y);
                var style = cell.Style;
                if (style == CellStyle.Plain && cell.Rune == ' ')
                {
                    continue;
                }

                painted++;
                distinct.Add(style);
            }
        }

        return distinct.Count;
    }
}
