using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Core;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     The screen wait ends on the STATE, not only on the deadline (#2001).
///     <para />
///     The wait used to run out its whole 10 s diagnostic window whenever the
///     condition was never met, including the case where it CANNOT be met
///     any more: the child exited and the pump drained its output, so no
///     further repaint can arrive. That is the shape of a mass failure (13
///     scenarios at once, every one of them waiting out the bound, serialized
///     behind the assembly's two-permit limiter), and the bound was the bill
///     rather than the cause.
///     <para />
///     These tests pin the three halves of the change:
///     <list type="number">
///         <item>
///             <description>
///                 A wait for a repaint that cannot arrive ends FAST and says
///                 WHY: the process exited, so there will be no repaint.
///             </description>
///         </item>
///         <item>
///             <description>
///                 A wait for a condition the dead child DID paint still
///                 succeeds: the predicate is evaluated against the final grid
///                 before the terminal state is read, so the change can only
///                 make a failing scenario fail sooner.
///             </description>
///         </item>
///         <item>
///             <description>
///                 A LIVE child that does not paint the condition still waits
///                 out the bound: the deadline stays the emergency limit for a
///                 child that is alive and merely quiet.
///             </description>
///         </item>
///     </list>
/// </summary>
public sealed class PtyScreenWaitTerminationTests : CellForgePtyScenarioBase
{
    /// <summary>The status bar's model line. Painted by the app, and still there after it dies.</summary>
    private const string ModelMarker = "model: mock/test-model";

    /// <summary>A marker the app never paints, so the wait can only end by timeout or by the exit.</summary>
    private const string NeverPaintedMarker = "marker-this-app-never-paints-9f3c";

    /// <summary>
    ///     The bound the wait is expected NOT to reach. The terminal state is
    ///     published within a pump idle cap (100 ms) of the child's death, so
    ///     a wait that ends in seconds is waiting for the DEADLINE, not for
    ///     the state. Three seconds against a ten-second bound is the
    ///     margin: an order of magnitude above the terminal latency, well
    ///     below the bound it replaces.
    /// </summary>
    private static readonly TimeSpan FastFailureBound = TimeSpan.FromSeconds(3);

    [Test]
    [Timeout(60_000)]
    public async Task Wait_For_A_Repaint_After_The_Child_Exited_Ends_Fast_And_Says_Why()
    {
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains(ModelMarker, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // SIGKILL: the child dies without painting anything else. No cleanup
        // handler runs, so there is no repaint on the way out either.
        Session.Kill();
        int exit = await Session.WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(137); // 128 + SIGKILL

        var sw = Stopwatch.StartNew();
        PtyChildExitedException error = await Assert.ThrowsAsync<PtyChildExitedException>(
            async () => await WaitForScreenAsync(
                l => l.Any(x => x.Contains(NeverPaintedMarker, StringComparison.Ordinal))).ConfigureAwait(false));
        sw.Stop();

        TestContext.Current?.Output.WriteLine(
            $"pty screen wait after the child's exit: {sw.Elapsed.TotalMilliseconds:F0} ms "
            + $"(the bound it replaced is {TimeSpan.FromMilliseconds(10_000).TotalMilliseconds:F0} ms)");

        await Assert.That(sw.Elapsed).IsLessThan(FastFailureBound)
            .Because(
                "the child exited and its output is drained, so no repaint can arrive: the wait is "
                + "already decided and must end on that state, not on the ten-second diagnostic window.");

        await Assert.That(error.Message).Contains("exited")
            .Because(
                "the failure has to say WHICH state ended the wait: a timeout means the child is still "
                + "out there and did not paint the condition, and the child-exited case means waiting "
                + "longer is not a fix.");
        await Assert.That(error.Message).Contains("no further repaint can arrive")
            .Because(
                "the message has to say that the wait cannot be satisfied by waiting, or the reader "
                + "cannot tell the two failures apart.");
        await Assert.That(error.Message).Contains("expected:")
            .Because(
                "the message quotes the condition that was not met, so the reader knows what the "
                + "scenario was waiting for.");
    }

    [Test]
    [Timeout(60_000)]
    public async Task Wait_For_A_Condition_The_Final_Grid_Already_Holds_Still_Succeeds_After_The_Child_Exited()
    {
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains(ModelMarker, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        Session.Kill();
        int exit = await Session.WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(137);

        // The condition the dead child DID paint is still a pass: the
        // predicate is evaluated against the final grid BEFORE the terminal
        // state is read, so the change cannot turn a green scenario red.
        string[] lines = await WaitForScreenAsync(
            l => l.Any(x => x.Contains(ModelMarker, StringComparison.Ordinal))).ConfigureAwait(false);

        await Assert.That(lines.Any(x => x.Contains(ModelMarker, StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Timeout(60_000)]
    public async Task A_Live_Child_That_Does_Not_Paint_The_Condition_Still_Waits_Out_The_Bound()
    {
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains(ModelMarker, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // The child is alive and idle: the condition cannot arrive, and the
        // bound is the emergency limit that still applies to a live child.
        var sw = Stopwatch.StartNew();
        TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(
            async () => await WaitForScreenAsync(
                l => l.Any(x => x.Contains(NeverPaintedMarker, StringComparison.Ordinal)),
                TimeSpan.FromSeconds(1)).ConfigureAwait(false));
        sw.Stop();

        await Assert.That(error.GetType()).IsEqualTo(typeof(TimeoutException))
            .Because(
                "the child is ALIVE, so the wait ended on the bound and not on the terminal state: the "
                + "exact type says so, where a subclass would hide it.");

        await Assert.That(sw.Elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(1))
            .Because(
                "the wait ran the whole bound it was given: the deadline is still the emergency limit "
                + "for a child that is alive and quiet.");
        await Assert.That(sw.Elapsed).IsLessThan(FastFailureBound)
            .Because(
                "the bound of this wait is one second, so the wait cannot have run longer than it: the "
                + "assertion above is about the bound being honoured, not about its size.");
    }
}
