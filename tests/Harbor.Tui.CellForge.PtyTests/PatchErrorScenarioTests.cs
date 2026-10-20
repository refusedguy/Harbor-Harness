using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — patch-error path through the PTY: the mock answers
///     with a <c>patch</c> tool call whose hunk context does not match the
///     probe file, so the tool fails deterministically with a context-mismatch
///     error (see <c>PatchTool.TryResolveHunkStart</c> — a hunk whose context
///     lines match nowhere returns <c>Result.Failure</c> and
///     <c>ExecuteAsync</c> surfaces it as <c>ToolResult.Error</c>, leaving the
///     file untouched). The probe file exists with known content, so the
///     failure is specifically the patch-match path, not "File not found".
///     The out-of-workspace path raises the approval prompt (workspace
///     confinement downgrades Allow to Ask), so the test drives the prompt's
///     own <c>[y]/[n]/[a]</c> keys — <c>a</c> (always allow) is remembered for
///     the run — then switches the mock to a plain text response so follow-up
///     turns settle instead of re-executing the canned call (the mock
///     re-serves per request, and the home-config <c>maxSteps</c> budget ends
///     the run now — #1118 — so the budget is set to the two turns this
///     scenario needs). The failed call completes the card in
///     the error state (glyph <c>✖</c>, symmetric to the <c>✔</c> success
///     header asserted by #1122), the run goes idle, and the probe stays
///     byte-identical on disk. The error BODY ("Hunk at line … did not match
///     (context mismatch)") is deliberately not asserted: like the success
///     bodies in #1122, it does not paint on the settled timeline (stale 1-row
///     layout when the card is not last — see #1137). Same prompt-driving
///     pattern as <c>ToolErrorScenarioTests</c> /
///     <c>PermissionDenyScenarioTests</c> (#1163),
///     <c>WriteErrorScenarioTests</c> (#1388) and
///     <c>EditErrorScenarioTests</c> (#1403). Only tests; the product is
///     untouched.
/// </summary>
public sealed class PatchErrorScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(120_000)]
    public async Task PatchCall_ContextMismatch_StreamsError_AndFileUnchanged()
    {
        // Probe file exists with known content; the canned hunk context never
        // occurs in it — unique markers.
        string probe = Path.Combine(TempHome, "pty-patcherr-probe-423e.txt");
        const string probeContent = "alpha-423e\nstable-line-423e\nomega-423e\n";
        await File.WriteAllTextAsync(probe, probeContent).ConfigureAwait(false);

        WriteHomeConfig("""
            {
              "provider": "mock",
              "model": "mock/test-model",
              "agent": "code",
              "onboarded": true,
              "maxSteps": 2
            }
            """);
        Server.SetToolCallResponse("test-model", "patch", new
        {
            path = probe,
            patch = "@@ -1,3 +1,3 @@\n alpha-423e\n-absent-context-423e\n+patched-line-423e\n omega-423e\n"
        });

        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("apply the patch to the probe file");

        // Approve with "a" (remembered for the run), then stop the tool flood
        // at the mock so the run settles after this one execution. Re-prompt
        // tolerant: approve again until the follow-up text lands.
        bool approved = await AnswerApprovalGateAsync(
            "permission required",
            "probe-patcherr-done-423e",
            TimeSpan.FromSeconds(30),
            answerKey: "a",
            onFirstAnswer: () => Server.SetResponse("test-model", "probe-patcherr-done-423e")).ConfigureAwait(false);

        await Assert.That(approved).IsTrue().Because($"approval prompt never appeared:\n{ScreenText}");

        // The follow-up turn landed as text, then the run went idle.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("probe-patcherr-done-423e", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // The failed call completed as an error: the card header repainted to
        // the error glyph (the result body itself does not paint on the
        // settled timeline — same stale-layout note as #1137/#1122 — so the
        // header glyph plus the args reference are the stable surface).
        await Assert.That(Session.RawText.Contains("✖", StringComparison.Ordinal)).IsTrue().Because("tool card must complete in the error state");
        await Assert.That(Session.RawText.Contains("pty-patcherr-probe-423e", StringComparison.Ordinal)).IsTrue().Because("tool card args must reference the probe file");

        // The approval trail survived on the settled timeline (same marker
        // as the #1122 diff trio), next to the errored patch card.
        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("✓ approved (always)", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("patch", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");

        // ...and the failed patch left the probe byte-identical on disk.
        string onDisk = await File.ReadAllTextAsync(probe).ConfigureAwait(false);
        await Assert.That(onDisk).IsEqualTo(probeContent).Because("failed patch must leave the probe file byte-identical");

        await Assert.That(Server.ReceivedRequests.Count).IsGreaterThanOrEqualTo(2).Because("tool loop must issue the tool request plus the text follow-up");
    }
}
