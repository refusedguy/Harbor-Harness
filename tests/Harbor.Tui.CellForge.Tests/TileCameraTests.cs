using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Pins the pure 2D camera arithmetic introduced for CellForge tile worlds:
/// viewport math, bounds clamping, deadzone following and coordinate conversion
/// stay in the engine while the following strategy remains with the game.
/// </summary>
public class TileCameraTests
{
    private static TileCamera NewCamera() => new(80, 24);

    [Test]
    public async Task New_Camera_Starts_At_World_Origin()
    {
        var camera = NewCamera();

        await Assert.That(camera.OriginX).IsEqualTo(0);
        await Assert.That(camera.OriginY).IsEqualTo(0);
        await Assert.That(camera.ViewportWidth).IsEqualTo(80);
        await Assert.That(camera.ViewportHeight).IsEqualTo(24);
        await Assert.That(camera.View).IsEqualTo(new EngineCells.Rect(0, 0, 80, 24));
        await Assert.That(camera.IsVisible(0, 0)).IsTrue();
        await Assert.That(camera.IsVisible(79, 23)).IsTrue();
        await Assert.That(camera.IsVisible(80, 0)).IsFalse();
        await Assert.That(camera.IsVisible(-1, 0)).IsFalse();
    }

    [Test]
    [Arguments(0, 24)]
    [Arguments(80, 0)]
    [Arguments(-1, 24)]
    public async Task Constructor_Rejects_NonPositive_Viewport(int width, int height)
    {
        await Assert.That(() => new TileCamera(width, height))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task World_And_Screen_Coordinates_Roundtrip_With_Negative_Origin()
    {
        var camera = NewCamera();
        camera.MoveTo(-10, -5);

        var screen = camera.WorldToScreen(-3, -2);
        await Assert.That(screen.X).IsEqualTo(7);
        await Assert.That(screen.Y).IsEqualTo(3);

        var world = camera.ScreenToWorld(screen.X, screen.Y);
        await Assert.That(world.X).IsEqualTo(-3);
        await Assert.That(world.Y).IsEqualTo(-2);
    }

    [Test]
    public async Task MoveTo_Reports_Whether_The_Origin_Changed()
    {
        var camera = NewCamera();

        await Assert.That(camera.MoveTo(0, 0)).IsFalse();
        await Assert.That(camera.MoveTo(12, -7)).IsTrue();
        await Assert.That(camera.OriginX).IsEqualTo(12);
        await Assert.That(camera.OriginY).IsEqualTo(-7);
        await Assert.That(camera.View).IsEqualTo(new EngineCells.Rect(12, -7, 80, 24));
    }

    [Test]
    public async Task ClampTo_Keeps_The_View_Inside_A_Larger_World()
    {
        var camera = NewCamera();
        var world = new EngineCells.Rect(0, 0, 200, 100);

        camera.MoveTo(-5, -5);
        await Assert.That(camera.ClampTo(world)).IsTrue();
        await Assert.That(camera.OriginX).IsEqualTo(0);
        await Assert.That(camera.OriginY).IsEqualTo(0);

        camera.MoveTo(500, 500);
        await Assert.That(camera.ClampTo(world)).IsTrue();
        await Assert.That(camera.OriginX).IsEqualTo(120);
        await Assert.That(camera.OriginY).IsEqualTo(76);

        await Assert.That(camera.ClampTo(world)).IsFalse();
    }

    [Test]
    public async Task ClampTo_Aligns_When_The_World_Is_Smaller_Than_The_Viewport()
    {
        var camera = NewCamera();

        await Assert.That(camera.ClampTo(new EngineCells.Rect(10, 20, 40, 10))).IsTrue();
        await Assert.That(camera.OriginX).IsEqualTo(10);
        await Assert.That(camera.OriginY).IsEqualTo(20);

        await Assert.That(camera.ClampTo(new EngineCells.Rect(-50, -50, 20, 20))).IsTrue();
        await Assert.That(camera.OriginX).IsEqualTo(-50);
        await Assert.That(camera.OriginY).IsEqualTo(-50);
    }

    [Test]
    [Arguments(0, 10)]
    [Arguments(10, 0)]
    [Arguments(-1, 10)]
    public async Task ClampTo_Rejects_Empty_World_Bounds(int width, int height)
    {
        var camera = NewCamera();

        await Assert.That(() => camera.ClampTo(new EngineCells.Rect(0, 0, width, height)))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Follow_Scrolls_Only_When_The_Target_Leaves_The_Deadzone()
    {
        var camera = NewCamera();

        await Assert.That(camera.Follow(30, 10, 40, 12)).IsFalse();
        await Assert.That(camera.OriginX).IsEqualTo(0);
        await Assert.That(camera.OriginY).IsEqualTo(0);

        await Assert.That(camera.Follow(100, 10, 40, 12)).IsTrue();
        await Assert.That(camera.OriginX).IsEqualTo(41);
        await Assert.That(camera.OriginY).IsEqualTo(0);

        await Assert.That(camera.Follow(50, 10, 40, 12)).IsTrue();
        await Assert.That(camera.OriginX).IsEqualTo(30);

        await Assert.That(camera.Follow(35, 100, 40, 12)).IsTrue();
        await Assert.That(camera.OriginX).IsEqualTo(15);
        await Assert.That(camera.OriginY).IsEqualTo(83);
    }

    [Test]
    public async Task Follow_With_A_Full_Viewport_Deadzone_Does_Not_Move()
    {
        var camera = NewCamera();

        await Assert.That(camera.Follow(5, 5, 80, 24)).IsFalse();
        await Assert.That(camera.OriginX).IsEqualTo(0);
        await Assert.That(camera.OriginY).IsEqualTo(0);
    }

    [Test]
    public async Task Follow_With_Zero_Deadzone_Centers_The_Target()
    {
        var camera = NewCamera();

        await Assert.That(camera.Follow(100, 50, 0, 0)).IsTrue();
        await Assert.That(camera.OriginX).IsEqualTo(60);
        await Assert.That(camera.OriginY).IsEqualTo(38);

        var screen = camera.WorldToScreen(100, 50);
        await Assert.That(screen.X).IsEqualTo(40);
        await Assert.That(screen.Y).IsEqualTo(12);
    }

    [Test]
    [Arguments(-1, 12)]
    [Arguments(81, 12)]
    [Arguments(40, -1)]
    [Arguments(40, 25)]
    public async Task Follow_Rejects_Deadzones_Outside_The_Viewport(int deadzoneX, int deadzoneY)
    {
        var camera = NewCamera();

        await Assert.That(() => camera.Follow(10, 10, deadzoneX, deadzoneY))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Follow_Does_Not_Assume_A_Finite_World_And_Composes_With_ClampTo()
    {
        var camera = NewCamera();
        var world = new EngineCells.Rect(0, 0, 200, 100);

        await Assert.That(camera.Follow(190, 95, 0, 0)).IsTrue();
        await Assert.That(camera.OriginX).IsEqualTo(150);
        await Assert.That(camera.OriginY).IsEqualTo(83);

        await Assert.That(camera.ClampTo(world)).IsTrue();
        await Assert.That(camera.OriginX).IsEqualTo(120);
        await Assert.That(camera.OriginY).IsEqualTo(76);
    }

    [Test]
    public async Task Extreme_Coordinates_Do_Not_Overflow()
    {
        var camera = NewCamera();

        await Assert.That(camera.Follow(int.MaxValue, int.MinValue, 0, 0)).IsTrue();
        await Assert.That(camera.OriginX).IsEqualTo(int.MaxValue - 40);
        await Assert.That(camera.OriginY).IsEqualTo(int.MinValue);
    }
}
