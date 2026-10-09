using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — session switch during a running turn: <c>/new</c>
///     while the agent runs is rejected with a marker (the rebind path aborts
///     the in-flight agent first, so a mid-turn switch is refused by design —
///     see <c>NewSessionCommand</c>); once idle, <c>/new</c> opens a fresh
///     session whose timeline is cleared and keeps working. Marker asserts
///     only (streaming cadence is nondeterministic — celldiff §8).
/// </summary>
[NotInParallel("pty")]
public sealed class SessionSwitchMidTurnScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(120_000)]
    public async Task NewSession_WhileRunning_Rejected_ThenFreshSessionWorks()
    {
        // ~800 chars at the mock's 4-chars/50ms cadence ≈ a ~10 s turn.
        Server.SetResponse("test-model", new string('х', 800));
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("first turn");

        // The turn is running: the mock streams, the grid shows progress.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("хх", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // /new mid-turn is refused — the in-flight turn keeps its session.
        SubmitLine("/new");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("Cannot create session while running", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        // Idle /new opens a fresh session and clears the timeline.
        SubmitLine("/new");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("Started fresh session:", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        Server.SetEchoResponse("test-model");
        SubmitLine("second turn");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("echo-", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // Fresh timeline: the second answer is there, the first turn's long
        // rune-run is gone (no cross-session bleed; triple-х never occurs in UI chrome).
        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("echo-", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("ххх", StringComparison.Ordinal))).IsFalse().Because($"screen:\n{ScreenText}");
    }
}
