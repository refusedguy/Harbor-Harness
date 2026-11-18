using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — bash-error path through the PTY: the mock answers
///     with a <c>bash</c> tool call whose <c>cwd</c> points at a directory that
///     does not exist on disk, so the tool fails deterministically with
///     "Working directory does not exist" before any process is spawned (see
///     <c>BashTool.ExecuteAsync</c> — the <c>cwd</c> existence check precedes
///     <c>IProcessProxy.Start</c>, so there is no shell, no output and no
///     side effect). Unlike the path-profile tools in the rest of this error
///     series, <c>bash</c> carries the <c>Command</c> safety profile and its
///     default rule is <c>Ask</c>, so the approval gate appears on the command
///     rather than through workspace confinement; the same <c>[y]/[n]/[a]</c>
///     keys drive it — <c>a</c> (always allow) is remembered for the run.
///     Right after approving, the test switches the mock to a plain text
///     response so follow-up turns settle instead of re-executing the canned
///     call (the mock re-serves per request, and the home-config
///     <c>maxSteps</c> budget ends the run now — #1118 — so the budget is set
///     to the two turns this scenario needs). The failed call completes the
///     card in the error state (glyph <c>✖</c>, symmetric to the <c>✔</c>
///     success header asserted by #1122), the run goes idle, and nothing
///     appears on disk. The error BODY ("Working directory does not exist: …")
///     is deliberately not asserted: like the success bodies in #1122, it
///     does not paint on the settled timeline (stale 1-row layout when the
///     card is not last — see #1137). Same prompt-driving pattern as
///     <c>ToolErrorScenarioTests</c> / <c>PermissionDenyScenarioTests</c>
///     (#1163), <c>WriteErrorScenarioTests</c> (#1388),
///     <c>GlobErrorScenarioTests</c> (#1427) and <c>ReadErrorScenarioTests</c>
///     (#1452). Only tests; the product is untouched.
/// </summary>
public sealed class BashErrorScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(120_000)]
    public async Task BashCall_MissingCwd_StreamsError_AndTurnSettles()
    {
        // Working directory that never exists — unique marker, never created.
        string probe = Path.Combine(TempHome, "pty-basherr-missing-423k");

        WriteHomeConfig("""
            {
              "provider": "mock",
              "model": "mock/test-model",
              "agent": "code",
              "onboarded": true,
              "maxSteps": 2
            }
            """);
        // The command text deliberately does NOT contain the follow-up done
        // marker — the pending card paints its args, and AnswerApprovalGateAsync
        // watches the whole screen for that marker. Reusing it would make the
        // gate look answered before any follow-up text landed.
        Server.SetToolCallResponse("test-model", "bash", new
        {
            command = "echo pty-basherr-cmd-423k",
            cwd = probe
        });

        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("run the probe command in the missing directory");

        // Approve with "a" (remembered for the run), then stop the tool flood
        // at the mock so the run settles after this one execution. Re-prompt
        // tolerant: approve again until the follow-up text lands.
        bool approved = await AnswerApprovalGateAsync(
            "permission required",
            "probe-basherr-done-423k",
            TimeSpan.FromSeconds(30),
            answerKey: "a",
            onFirstAnswer: () => Server.SetResponse("test-model", "probe-basherr-done-423k")).ConfigureAwait(false);

        await Assert.That(approved).IsTrue().Because($"approval prompt never appeared:\n{ScreenText}");

        // Settle signals in the opposite order from the path-tool scenarios:
        // the status line first (the tool loop has stopped), then the
        // follow-up text on the timeline. Local predicates keep the wait
        // conditions readable and this scenario's token stream its own.
        static bool IsIdle(string[] lines) => lines.Any(x => x.Contains("idle", StringComparison.Ordinal));
        static bool HasFollowUp(string[] lines) => lines.Any(x => x.Contains("probe-basherr-done-423k", StringComparison.Ordinal));
        _ = await WaitForScreenAsync(IsIdle, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        string[] followed = await WaitForScreenAsync(HasFollowUp, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(followed.Length).IsGreaterThan(0).Because($"follow-up turn never landed:\n{ScreenText}");

        // Only the header's error glyph and the echoed command are asserted.
        // The result body does not repaint behind an approval gate (#1137),
        // and the args summary is capped at 48 chars (ToolCardTracker.SummaryCap),
        // so the missing cwd is truncated out of the rendered card — the
        // command text is the arg fragment that actually survives on screen.
        string raw = Session.RawText;
        bool errored = raw.Contains('✖');
        bool echoed = raw.Contains("pty-basherr-cmd-423k", StringComparison.Ordinal);
        await Assert.That(errored && echoed).IsTrue().Because($"error glyph={errored}, command echo={echoed}:\n{ScreenText}");

        // The approval trail survived on the settled timeline (same marker
        // as the rest of the error series), next to the errored bash card.
        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("✓ approved (always)", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("bash", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");

        // ...and nothing was created on disk by the failed bash call.
        await Assert.That(Directory.Exists(probe)).IsFalse().Because("failed bash must not create the missing cwd");

        await Assert.That(Server.ReceivedRequests.Count).IsGreaterThanOrEqualTo(2).Because("tool loop must issue the tool request plus the text follow-up");
    }
}
