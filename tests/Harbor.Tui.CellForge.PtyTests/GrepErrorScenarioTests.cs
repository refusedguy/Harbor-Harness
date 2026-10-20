using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — grep-error path through the PTY: the mock answers
///     with a <c>grep</c> tool call whose <c>path</c> does not exist on disk,
///     so the tool fails deterministically with "Path not found" (see
///     <c>GrepTool.ExecuteCore</c> — neither <c>File.Exists</c> nor
///     <c>Directory.Exists</c> holds, so <c>ExecuteAsync</c> surfaces
///     <c>ToolResult.Error</c>; the pattern itself is a valid regex, so the
///     failure is specifically the missing-path branch, not "Invalid regex").
///     The out-of-workspace path raises the approval prompt (workspace
///     confinement downgrades Allow to Ask), so the test drives the prompt's
///     own <c>[y]/[n]/[a]</c> keys — <c>a</c> (always allow) is remembered for
///     the run — then switches the mock to a plain text response so follow-up
///     turns settle instead of re-executing the canned call (the mock
///     re-serves per request, and the home-config <c>maxSteps</c> budget ends
///     the run now — #1118 — so the budget is set to the two turns this
///     scenario needs). The failed call completes the card in
///     the error state (glyph <c>✖</c>, symmetric to the <c>✔</c> success
///     header asserted by #1122), the run goes idle, and nothing appears on
///     disk. The error BODY ("Path not found: …") is deliberately not
///     asserted: like the success bodies in #1122, it does not paint on the
///     settled timeline (stale 1-row layout when the card is not last — see
///     #1137). Same prompt-driving pattern as
///     <c>ToolErrorScenarioTests</c> / <c>PermissionDenyScenarioTests</c>
///     (#1163), <c>WriteErrorScenarioTests</c> (#1388),
///     <c>EditErrorScenarioTests</c> (#1403) and
///     <c>PatchErrorScenarioTests</c> (#1411). Only tests; the product is
///     untouched.
/// </summary>
public sealed class GrepErrorScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(120_000)]
    public async Task GrepCall_MissingPath_StreamsError_AndTurnSettles()
    {
        // Probe path that does not exist — unique marker, never created.
        string probe = Path.Combine(TempHome, "pty-greperr-probe-423f.txt");

        WriteHomeConfig("""
            {
              "provider": "mock",
              "model": "mock/test-model",
              "agent": "code",
              "onboarded": true,
              "maxSteps": 2
            }
            """);
        Server.SetToolCallResponse("test-model", "grep", new
        {
            pattern = "needle-423f",
            path = probe
        });

        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("search the missing probe path");

        // Approve with "a" (remembered for the run), then stop the tool flood
        // at the mock so the run settles after this one execution. Re-prompt
        // tolerant: approve again until the follow-up text lands.
        bool approved = await AnswerApprovalGateAsync(
            "permission required",
            "probe-greperr-done-423f",
            TimeSpan.FromSeconds(30),
            answerKey: "a",
            onFirstAnswer: () => Server.SetResponse("test-model", "probe-greperr-done-423f")).ConfigureAwait(false);

        await Assert.That(approved).IsTrue().Because($"approval prompt never appeared:\n{ScreenText}");

        // The follow-up turn landed as text, then the run went idle.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("probe-greperr-done-423f", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // The failed call completed as an error: the card header repainted to
        // the error glyph (the result body itself does not paint on the
        // settled timeline — same stale-layout note as #1137/#1122 — so the
        // header glyph plus the args reference are the stable surface).
        await Assert.That(Session.RawText.Contains("✖", StringComparison.Ordinal)).IsTrue().Because("tool card must complete in the error state");
        await Assert.That(Session.RawText.Contains("pty-greperr-probe-423f", StringComparison.Ordinal)).IsTrue().Because("tool card args must reference the probe path");

        // The approval trail survived on the settled timeline (same marker
        // as the #1122 diff trio), next to the errored grep card.
        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("✓ approved (always)", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("grep", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");

        // ...and nothing was created on disk by the failed grep.
        await Assert.That(File.Exists(probe)).IsFalse().Because("failed grep must not create the probe path");

        await Assert.That(Server.ReceivedRequests.Count).IsGreaterThanOrEqualTo(2).Because("tool loop must issue the tool request plus the text follow-up");
    }
}
