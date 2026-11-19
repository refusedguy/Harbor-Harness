// #3087 (S6): behavior + golden coverage for the BCL-only widget catalog.
// Art is local and dependency-free: the catalog is an ENGINE surface, so it
// paints the engine ScreenBuffer rather than the renderer's twin. The golden
// rows are inline by design — a new widget must not need a regenerated binary
// fixture to prove its exact frame.

using System.Text;
using Harbor.Tui.CellForge.Catalog;
using Harbor.Tui.CellForge.Rendering;
using TUnit.Assertions;
// The suite's GlobalUsings alias the RENDERER twins of these names; the
// catalog is an ENGINE surface, so name the engine types explicitly.
using EngineCell = Harbor.Tui.CellForge.Rendering.Cell;
using EngineRect = Harbor.Tui.CellForge.Rendering.Rect;
using EngineScreenBuffer = Harbor.Tui.CellForge.Rendering.ScreenBuffer;

namespace Harbor.Tui.CellForge.Tests;

public sealed class WidgetCatalogTests
{
    private static string Art(EngineScreenBuffer buffer)
    {
        var rows = new string[buffer.Rows];
        for (int y = 0; y < buffer.Rows; y++)
        {
            var row = new StringBuilder(buffer.Cols);
            int end = buffer.Cols;
            while (end > 0 && buffer.Get(end - 1, y).Rune == ' ')
            {
                end--;
            }

            for (int x = 0; x < end; x++)
            {
                EngineCell cell = buffer.Get(x, y);
                row.Append(cell.Width == EngineCell.WSkip ? '\0' : (char)cell.Rune);
            }

            rows[y] = row.ToString();
        }

        return string.Join('\n', rows);
    }

    private static string Frame(int cols, int rows, WidgetSpec spec)
    {
        var buffer = new EngineScreenBuffer(cols, rows);
        WidgetCatalog.Paint(spec, buffer, new EngineRect(0, 0, cols, rows));
        return Art(buffer);
    }

    // ── Goldens: one exact frame per ready widget ───────────────────────────

    [Test]
    public async Task Golden_Paragraph_ClipsEachLine()
    {
        string frame = Frame(12, 4, new ParagraphSpec("alpha\nbeta"));
        await Assert.That(frame).IsEqualTo("alpha\nbeta\n\n");
    }

    [Test]
    public async Task Golden_Rule_FillsOneRow()
    {
        string frame = Frame(12, 1, RuleSpec.Dashed());
        await Assert.That(frame).IsEqualTo("------------");
    }

    [Test]
    public async Task Golden_Gauge_SplitsLabelBarAndRemainder()
    {
        string frame = Frame(12, 1, GaugeSpec.Create("load", 0.5));
        await Assert.That(frame).IsEqualTo("load ###----");
    }

    [Test]
    public async Task Golden_List_MarksTheSelectedRow()
    {
        string frame = Frame(12, 4, ListSpec.Create(["one", "two", "three"], 1));
        await Assert.That(frame).IsEqualTo("> one\n  two\n  three\n");
    }

    [Test]
    public async Task Golden_Status_RightAlignsTheValue()
    {
        string frame = Frame(12, 1, new StatusSpec("mode", "ready"));
        await Assert.That(frame).IsEqualTo("mode   ready");
    }

    // ── Behavior: invariants a golden frame cannot prove ───────────────────

    [Test]
    public async Task Catalog_ExposesFiveDistinctDeclarativeWidgets()
    {
        var widgets = WidgetCatalog.Widgets;
        await Assert.That(widgets.Count).IsEqualTo(5);
        await Assert.That(widgets.Select(static w => w.Kind).Distinct().Count()).IsEqualTo(widgets.Count);
        await Assert.That(widgets.Select(static w => w.Name).Distinct().Count()).IsEqualTo(widgets.Count);
        foreach (var widget in widgets)
        {
            await Assert.That(widget.Description).IsNotEmpty();
        }
    }

    [Test]
    public async Task Measure_ClampsBothExtents()
    {
        var bounds = new Constraints(6, 2);
        await Assert.That(WidgetCatalog.Measure(new ParagraphSpec("abcdefgh\nxy"), bounds)).IsEqualTo(new Size(6, 2));
        await Assert.That(WidgetCatalog.Measure(RuleSpec.Dashed(), bounds)).IsEqualTo(new Size(6, 1));
        await Assert.That(WidgetCatalog.Measure(GaugeSpec.Create("load", 0.5), bounds)).IsEqualTo(new Size(6, 1));
        await Assert.That(WidgetCatalog.Measure(ListSpec.Create(["aaaaa", "b"], 0), bounds)).IsEqualTo(new Size(6, 2));
        await Assert.That(WidgetCatalog.Measure(new StatusSpec("mode", "ready"), bounds)).IsEqualTo(new Size(6, 1));
    }

    [Test]
    public async Task Measure_EmptyParagraphHasNoHeight()
    {
        Size size = WidgetCatalog.Measure(new ParagraphSpec(string.Empty), new Constraints(10, 4));
        await Assert.That(size).IsEqualTo(new Size(0, 0));
    }

    [Test]
    public async Task Gauge_ClampsRatioAtBothEndsAndTreatsNaNAsEmpty()
    {
        string full = Frame(10, 1, GaugeSpec.Create("load", 2.0));
        string empty = Frame(10, 1, GaugeSpec.Create("load", -1.0));
        string nan = Frame(10, 1, GaugeSpec.Create("load", double.NaN));

        await Assert.That(full).IsEqualTo("load #####");
        await Assert.That(empty).IsEqualTo("load -----");
        await Assert.That(nan).IsEqualTo("load -----");
    }

    [Test]
    public async Task Gauge_WithNoRoomForABarStillPaintsTheLabel()
    {
        string frame = Frame(4, 1, GaugeSpec.Create("load", 1.0));
        await Assert.That(frame).IsEqualTo("load");
    }

    [Test]
    public async Task List_SelectionClampsAndAnEmptyListHasNoSelection()
    {
        var list = ListSpec.Create(["a", "b", "c"], 0);
        ListSpec down = WidgetCatalog.Move(list, 10);
        await Assert.That(down.Selected).IsEqualTo(2);

        ListSpec up = WidgetCatalog.Move(down, -10);
        await Assert.That(up.Selected).IsEqualTo(0);

        ListSpec clamped = WidgetCatalog.Select(list, -5);
        await Assert.That(clamped.Selected).IsEqualTo(0);

        ListSpec empty = ListSpec.Create([], 0);
        await Assert.That(WidgetCatalog.Move(empty, 1).Selected).IsEqualTo(-1);
    }

    [Test]
    public async Task Painting_StaysInsideTheGivenArea()
    {
        var buffer = new EngineScreenBuffer(6, 4);
        WidgetCatalog.Paint(RuleSpec.Dashed(), buffer, new EngineRect(1, 1, 3, 2));

        await Assert.That(buffer.Get(0, 1).Rune).IsEqualTo((int)' ');
        await Assert.That(buffer.Get(1, 1).Rune).IsEqualTo((int)'-');
        await Assert.That(buffer.Get(3, 1).Rune).IsEqualTo((int)'-');
        await Assert.That(buffer.Get(4, 1).Rune).IsEqualTo((int)' ');
        await Assert.That(buffer.Get(1, 2).Rune).IsEqualTo((int)' ');
        await Assert.That(buffer.Get(1, 0).Rune).IsEqualTo((int)' ');
    }

    [Test]
    public async Task OffscreenAndZeroAreasAreNoOps()
    {
        var buffer = new EngineScreenBuffer(4, 2);
        WidgetCatalog.Paint(RuleSpec.Dashed(), buffer, new EngineRect(-5, -5, 2, 2));
        WidgetCatalog.Paint(RuleSpec.Dashed(), buffer, new EngineRect(0, 0, 0, 1));

        await Assert.That(Art(buffer)).IsEqualTo("\n");
    }

    [Test]
    public async Task Status_TruncatesCaptionBeforeOverwritingTheValue()
    {
        string frame = Frame(8, 1, new StatusSpec("verylongcaption", "ok"));
        await Assert.That(frame).IsEqualTo("veryl ok");
    }

    [Test]
    public async Task Paragraph_HonoursTheAvailableHeight()
    {
        string frame = Frame(8, 1, new ParagraphSpec("first\nsecond"));
        await Assert.That(frame).IsEqualTo("first");
    }

    [Test]
    public async Task EveryDeclaredKindPaintsThroughTheSingleEntry()
    {
        WidgetSpec[] specs =
        [
            new ParagraphSpec("text"),
            RuleSpec.Dashed(),
            GaugeSpec.Create("g", 0.5),
            ListSpec.Create(["item"], 0),
            new StatusSpec("k", "v"),
        ];

        for (int i = 0; i < specs.Length; i++)
        {
            string frame = Frame(8, 2, specs[i]);
            await Assert.That(frame).IsNotEmpty()
                .Because($"widget kind {specs[i].Kind} must paint through WidgetCatalog.Paint");
        }
    }
}
