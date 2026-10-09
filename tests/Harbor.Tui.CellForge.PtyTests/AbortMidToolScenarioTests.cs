using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — abort mid-tool-execution: the mock answers every
///     request with a <c>read</c> tool call, so the turn stays inside tool
///     execution for up to <c>maxSteps</c> iterations; Ctrl+C in that window
///     must abort the turn exactly like it aborts a streaming turn (see
///     <see cref="CtrlCScenarioTests" />). Marker asserts only (celldiff §8).
/// </summary>
[NotInParallel("pty")]
public sealed class AbortMidToolScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(60_000)]
    public async Task CtrlC_DuringToolLoop_AbortsTurn_AndReturnsIdle()
    {
        string probe = Path.Combine(TempHome, "pty-abort-probe-423.txt");
        await File.WriteAllTextAsync(probe, "pty-abort-content-423\n").ConfigureAwait(false);

        // Default maxSteps (50): the tool loop is a wide abort window.
        Server.SetToolCallResponse("test-model", "read", new { path = probe });
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("read the probe in a loop");

        // The first tool card proves the turn is inside tool execution.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("read", StringComparison.Ordinal))
                && l.Any(x => x.Contains("args:", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        SendCtrlC();
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("^C — прерываю текущий ход…", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("ход прерван", StringComparison.Ordinal) || x.Contains("The operation was canceled", StringComparison.Ordinal) || x.Contains("Operation was canceled", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // The aborted card stays on the timeline with its args preview.
        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("args:", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
    }
}
