using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — tool-call streaming into the timeline: the mock answers
///     with a <c>read</c> tool call (the one file tool allowed everywhere, so no
///     permission prompt can interleave), the call card streams into the PTY grid
///     with its name and args preview, the tool executes for real, and the turn
///     keeps going until the seeded <c>maxSteps</c> cap ends it back at idle.
///     Marker asserts only (streaming cadence is nondeterministic — celldiff §8).
/// </summary>
[NotInParallel("pty")]
public sealed class ToolCallStreamingScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(60_000)]
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

        // The tool card streams into the grid: name + args preview row.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("read", StringComparison.Ordinal))
                && l.Any(x => x.Contains("args:", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // The args preview carries the probe path, so the card is bound to THIS call.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("pty-tool-probe-423", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // The tool executed for real: the file content reaches the timeline.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("pty-probe-line-alpha-423", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // Turn settles back at idle after the maxSteps cap ends the loop.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("pty-tool-probe-423", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(Server.ReceivedRequests.Count).IsGreaterThanOrEqualTo(2).Because("tool loop must issue follow-up requests");
    }
}
