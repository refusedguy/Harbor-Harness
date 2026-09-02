using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Leader-key chords (ctrl+x pattern): arming, chord resolution inside the
/// timeout window, unknown-chord disarm, and pass-through while unarmed.
/// </summary>
public class LeaderKeyRouterTests
{
    private static KeyEvent Plain(char c) => KeyEvent.Char(new Rune(c));
    private static KeyEvent Leader() => KeyEvent.Char(new Rune('x'), KeyModifiers.Ctrl);

    [Test]
    public async Task HandleKey_Unarmed_NonLeaderPassesThrough()
    {
        var router = new LeaderKeyRouter();

        await Assert.That(router.HandleKey(Plain('g'), nowMs: 0)).IsFalse();
        await Assert.That(router.IsPending).IsFalse();
    }

    [Test]
    public async Task HandleKey_LeaderPress_ArmsRouter()
    {
        var router = new LeaderKeyRouter();

        await Assert.That(router.HandleKey(Leader(), nowMs: 0)).IsTrue();
        await Assert.That(router.IsPending).IsTrue();
    }

    [Test]
    public async Task HandleKey_BoundChord_FiresInsideWindow()
    {
        var router = new LeaderKeyRouter();
        int fired = 0;
        router.Bind('g', () => fired++);

        _ = router.HandleKey(Leader(), nowMs: 0);
        _ = router.HandleKey(Plain('G'), nowMs: 500); // case-insensitive

        await Assert.That(fired).IsEqualTo(1);
        await Assert.That(router.IsPending).IsFalse();
    }

    [Test]
    public async Task HandleKey_ChordAfterTimeout_DoesNotFire()
    {
        var router = new LeaderKeyRouter();
        int fired = 0;
        router.Bind('g', () => fired++);

        _ = router.HandleKey(Leader(), nowMs: 0);
        _ = router.HandleKey(Plain('g'), nowMs: LeaderKeyRouter.TimeoutMs + 1);

        await Assert.That(fired).IsEqualTo(0);
        await Assert.That(router.IsPending).IsFalse();
    }

    [Test]
    public async Task HandleKey_UnknownChord_DisarmsSilently()
    {
        var router = new LeaderKeyRouter();
        int fired = 0;
        router.Bind('g', () => fired++);

        _ = router.HandleKey(Leader(), nowMs: 0);
        _ = router.HandleKey(Plain('z'), nowMs: 100);
        _ = router.HandleKey(Plain('g'), nowMs: 200); // router disarmed — passes through

        await Assert.That(fired).IsEqualTo(0);
    }

    [Test]
    public async Task HandleKey_ModifiedKeyWhileArmed_ConsumesAndDisarms()
    {
        var router = new LeaderKeyRouter();

        _ = router.HandleKey(Leader(), nowMs: 0);
        await Assert.That(router.HandleKey(KeyEvent.Simple(KeyCode.Enter), nowMs: 100)).IsTrue();
        await Assert.That(router.IsPending).IsFalse();
    }

    [Test]
    public async Task Bind_SameChord_Rebinds()
    {
        var router = new LeaderKeyRouter();
        int first = 0, second = 0;
        router.Bind('g', () => first++);
        router.Bind('g', () => second++);

        _ = router.HandleKey(Leader(), nowMs: 0);
        _ = router.HandleKey(Plain('g'), nowMs: 100);

        await Assert.That(first).IsEqualTo(0);
        await Assert.That(second).IsEqualTo(1);
    }
}
