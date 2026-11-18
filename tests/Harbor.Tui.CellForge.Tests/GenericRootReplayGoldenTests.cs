// #2516: the production host must drive the INode/NodeFrame pipeline, not
// just the unit tests. This golden pins the outcome end to end: the generic
// renderer solves plus paints its screen, overlays the optional root through
// NodeFrame.Render, and the second frame replays the recorded scene range
// (PrepaintContext.Hits > 0) with byte-identical art.

using Harbor.Tui.CellForge.Generic;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

public class GenericRootReplayGoldenTests
{
    private const int Cols = 16;
    private const int Rows = 3;

    /// <summary>
    /// Reference leaf that records one text op in prepaint and only replays it
    /// in Paint. Its version key is constant, so only the stable ElementId
    /// decides reuse — exactly the contract a production host relies on.
    /// </summary>
    private sealed class BannerNode : EngineCells.PrepaintNode
    {
        private readonly string _text;
        private readonly int _x;
        private readonly int _y;

        public BannerNode(string text, int x, int y)
        {
            _text = text;
            _x = x;
            _y = y;
        }

        public int Prepares { get; private set; }

        protected override ulong PrepaintKey => 1;

        public override EngineCells.Size Measure(EngineCells.Constraints constraints) =>
            new(_x + _text.Length, _y + 1);

        protected override EngineCells.PrepaintRecord OnPrepaint(
            EngineCells.PrepaintContext context,
            EngineCells.ElementId id,
            EngineCells.Rect bounds)
        {
            Prepares++;
            var mark = context.Scene.Mark();
            context.Scene.SetText(_x, _y, _text, EngineCells.CellStyle.Plain);
            return new EngineCells.PrepaintRecord(
                bounds,
                EngineCells.CellStyle.Plain,
                context.Scene.Close(mark),
                null);
        }
    }

    [Test]
    public async Task RootNode_SecondFrameReplaysScene_AndArtIsStable()
    {
        // Empty static panel: the screen paints blanks, so the golden pins the
        // root's contribution alone and never depends on chat or widget art.
        var screen = GenericScreen.Build([new GenericNodeSpec { Id = "base" }]);
        var root = new BannerNode("REPLAY-OK", 1, 1);
        using var renderer = new GenericTuiRenderer(null, screen) { Root = root };

        ScreenBuffer first = renderer.Render(Cols, Rows);
        await Assert.That(root.Prepares).IsEqualTo(1);
        await Assert.That(renderer.Prepaint.Misses).IsEqualTo(1);
        await Assert.That(renderer.Prepaint.Hits).IsEqualTo(0);

        ScreenBuffer second = renderer.Render(Cols, Rows);
        await Assert.That(root.Prepares).IsEqualTo(1);
        await Assert.That(renderer.Prepaint.Misses).IsEqualTo(1);
        await Assert.That(renderer.Prepaint.Hits).IsGreaterThan(0);

        const string golden =
            "                \n" +
            " REPLAY-OK     \n" +
            "                \n";
        await Assert.That(TestRender.Art(first)).IsEqualTo(golden);
        await Assert.That(TestRender.Art(second)).IsEqualTo(golden);
    }

    [Test]
    public async Task WithoutRoot_LegacyPathRendersScreenOnly()
    {
        var screen = GenericScreen.Build([new GenericNodeSpec { Id = "base" }]);
        using var renderer = new GenericTuiRenderer(null, screen);

        ScreenBuffer frame = renderer.Render(Cols, Rows);

        await Assert.That(renderer.Prepaint.Hits).IsEqualTo(0);
        await Assert.That(renderer.Prepaint.Misses).IsEqualTo(0);
        await Assert.That(TestRender.Art(frame)).IsEqualTo(
            "                \n" +
            "                \n" +
            "                \n");
    }
}
