using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — abort mid-tool-execution: the mock answers every
///     request with a <c>read</c> tool call, so the turn stays inside tool
///     execution for up to <c>maxSteps</c> iterations. The first iteration
///     raises the approval prompt (out-of-workspace path); <c>a</c> (always
///     allow) frees the loop, and Ctrl+C immediately after the first executed
///     result lands mid-loop — aborting exactly like a streaming turn (see
///     <see cref="CtrlCScenarioTests" />). Marker asserts only (celldiff §8).
/// </summary>
[NotInParallel("pty")]
public sealed class AbortMidToolScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(90_000)]
    public async Task CtrlC_DuringToolLoop_AbortsTurn_AndReturnsIdle()
    {
        string probe = Path.Combine(TempHome, "pty-abort-probe-423.txt");
        await File.WriteAllTextAsync(probe, "pty-abort-content-423\n").ConfigureAwait(false);

        // Default maxSteps (50): the freed tool loop is a wide abort window.
        Server.SetToolCallResponse("test-model", "read", new { path = probe });
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("read the probe in a loop");

        // Free the loop at the approval prompt, tolerating re-prompts.
        var approvalDeadline = TimeSpan.FromSeconds(30);
        var approvalSw = System.Diagnostics.Stopwatch.StartNew();
        while (approvalSw.Elapsed < approvalDeadline)
        {
            string[] snap = NormalizedLines();
            if (snap.Any(x => x.Contains("pty-abort-content-423", StringComparison.Ordinal)))
            {
                break;
            }

            if (snap.Any(x => x.Contains("permission required", StringComparison.Ordinal)))
            {
                Session.SendKey("a");
            }

            await Task.Delay(300).ConfigureAwait(false);
        }

        // First executed result proves the free-running loop — abort NOW, deep inside it.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("pty-abort-content-423", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
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

        // The aborted card stays on the timeline with the probe path.
        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("pty-abort-probe-423", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
    }
}
