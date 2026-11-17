using Harbor.Tui.CellForge.Input;

// #436 dual Rect vocabulary: the test project aliases Rect to UIR globally,
// so MouseRouter.Bind needs the engine's Rect under its own name.
using EngineRect = Harbor.Tui.CellForge.Rendering.Rect;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #1110 z-order row (landed via #1404): overlapping pointer regions must
/// resolve to the last-bound, topmost target, and a rebind must lift its
/// target above the old top. These tests pin that ordering so a future edit
/// cannot silently reintroduce first-match routing.
/// </summary>
public class MouseRouterZOrderTests
{
    private sealed class Target : IPointerTarget
    {
        public Target(string id) => Id = id;

        public string Id { get; }
        public int Presses { get; private set; }

        public void OnPress(int col, int row) => Presses++;

        public void OnRelease(int col, int row)
        {
        }

        public void OnWheel(int col, int row, int delta)
        {
        }
    }

    [Test]
    public async Task HitTest_Overlap_RoutesToLastBoundTarget()
    {
        var router = new MouseRouter(80, 24);
        var bottom = new Target("bottom");
        var top = new Target("top");
        router.Bind(bottom, new EngineRect(0, 0, 40, 10));
        router.Bind(top, new EngineRect(0, 0, 40, 10));

        router.Press(5, 5);

        await Assert.That(top.Presses).IsEqualTo(1);
        await Assert.That(bottom.Presses).IsEqualTo(0);
    }

    [Test]
    public async Task Rebind_LiftsTargetToTop()
    {
        var router = new MouseRouter(80, 24);
        var first = new Target("first");
        var second = new Target("second");
        router.Bind(first, new EngineRect(0, 0, 40, 10));
        router.Bind(second, new EngineRect(0, 0, 40, 10));

        router.Rebind(first, new EngineRect(0, 0, 40, 10));
        router.Press(5, 5);

        await Assert.That(first.Presses).IsEqualTo(1);
        await Assert.That(second.Presses).IsEqualTo(0);
    }
}
