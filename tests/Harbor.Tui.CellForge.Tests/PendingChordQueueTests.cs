using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #2359: the one pending-chord queue. Timing, drain-once staging and the
/// shared 2000 ms window that LeaderKeyRouter now uses instead of its own arm
/// field.
/// </summary>
public class PendingChordQueueTests
{
    [Test]
    public async Task TryArm_ResolveInsideWindow()
    {
        var queue = new PendingChordQueue<string>();

        await Assert.That(queue.IsPending).IsFalse();
        await Assert.That(queue.TryArm(100)).IsTrue();
        await Assert.That(queue.IsPending).IsTrue();
        await Assert.That(queue.IsWithinWindow(100 + PendingChordQueue<string>.TimeoutMs)).IsTrue();
        await Assert.That(queue.TryResolve(100 + PendingChordQueue<string>.TimeoutMs)).IsTrue();
        await Assert.That(queue.IsPending).IsFalse();
    }

    [Test]
    public async Task TryResolve_AfterWindow_ExpiresAndDisarms()
    {
        var queue = new PendingChordQueue<string>();
        _ = queue.TryArm(100);
        long late = 100 + PendingChordQueue<string>.TimeoutMs + 1;

        await Assert.That(queue.IsWithinWindow(late)).IsFalse();
        await Assert.That(queue.TryResolve(late)).IsFalse();
        await Assert.That(queue.IsPending).IsFalse();
    }

    [Test]
    public async Task TryArm_WhilePending_Fails()
    {
        var queue = new PendingChordQueue<string>();
        _ = queue.TryArm(0);

        await Assert.That(queue.TryArm(10)).IsFalse();
    }

    [Test]
    public async Task Cancel_ClearsPendingOnlyOnce()
    {
        var queue = new PendingChordQueue<string>();
        _ = queue.TryArm(0);

        await Assert.That(queue.Cancel()).IsTrue();
        await Assert.That(queue.Cancel()).IsFalse();
        await Assert.That(queue.IsPending).IsFalse();
    }

    [Test]
    public async Task Stage_Take_DrainsExactlyOnce()
    {
        var queue = new PendingChordQueue<string>();
        queue.Stage("msg");

        await Assert.That(queue.Pending).IsEqualTo("msg");
        await Assert.That(queue.Take()).IsEqualTo("msg");
        await Assert.That(queue.Take()).IsNull();
        await Assert.That(queue.Pending).IsNull();
    }

    [Test]
    public async Task Clear_DropsArmAndStagedResult()
    {
        var queue = new PendingChordQueue<string>();
        _ = queue.TryArm(0);
        queue.Stage("msg");

        queue.Clear();

        await Assert.That(queue.IsPending).IsFalse();
        await Assert.That(queue.Pending).IsNull();
    }

    [Test]
    public async Task LeaderRouter_UsesSharedTimeout()
    {
        await Assert.That(LeaderKeyRouter.TimeoutMs).IsEqualTo(PendingChordQueue<string>.TimeoutMs);
    }
}
