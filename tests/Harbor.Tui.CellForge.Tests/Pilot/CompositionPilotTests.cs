// #2522: prove the chat-independent pilot finds a widget by id and reads its
// visible output from the ScreenBuffer, with no CellForgeTuiRenderer, no
// ChatKeyMap and no chat view-models in the picture.

using System;

namespace Harbor.Tui.CellForge.Tests.Pilot;

public class CompositionPilotTests
{
    private const int Cols = 24;
    private const int Rows = 3;

    /// <summary>Plain leaf: paints its text at the top-left of its arranged rect.</summary>
    private sealed class LabelNode(string text) : EngineCells.LeafNode
    {
        public override EngineCells.Size Measure(EngineCells.Constraints constraints) =>
            new(text.Length, 1);

        public override void Paint(EngineCells.ScreenBuffer buffer, EngineCells.Rect clip)
        {
            var area = PaintArea(clip);
            if (area.Width <= 0 || area.Height <= 0)
            {
                return;
            }

            int count = Math.Min(text.Length, area.Width);
            buffer.SetText(area.X, area.Y, text.AsSpan(0, count), EngineCells.CellStyle.Plain);
        }
    }

    /// <summary>Prepaint leaf: records its ElementId path and paints via a scene range.</summary>
    private sealed class TracedLabelNode(string text) : EngineCells.PrepaintNode
    {
        public Action<EngineCells.ElementId>? Recorder { get; set; }

        protected override ulong PrepaintKey => (ulong)(uint)text.GetHashCode(StringComparison.Ordinal);

        public override EngineCells.Size Measure(EngineCells.Constraints constraints) =>
            new(text.Length, 1);

        protected override EngineCells.PrepaintRecord OnPrepaint(
            EngineCells.PrepaintContext context,
            EngineCells.ElementId id,
            EngineCells.Rect bounds)
        {
            Recorder?.Invoke(id);
            var mark = context.Scene.Mark();
            int count = Math.Min(text.Length, bounds.Width);
            context.Scene.SetText(bounds.X, bounds.Y, text.AsSpan(0, count), EngineCells.CellStyle.Plain);
            return new EngineCells.PrepaintRecord(
                bounds,
                EngineCells.CellStyle.Plain,
                context.Scene.Close(mark),
                null);
        }
    }

    private static EngineCells.INode BuildComposition(
        EngineCells.INode left,
        EngineCells.INode center,
        EngineCells.INode right)
    {
        var root = new EngineCells.FlexSplitNode(EngineCells.SplitDir.Horizontal);
        root.Add(left, EngineCells.FlexTrack.Fill());
        root.Add(center, EngineCells.FlexTrack.Fill());
        root.Add(right, EngineCells.FlexTrack.Fill());
        return root;
    }

    [Test]
    public async Task FindById_ReadsVisibleOutput_OfOneWidget()
    {
        var left = new LabelNode("LEFT");
        var center = new TracedLabelNode("CENTER");
        var right = new LabelNode("RIGHT");
        var pilot = new CompositionPilot(() => BuildComposition(left, center, right), Cols, Rows)
            .Register("left", left)
            .Register("center", center)
            .Register("right", right);
        center.Recorder = pilot.Trace;

        pilot.Render();

        // Find by stable id, independent of the prepaint ElementId path.
        await Assert.That(pilot.FindById("center")).IsSameReferenceAs((EngineCells.INode)center);
        await Assert.That(pilot.FindById("missing")).IsNull();

        // The found node's visible output is cropped to its arranged rect:
        // each widget owns a third of the 24-column grid and only its own text.
        await Assert.That(pilot.NodeText("center")).IsEqualTo("CENTER");
        await Assert.That(pilot.NodeText("left")).IsEqualTo("LEFT");
        await Assert.That(pilot.NodeText("right")).IsEqualTo("RIGHT");

        // Whole frame is the three widgets side by side, with no chat chrome.
        await Assert.That(pilot.FrameText().Contains("LEFT")).IsTrue();
        await Assert.That(pilot.FrameText().Contains("CENTER")).IsTrue();
        await Assert.That(pilot.FrameText().Contains("RIGHT")).IsTrue();

        // Prepaint addressed the traced widget by its frame path, a separate
        // concept from the id used for lookup.
        await Assert.That(pilot.IdTrace).Contains("/1");
    }

    [Test]
    public async Task TwoFrames_ReusePrepaint_AndNodeTextTracksContent()
    {
        var left = new MutableLabel("A");
        var center = new TracedLabelNode("MID");
        var right = new LabelNode("Z");
        var pilot = new CompositionPilot(() => BuildComposition(left, center, right), Cols, Rows)
            .Register("left", left)
            .Register("center", center)
            .Register("right", right);
        center.Recorder = pilot.Trace;

        pilot.Render();
        await Assert.That(pilot.Context.Misses).IsEqualTo(2);
        await Assert.That(pilot.NodeText("left")).IsEqualTo("A");
        await Assert.That(pilot.IdTrace).Contains("/1");

        // Same tree, second frame: both prepaint records are reused (hits) and
        // the driver still reads the same visible output without a renderer.
        pilot.ResetTrace().Render();
        await Assert.That(pilot.Context.Hits).IsEqualTo(2);
        await Assert.That(pilot.NodeText("center")).IsEqualTo("MID");

        // Bump the widget's version key; the driver observes the new text.
        left.Text = "B";
        pilot.Render();
        await Assert.That(pilot.NodeText("left")).IsEqualTo("B");
    }

    /// <summary>A plain label whose text can change between frames.</summary>
    private sealed class MutableLabel(string text) : EngineCells.PrepaintNode
    {
        public string Text { get; set; } = text;

        protected override ulong PrepaintKey => (ulong)(uint)Text.GetHashCode(StringComparison.Ordinal);

        public override EngineCells.Size Measure(EngineCells.Constraints constraints) =>
            new(Text.Length, 1);

        protected override EngineCells.PrepaintRecord OnPrepaint(
            EngineCells.PrepaintContext context,
            EngineCells.ElementId id,
            EngineCells.Rect bounds)
        {
            var mark = context.Scene.Mark();
            int count = Math.Min(Text.Length, bounds.Width);
            context.Scene.SetText(bounds.X, bounds.Y, Text.AsSpan(0, count), EngineCells.CellStyle.Plain);
            return new EngineCells.PrepaintRecord(
                bounds,
                EngineCells.CellStyle.Plain,
                context.Scene.Close(mark),
                null);
        }
    }
}
