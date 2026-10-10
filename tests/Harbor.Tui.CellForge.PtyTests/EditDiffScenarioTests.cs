using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #1057 — diff rendering through the PTY (edit): the mock answers
///     with an <c>edit</c> tool call on an isolated-<c>$HOME</c> probe file.
///     Paths outside the workspace resolve to an approval prompt (workspace
///     confinement downgrades Allow to Ask), so the test drives the prompt's
///     own <c>[y]/[n]/[a]</c> keys — <c>a</c> (always allow) is remembered for
///     the run — then the outcome line and the context-diff header stream into
///     the PTY and the probe changes on disk. This is the prompt-driving
///     variant from the issue body, the same pattern as
///     <c>ToolCallStreamingScenarioTests</c> (#1056): the persisted-preseed
///     variant cannot work in the interactive path because the CellForgeModule
///     <c>IPermissionService</c> override is constructed without a config store
///     (live defect, see #1125).
///     <para />
///     The mock re-serves the canned call per request, so turns 2+ keep
///     executing it after turn 1 succeeds (and fail with "not found",
///     scrolling the 30-row grid past the turn-1 card) — marker asserts
///     therefore read <c>Session.RawText</c>, the cumulative PTY byte stream,
///     instead of the viewport. The collapsed card paints the outcome line plus
///     the first body rows, so the outcome and the context-diff header are
///     stream-observable; the -/+ rows past the collapsed budget are not
///     asserted here — the on-disk replacement below is their proof. (The
///     home-config <c>maxSteps</c> budget does not end the run after one
///     turn — see #1118.) Streaming cadence is nondeterministic — celldiff §8.
/// </summary>
[NotInParallel("pty")]
public sealed class EditDiffScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(90_000)]
    public async Task EditCall_ContextDiffStreams_AndFileChangesOnDisk()
    {
        // Probe file inside the isolated $HOME; markers are unique per issue.
        string probe = Path.Combine(TempHome, "pty-edit-probe-1057.txt");
        await File.WriteAllTextAsync(probe, "header-line-1057\noldmarker-line-1057\nfooter-line-1057\n").ConfigureAwait(false);

        // Cap the tool-call loop: the mock re-serves the canned call per request.
        WriteHomeConfig("""
            {
              "provider": "mock",
              "model": "mock/test-model",
              "agent": "code",
              "onboarded": true,
              "maxSteps": 1
            }
            """);
        Server.SetToolCallResponse("test-model", "edit", new
        {
            path = probe,
            oldString = "oldmarker-line-1057",
            newString = "newmarker-line-1057"
        });

        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("apply the edit to the probe file");

        // The out-of-workspace path raises the approval prompt; approve with
        // "a" (always allow, remembered for the run) until the outcome streams.
        // Re-prompt tolerant: if a later iteration prompts again, approve again.
        var approvalDeadline = TimeSpan.FromSeconds(40);
        var approvalSw = System.Diagnostics.Stopwatch.StartNew();
        while (approvalSw.Elapsed < approvalDeadline)
        {
            if (Session.RawText.Contains("Edited ", StringComparison.Ordinal))
            {
                break;
            }

            string[] snap = NormalizedLines();
            if (snap.Any(x => x.Contains("permission required", StringComparison.Ordinal)))
            {
                Session.SendKey("a");
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        // The edit executed for real: its outcome line and context-diff header
        // streamed through the PTY (scroll-robust: the cumulative byte stream,
        // not the 30-row viewport the follow-up turns scroll past).
        bool streamedOutcome = await WaitForRawTextAsync("Edited ", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        bool probeExists = File.Exists(probe);
        string probeState = probeExists ? await File.ReadAllTextAsync(probe).ConfigureAwait(false) : "<missing>";
        await Assert.That(streamedOutcome).IsTrue().Because(
            $"screen:\n{ScreenText}\nrawLen={Session.RawText.Length} requests={Server.ReceivedRequests.Count} probeExists={probeExists} probe=[{probeState}]");
        await Assert.That(await WaitForRawTextAsync("Diff (context):", TimeSpan.FromSeconds(20)).ConfigureAwait(false)).IsTrue().Because("edit result must stream its context-diff header");

        // ...and the tool ran for real, not just rendered: the probe changed on disk.
        string onDisk = await File.ReadAllTextAsync(probe).ConfigureAwait(false);
        await Assert.That(onDisk.Contains("newmarker-line-1057", StringComparison.Ordinal)).IsTrue().Because("edit must replace the probe content on disk");
        await Assert.That(onDisk.Contains("oldmarker-line-1057", StringComparison.Ordinal)).IsFalse().Because("edit must remove the old probe content from disk");

        await Assert.That(Server.ReceivedRequests.Count).IsGreaterThanOrEqualTo(1).Because("tool loop must issue at least the budgeted request");
    }
}
