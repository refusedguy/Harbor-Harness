using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — write-error path through the PTY: the mock answers
///     with a <c>write</c> tool call whose parent directory does not exist and
///     <c>createDirs: false</c>, so the tool fails deterministically with
///     "Parent directory does not exist" (see <c>WriteTool.ExecuteAsync</c> —
///     with the default <c>createDirs: true</c> the parents would be created
///     and no error would occur). The out-of-workspace path raises the
///     approval prompt (workspace confinement downgrades Allow to Ask), so the
///     test drives the prompt's own <c>[y]/[n]/[a]</c> keys — <c>a</c> (always
///     allow) is remembered for the run — then switches the mock to a plain
///     text response so follow-up turns settle instead of re-executing the
///     canned call (the mock re-serves per request, and the home-config
///     <c>maxSteps</c> budget ends the run now — #1118 — so the budget is set
///     to the two turns this scenario needs). The failed
///     call completes the card in the error state (glyph <c>✖</c>, symmetric
///     to the <c>✔</c> success header asserted by #1122), the run goes idle,
///     and nothing appears on disk. The error BODY ("Parent directory does
///     not exist: …") is deliberately not asserted: like the success bodies
///     in #1122, it does not paint on the settled timeline (stale 1-row
///     layout when the card is not last — see #1137). Same prompt-driving
///     pattern as <c>ToolErrorScenarioTests</c> / <c>PermissionDenyScenarioTests</c>
///     (#1163) and the diff trio (#1122). Only tests; the product is untouched.
/// </summary>
public sealed class WriteErrorScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(120_000)]
    public async Task WriteCall_MissingParentDirNoCreate_StreamsError_AndTurnSettles()
    {
        // Target inside a directory that is never created — unique marker.
        string probe = Path.Combine(TempHome, "pty-writeerr-missing-423c", "nested-probe-423c.txt");

        WriteHomeConfig("""
            {
              "provider": "mock",
              "model": "mock/test-model",
              "agent": "code",
              "onboarded": true,
              "maxSteps": 2
            }
            """);
        Server.SetToolCallResponse("test-model", "write", new
        {
            path = probe,
            content = "never-written-423c\n",
            createDirs = false
        });

        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("create the nested probe file");

        // Approve with "a" (remembered for the run), then stop the tool flood
        // at the mock so the run settles after this one execution. Re-prompt
        // tolerant: approve again until the follow-up text lands.
        bool approved = await AnswerApprovalGateAsync(
            "permission required",
            "probe-writeerr-done-423c",
            TimeSpan.FromSeconds(30),
            answerKey: "a",
            onFirstAnswer: () => Server.SetResponse("test-model", "probe-writeerr-done-423c")).ConfigureAwait(false);

        await Assert.That(approved).IsTrue().Because($"approval prompt never appeared:\n{ScreenText}");

        // The follow-up turn landed as text, then the run went idle.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("probe-writeerr-done-423c", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // The failed call completed as an error: the card header repainted to
        // the error glyph (the result body itself does not paint on the
        // settled timeline — same stale-layout note as #1137/#1122 — so the
        // header glyph plus the args reference are the stable surface).
        await Assert.That(Session.RawText.Contains("✖", StringComparison.Ordinal)).IsTrue().Because("tool card must complete in the error state");
        await Assert.That(Session.RawText.Contains("nested-probe-423c", StringComparison.Ordinal)).IsTrue().Because("tool card args must reference the probe file");

        // The approval trail survived on the settled timeline (same marker
        // as the #1122 diff trio), next to the errored write card.
        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("✓ approved (always)", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("write", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");

        // ...and nothing was created on disk: neither the file nor its parent.
        await Assert.That(File.Exists(probe)).IsFalse().Because("failed write must not create the probe file");
        await Assert.That(Directory.Exists(Path.GetDirectoryName(probe)!)).IsFalse().Because("createDirs:false must not create the parent directory");

        await Assert.That(Server.ReceivedRequests.Count).IsGreaterThanOrEqualTo(2).Because("tool loop must issue the tool request plus the text follow-up");
    }
}
