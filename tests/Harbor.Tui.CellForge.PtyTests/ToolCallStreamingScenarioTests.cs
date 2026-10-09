using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — tool-call streaming into the timeline: the mock answers
///     with a <c>read</c> tool call on an isolated-<c>$HOME</c> probe file. Paths
///     outside the workspace resolve to an approval prompt (workspace
///     confinement), so the test approves via the prompt's own
///     <c>[y]/[n]/[a]</c> keys — <c>a</c> (always allow) persists for the run —
///     then the call card, its file argument and the executed result stream
///     into the grid, and the turn settles at idle after the seeded
///     <c>maxSteps</c> cap. Marker asserts only (streaming cadence is
///     nondeterministic — celldiff §8).
/// </summary>
[NotInParallel("pty")]
public sealed class ToolCallStreamingScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(90_000)]
    public async Task ToolCall_ReadCardStreamsArgsResult_AndTurnSettles()
    {
        // Probe file inside the isolated $HOME; content lines are unique markers.
        string probe = Path.Combine(TempHome, "pty-tool-probe-423.txt");
        await File.WriteAllTextAsync(probe, "pty-probe-line-alpha-423\npty-probe-line-beta-423\n").ConfigureAwait(false);

        // Cap the tool-call loop: the mock re-serves the canned call per request.
        WriteHomeConfig("""
            {
              "provider": "mock",
              "model": "mock/test-model",
              "agent": "code",
              "onboarded": true,
              "maxSteps": 5
            }
            """);
        Server.SetToolCallResponse("test-model", "read", new { path = probe });

        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("read the probe file");

        // The out-of-workspace path raises the approval prompt; approve with
        // "a" (always allow, remembered for the run). Re-prompt tolerant: if a
        // later iteration prompts again, approve again until content flows.
        var approvalDeadline = TimeSpan.FromSeconds(40);
        var approvalSw = System.Diagnostics.Stopwatch.StartNew();
        while (approvalSw.Elapsed < approvalDeadline)
        {
            string[] snap = NormalizedLines();
            if (snap.Any(x => x.Contains("pty-probe-line-alpha-423", StringComparison.Ordinal)))
            {
                break;
            }

            if (snap.Any(x => x.Contains("permission required", StringComparison.Ordinal)))
            {
                Session.SendKey("a");
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        // The tool card streamed with the call name and the executed result.
        // Filename matching is viewport-fragile (the 30-row grid scrolls past
        // it under the maxSteps loop) — content lines are the stable marker.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("read", StringComparison.Ordinal))
                && l.Any(x => x.Contains("pty-probe-line-alpha-423", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // ...the tool executed for real: the file content reaches the timeline.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("pty-probe-line-alpha-423", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // Turn settles back at idle after the maxSteps cap ends the loop.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("pty-probe-line-alpha-423", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(Session.RawText.Contains("pty-tool-probe-423", StringComparison.Ordinal)).IsTrue().Because("tool card args must reference the probe file");
        await Assert.That(Server.ReceivedRequests.Count).IsGreaterThanOrEqualTo(2).Because("tool loop must issue follow-up requests");
    }
}
