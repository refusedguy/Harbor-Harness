// #2688 (tui-composition-2026-11, idea 1): a layout-tree Panel must be able to
// enter the engine's node pipeline, and it must keep its Id as a named ElementId
// step. The golden pins the cell bridge; the reorder test pins that a record
// committed for one panel is reused only when the name (not the slot) matches.

using Harbor.Tui.CellForge.Generic;
using Harbor.Tui.CellForge.Rendering;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

public class PanelNodeAdapterTests
{
    private const int Cols = 20;
    private const int Rows = 3;

    private static EngineCells.ScreenBuffer Render(
        EngineCells.INode root,
        EngineCells.PrepaintContext context,
        int cols,
        int rows)
    {
        var buffer = new EngineCells.ScreenBuffer(cols, rows);
        EngineCells.NodeFrame.Render(
            root,
            context,
            buffer,
            new EngineCells.Constraints(cols, rows),
            new EngineCells.Rect(0, 0, cols, rows));
        return buffer;
    }

    private static string Art(EngineCells.ScreenBuffer buffer)
    {
        var text = new System.Text.StringBuilder();
        for (int y = 0; y < buffer.Rows; y++)
        {
            for (int x = 0; x < buffer.Cols; x++)
            {
                text.Append((char)buffer.Get(x, y).Rune);
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static PanelNodeAdapter Panel(string id, string title) =>
        new(new GenericContentPanel(new GenericNodeSpec { Id = id, Title = title }));

    [Test]
    public async Task PanelNode_PaintsHostPanelIntoEngineGrid()
    {
        var adapter = Panel("title.panel", "HELLO");
        var context = new EngineCells.PrepaintContext();

        EngineCells.ScreenBuffer frame = Render(adapter, context, Cols, Rows);

        await Assert.That(Art(frame)).Contains("HELLO");
    }

    [Test]
    public async Task PanelNode_NamedIdIsPanelId()
    {
        var adapter = Panel("panel.a", "A");

        await Assert.That(adapter.NamedId).IsEqualTo(EngineCells.ElementId.Root.Child("panel.a"));
        await Assert.That(adapter.PanelId).IsEqualTo("panel.a");
    }

    [Test]
    public async Task KeyedChildren_ReorderReusesRecords_IndexedChildrenDoNot()
    {
        var a = Panel("panel.a", "A");
        var b = Panel("panel.b", "B");

        var keyedContext = new EngineCells.PrepaintContext();
        var keyed = new EngineCells.FlexSplitNode(SplitDir.Horizontal);
        keyed.Add(a, a.PanelId, EngineCells.FlexTrack.Fill());
        keyed.Add(b, b.PanelId, EngineCells.FlexTrack.Fill());
        _ = Render(keyed, keyedContext, Cols, Rows);
        _ = Render(keyed, keyedContext, Cols, Rows);
        int keyedHitsBefore = keyedContext.Hits;

        var keyedReordered = new EngineCells.FlexSplitNode(SplitDir.Horizontal);
        keyedReordered.Add(b, b.PanelId, EngineCells.FlexTrack.Fill());
        keyedReordered.Add(a, a.PanelId, EngineCells.FlexTrack.Fill());
        _ = Render(keyedReordered, keyedContext, Cols, Rows);

        await Assert.That(keyedContext.Misses).IsEqualTo(2);
        await Assert.That(keyedContext.Hits).IsGreaterThan(keyedHitsBefore);

        var indexedContext = new EngineCells.PrepaintContext();
        var indexed = new EngineCells.FlexSplitNode(SplitDir.Horizontal);
        indexed.Add(Panel("panel.a", "A"), EngineCells.FlexTrack.Fill());
        indexed.Add(Panel("panel.b", "B"), EngineCells.FlexTrack.Fill());
        _ = Render(indexed, indexedContext, Cols, Rows);
        _ = Render(indexed, indexedContext, Cols, Rows);
        int indexedMissesBefore = indexedContext.Misses;

        var indexedReordered = new EngineCells.FlexSplitNode(SplitDir.Horizontal);
        indexedReordered.Add(Panel("panel.b", "B"), EngineCells.FlexTrack.Fill());
        indexedReordered.Add(Panel("panel.a", "A"), EngineCells.FlexTrack.Fill());
        _ = Render(indexedReordered, indexedContext, Cols, Rows);

        await Assert.That(indexedContext.Misses).IsGreaterThan(indexedMissesBefore);
    }
}
