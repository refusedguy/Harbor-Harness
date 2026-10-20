using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — ls-error path through the PTY: the mock answers
///     with an <c>ls</c> tool call whose <c>path</c> does not exist on disk,
///     so the tool fails deterministically with "Directory not found" (see
///     <c>LsTool.ExecuteCore</c> — <c>ToolPaths.Resolve</c> succeeds on the
///     well-formed absolute probe, then <c>!Directory.Exists(path)</c>
///     surfaces <c>ToolResult.Error</c>).
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
///     disk. The error BODY ("Directory not found: …") is deliberately not
///     asserted: like the success bodies in #1122, it does not paint on the
///     settled timeline (stale 1-row layout when the card is not last — see
///     #1137). Same prompt-driving pattern as
///     <c>ToolErrorScenarioTests</c> / <c>PermissionDenyScenarioTests</c>
///     (#1163), <c>WriteErrorScenarioTests</c> (#1388),
///     <c>EditErrorScenarioTests</c> (#1403),
///     <c>PatchErrorScenarioTests</c> (#1411),
///     <c>GlobErrorScenarioTests</c> (#1427) and
///     <c>GrepErrorScenarioTests</c> (#1418). Only tests; the product is
///     untouched.
/// </summary>
[NotInParallel("pty")]
public sealed class LsErrorScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(120_000)]
    public async Task LsCall_MissingPath_StreamsError_AndTurnSettles()
    {
        // Probe directory that does not exist — unique marker, never created.
        string probe = Path.Combine(TempHome, "pty-lserr-probe-423h");

        WriteHomeConfig("""
            {
              "provider": "mock",
              "model": "mock/test-model",
              "agent": "code",
              "onboarded": true,
              "maxSteps": 2
            }
            """);
        Server.SetToolCallResponse("test-model", "ls", new
        {
            path = probe
        });

        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("list the missing probe directory");

        // Approve with "a" (remembered for the run), then stop the tool flood
        // at the mock so the run settles after this one execution. Re-prompt
        // tolerant: approve again until the follow-up text lands.
        bool approved = await AnswerApprovalGateAsync(
            "permission required",
            "probe-lserr-done-423h",
            TimeSpan.FromSeconds(30),
            answerKey: "a",
            onFirstAnswer: () => Server.SetResponse("test-model", "probe-lserr-done-423h")).ConfigureAwait(false);

        await Assert.That(approved).IsTrue().Because($"approval prompt never appeared:\n{ScreenText}");

        // The follow-up turn landed as text, then the run went idle.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("probe-lserr-done-423h", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // The failed call completed as an error: the card header repainted to
        // the error glyph (the result body itself does not paint on the
        // settled timeline — same stale-layout note as #1137/#1122 — so the
        // header glyph plus the args reference are the stable surface).
        await Assert.That(Session.RawText.Contains("✖", StringComparison.Ordinal)).IsTrue().Because("tool card must complete in the error state");
        await Assert.That(Session.RawText.Contains("pty-lserr-probe-423h", StringComparison.Ordinal)).IsTrue().Because("tool card args must reference the probe path");

        // The approval trail survived on the settled timeline (same marker
        // as the #1122 diff trio), next to the errored ls card.
        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("✓ approved (always)", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("ls", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");

        // ...and nothing was created on disk by the failed ls.
        await Assert.That(Directory.Exists(probe)).IsFalse().Because("failed ls must not create the probe directory");
        await Assert.That(File.Exists(probe)).IsFalse().Because("failed ls must not create the probe path");

        await Assert.That(Server.ReceivedRequests.Count).IsGreaterThanOrEqualTo(2).Because("tool loop must issue the tool request plus the text follow-up");
    }
}
