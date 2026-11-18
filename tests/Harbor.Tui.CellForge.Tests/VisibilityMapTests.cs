using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Pins the world-space fog-of-war buffer introduced for CellForge tile
/// primitives: visibility, explored history and counts stay consistent without
/// any field-of-view algorithm in the engine.
/// </summary>
public class VisibilityMapTests
{
    private static VisibilityMap NewMap() => new(new EngineCells.Rect(0, 0, 8, 4));

    [Test]
    public async Task New_Map_Starts_Unexplored()
    {
        var map = NewMap();

        await Assert.That(map.Bounds.Width).IsEqualTo(8);
        await Assert.That(map.Bounds.Height).IsEqualTo(4);
        await Assert.That(map.VisibleCount).IsEqualTo(0);
        await Assert.That(map.ExploredCount).IsEqualTo(0);
        await Assert.That(map.StateAt(3, 2)).IsEqualTo(TileVisibility.Unexplored);
        await Assert.That(map.IsVisible(3, 2)).IsFalse();
        await Assert.That(map.IsExplored(3, 2)).IsFalse();
    }

    [Test]
    public async Task Bounds_Use_World_Coordinates_Including_Negative_Origins()
    {
        var map = new VisibilityMap(new EngineCells.Rect(-10, -5, 4, 3));

        await Assert.That(map.Contains(-10, -5)).IsTrue();
        await Assert.That(map.Contains(-7, -3)).IsTrue();
        await Assert.That(map.Contains(-6, -3)).IsFalse();
        await Assert.That(map.Contains(-10, -2)).IsFalse();
        await Assert.That(map.Reveal(-10, -5)).IsTrue();
        await Assert.That(map.Reveal(-7, -3)).IsTrue();
        await Assert.That(map.IsVisible(-10, -5)).IsTrue();
        await Assert.That(map.IsVisible(-7, -3)).IsTrue();
        await Assert.That(map.StateAt(-7, -3)).IsEqualTo(TileVisibility.Visible);
        await Assert.That(map.VisibleCount).IsEqualTo(2);
    }

    [Test]
    public async Task Reveal_Marks_Explored_And_Is_Idempotent()
    {
        var map = NewMap();

        await Assert.That(map.Reveal(2, 1)).IsTrue();
        await Assert.That(map.Reveal(2, 1)).IsFalse();
        await Assert.That(map.StateAt(2, 1)).IsEqualTo(TileVisibility.Visible);
        await Assert.That(map.IsExplored(2, 1)).IsTrue();
        await Assert.That(map.VisibleCount).IsEqualTo(1);
        await Assert.That(map.ExploredCount).IsEqualTo(1);
    }

    [Test]
    public async Task Hide_Leaves_The_Tile_Explored()
    {
        var map = NewMap();
        await Assert.That(map.Reveal(1, 1)).IsTrue();
        await Assert.That(map.Reveal(6, 3)).IsTrue();

        await Assert.That(map.Hide(1, 1)).IsTrue();
        await Assert.That(map.Hide(1, 1)).IsFalse();
        await Assert.That(map.StateAt(1, 1)).IsEqualTo(TileVisibility.Explored);
        await Assert.That(map.IsVisible(1, 1)).IsFalse();
        await Assert.That(map.IsExplored(1, 1)).IsTrue();
        await Assert.That(map.StateAt(6, 3)).IsEqualTo(TileVisibility.Visible);
        await Assert.That(map.VisibleCount).IsEqualTo(1);
        await Assert.That(map.ExploredCount).IsEqualTo(2);
    }

    [Test]
    public async Task ClearVisible_Keeps_History_And_Reset_Drops_It()
    {
        var map = NewMap();
        await Assert.That(map.Reveal(0, 0)).IsTrue();
        await Assert.That(map.Reveal(7, 3)).IsTrue();
        await Assert.That(map.Hide(7, 3)).IsTrue();

        map.ClearVisible();

        await Assert.That(map.VisibleCount).IsEqualTo(0);
        await Assert.That(map.ExploredCount).IsEqualTo(2);
        await Assert.That(map.StateAt(0, 0)).IsEqualTo(TileVisibility.Explored);
        await Assert.That(map.StateAt(7, 3)).IsEqualTo(TileVisibility.Explored);

        map.Reset();

        await Assert.That(map.ExploredCount).IsEqualTo(0);
        await Assert.That(map.StateAt(0, 0)).IsEqualTo(TileVisibility.Unexplored);
        await Assert.That(map.StateAt(7, 3)).IsEqualTo(TileVisibility.Unexplored);
    }

    [Test]
    public async Task Outside_Coordinates_Never_Mutate_The_Map()
    {
        var map = NewMap();

        await Assert.That(map.StateAt(-1, 0)).IsEqualTo(TileVisibility.Outside);
        await Assert.That(map.StateAt(0, 4)).IsEqualTo(TileVisibility.Outside);
        await Assert.That(map.IsVisible(-1, 0)).IsFalse();
        await Assert.That(map.IsExplored(0, 4)).IsFalse();
        await Assert.That(map.Reveal(-1, 0)).IsFalse();
        await Assert.That(map.Hide(0, 4)).IsFalse();
        await Assert.That(map.VisibleCount).IsEqualTo(0);
        await Assert.That(map.ExploredCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(0, 4)]
    [Arguments(8, 0)]
    [Arguments(-1, 4)]
    public async Task Constructor_Rejects_Empty_Bounds(int width, int height)
    {
        await Assert.That(() => new VisibilityMap(new EngineCells.Rect(0, 0, width, height)))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_Rejects_Coordinate_Overflow()
    {
        await Assert.That(() => new VisibilityMap(new EngineCells.Rect(int.MaxValue - 1, 0, 4, 1)))
            .Throws<ArgumentOutOfRangeException>();
    }
}
