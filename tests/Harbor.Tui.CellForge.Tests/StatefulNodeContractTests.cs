// #3086 (S5): the shared stateful contract. Each test mutates a primitive,
// captures its value snapshot, rebuilds the primitive at the same ElementId
// and restores the snapshot. That is the repaint shape: a fresh node with the
// same identity must observe the same focus / cursor / offset. The contract
// owns no map, so the snapshot is designed to be held by the engine's single
// ElementStateStore (#2436, wired in S3/#3084), not by a second store here.

using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

public class StatefulNodeContractTests
{
    [Test]
    public async Task ScrollableViewport_CapturedOffset_SurvivesARepaint()
    {
        var id = EngineCells.ElementId.Root.Child("timeline");
        var first = new ScrollableViewport(id);
        first.Configure(100, 10);
        first.SetOffset(42);

        EngineCells.IStatefulNode<ScrollableViewportState> before = first;
        var snapshot = before.CaptureState();

        var repainted = new ScrollableViewport(id);
        repainted.Configure(100, 10);
        ((EngineCells.IStatefulNode<ScrollableViewportState>)repainted).RestoreState(snapshot);

        await Assert.That(repainted.Offset).IsEqualTo(42L);
        await Assert.That(((EngineCells.IStatefulNode)repainted).StateId).IsEqualTo(id);
    }

    [Test]
    public async Task TileCamera_CapturedOrigin_SurvivesARepaint()
    {
        var id = EngineCells.ElementId.Root.Child("map");
        var first = new EngineCells.TileCamera(id, 20, 10);
        first.Follow(57, 33, 6, 4);

        EngineCells.IStatefulNode<EngineCells.TileCameraState> before = first;
        var snapshot = before.CaptureState();

        var repainted = new EngineCells.TileCamera(id, 20, 10);
        ((EngineCells.IStatefulNode<EngineCells.TileCameraState>)repainted).RestoreState(snapshot);

        await Assert.That(repainted.OriginX).IsEqualTo(first.OriginX);
        await Assert.That(repainted.OriginY).IsEqualTo(first.OriginY);
        await Assert.That(((EngineCells.IStatefulNode)repainted).StateId).IsEqualTo(id);
    }

    [Test]
    public async Task NavigableChangeBlocks_CapturedFocusAndCursors_SurviveARepaint()
    {
        var id = EngineCells.ElementId.Root.Child("diff");
        IReadOnlyList<EngineCells.ChangeBlock> blocks =
        [
            new("a", "A", [new EngineCells.ChangeLine(1, "one"), new EngineCells.ChangeLine(2, "two")]),
            new("b", "B", [new EngineCells.ChangeLine(3, "three")]),
        ];

        var first = new EngineCells.NavigableChangeBlocks(id);
        first.Replace(blocks);
        first.MoveLine(1);
        first.SetAnchor();
        first.SwitchActive();

        EngineCells.IStatefulNode<EngineCells.ChangeBlocksState> before = first;
        var snapshot = before.CaptureState();

        var repainted = new EngineCells.NavigableChangeBlocks(id);
        repainted.Replace(blocks);
        ((EngineCells.IStatefulNode<EngineCells.ChangeBlocksState>)repainted).RestoreState(snapshot);

        await Assert.That(repainted.Active).IsEqualTo(EngineCells.BlockSide.Right);
        await Assert.That(repainted.LineCursor(EngineCells.BlockSide.Left)).IsEqualTo(1);
        await Assert.That(repainted.Anchor(EngineCells.BlockSide.Left).HasValue).IsTrue();
        await Assert.That(repainted.Anchor(EngineCells.BlockSide.Left)!.Value).IsEqualTo(1);
        await Assert.That(((EngineCells.IStatefulNode)repainted).StateId).IsEqualTo(id);
    }
}
