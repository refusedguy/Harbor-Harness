using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Boundary coverage for the pure edge-band geometry (issue #2493). The
/// engine Rect is named explicitly because the test project aliases the
/// historical Rendering Rect as <c>Rect</c>.
/// </summary>
public class AutoScrollZonesTests
{
    [Test]
    public async Task Viewport_ProducesTwoNonOverlappingBands()
    {
        var zones = new AutoScrollZones(new EngineCells.Rect(0, 0, 80, 24), 3);

        await Assert.That(zones.IsEmpty).IsFalse();
        await Assert.That(zones.Up.X).IsEqualTo(0);
        await Assert.That(zones.Up.Y).IsEqualTo(0);
        await Assert.That(zones.Up.Width).IsEqualTo(80);
        await Assert.That(zones.Up.Height).IsEqualTo(3);
        await Assert.That(zones.Down.X).IsEqualTo(0);
        await Assert.That(zones.Down.Y).IsEqualTo(21);
        await Assert.That(zones.Down.Width).IsEqualTo(80);
        await Assert.That(zones.Down.Height).IsEqualTo(3);
        await Assert.That(zones.Up.Bottom <= zones.Down.Y).IsTrue();
    }

    [Test]
    public async Task LinesLargerThanHalfViewport_AreClampedWithoutOverlap()
    {
        var zones = new AutoScrollZones(new EngineCells.Rect(0, 0, 10, 5), 5);

        await Assert.That(zones.Up.Y).IsEqualTo(0);
        await Assert.That(zones.Up.Height).IsEqualTo(2);
        await Assert.That(zones.Down.Y).IsEqualTo(3);
        await Assert.That(zones.Down.Height).IsEqualTo(2);
        await Assert.That(zones.Up.Bottom <= zones.Down.Y).IsTrue();
        await Assert.That(zones.ShiftFor(0, 2)).IsEqualTo(0);
    }

    [Test]
    public async Task BandBoundaries_AreInclusiveAtStartExclusiveAtEnd()
    {
        var zones = new AutoScrollZones(new EngineCells.Rect(0, 0, 80, 24), 3);

        await Assert.That(zones.IsUp(0, 0)).IsTrue();
        await Assert.That(zones.IsUp(79, 2)).IsTrue();
        await Assert.That(zones.IsUp(0, 3)).IsFalse();
        await Assert.That(zones.IsUp(80, 2)).IsFalse();
        await Assert.That(zones.IsUp(-1, 2)).IsFalse();
        await Assert.That(zones.IsUp(0, -1)).IsFalse();

        await Assert.That(zones.IsDown(0, 21)).IsTrue();
        await Assert.That(zones.IsDown(79, 23)).IsTrue();
        await Assert.That(zones.IsDown(0, 20)).IsFalse();
        await Assert.That(zones.IsDown(0, 24)).IsFalse();
        await Assert.That(zones.IsDown(80, 23)).IsFalse();
        await Assert.That(zones.IsDown(-1, 23)).IsFalse();
    }

    [Test]
    public async Task ZeroSizedViewportOrLines_ProduceEmptyZones()
    {
        var defaults = new AutoScrollZones(new EngineCells.Rect(0, 0, 80, 24), 3);
        await Assert.That(defaults.IsEmpty).IsFalse();

        await Assert.That(new AutoScrollZones(default, 3).IsEmpty).IsTrue();
        await Assert.That(new AutoScrollZones(new EngineCells.Rect(0, 0, 0, 24), 3).IsEmpty).IsTrue();
        await Assert.That(new AutoScrollZones(new EngineCells.Rect(0, 0, 80, 0), 3).IsEmpty).IsTrue();
        await Assert.That(new AutoScrollZones(new EngineCells.Rect(0, 0, 80, 1), 3).IsEmpty).IsTrue();
        await Assert.That(new AutoScrollZones(new EngineCells.Rect(0, 0, 80, 24), 0).IsEmpty).IsTrue();
        await Assert.That(new AutoScrollZones(new EngineCells.Rect(0, 0, 80, 24), -1).IsEmpty).IsTrue();
        await Assert.That(new AutoScrollZones(default, 3).ShiftFor(0, 0)).IsEqualTo(0);
    }

    [Test]
    public async Task ShiftFor_ReturnsSignedDirectionOnlyInsideBands()
    {
        var zones = new AutoScrollZones(new EngineCells.Rect(0, 0, 80, 24), 3);

        await Assert.That(zones.ShiftFor(0, 0)).IsEqualTo(-1);
        await Assert.That(zones.ShiftFor(79, 2)).IsEqualTo(-1);
        await Assert.That(zones.ShiftFor(0, 23)).IsEqualTo(1);
        await Assert.That(zones.ShiftFor(79, 21)).IsEqualTo(1);
        await Assert.That(zones.ShiftFor(0, 3)).IsEqualTo(0);
        await Assert.That(zones.ShiftFor(0, 20)).IsEqualTo(0);
        await Assert.That(zones.ShiftFor(80, 0)).IsEqualTo(0);
    }

    [Test]
    public async Task OffsetViewport_BandsFollowItsOrigin()
    {
        var zones = new AutoScrollZones(new EngineCells.Rect(5, 10, 20, 10), 2);

        await Assert.That(zones.Up.X).IsEqualTo(5);
        await Assert.That(zones.Up.Y).IsEqualTo(10);
        await Assert.That(zones.Up.Height).IsEqualTo(2);
        await Assert.That(zones.Down.X).IsEqualTo(5);
        await Assert.That(zones.Down.Y).IsEqualTo(18);
        await Assert.That(zones.Down.Height).IsEqualTo(2);
        await Assert.That(zones.ShiftFor(5, 10)).IsEqualTo(-1);
        await Assert.That(zones.ShiftFor(24, 19)).IsEqualTo(1);
        await Assert.That(zones.ShiftFor(5, 12)).IsEqualTo(0);
        await Assert.That(zones.ShiftFor(4, 10)).IsEqualTo(0);
    }
}
