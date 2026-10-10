using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #1057 — diff rendering through the PTY (patch): the mock answers
///     with a <c>patch</c> tool call on an isolated-<c>$HOME</c> probe file.
///     Paths outside the workspace resolve to an approval prompt (workspace
///     confinement, same downgrade as the <c>read</c> scenario), so the test
///     drives the prompt's own <c>[y]/[n]/[a]</c> keys — <c>a</c> (always
///     allow) persists for the run.
///     <para />
///     The mock re-serves the canned call per request, so follow-up turns keep
///     executing it after turn 1 succeeds (and fail with "context mismatch",
///     scrolling the 30-row grid past the turn-1 card). Marker asserts
///     therefore read <c>Session.RawText</c> — the cumulative PTY byte stream,
///     which keeps every painted frame — instead of the viewport. The collapsed
///     card paints the outcome line plus the first body rows, so the outcome
///     and the patch-preview header are stream-observable; the hunk rows past
///     the collapsed budget are not asserted here — the on-disk patch below is
///     their proof. (The home-config <c>maxSteps</c> budget does not end the
///     run after one turn — see #1118 — so the test tolerates the follow-ups
///     instead of assuming them away.) Streaming cadence is nondeterministic —
///     celldiff §8.
/// </summary>
[NotInParallel("pty")]
public sealed class PatchDiffScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(90_000)]
    public async Task PatchCall_PreviewStreams_AndFileChangesOnDisk()
    {
        // Probe file inside the isolated $HOME; markers are unique per issue.
        string probe = Path.Combine(TempHome, "pty-patch-probe-1057.txt");
        await File.WriteAllTextAsync(probe, "alpha-1057\npatchme-line-1057\nomega-1057\n").ConfigureAwait(false);

        WriteHomeConfig("""
            {
              "provider": "mock",
              "model": "mock/test-model",
              "agent": "code",
              "onboarded": true,
              "maxSteps": 1
            }
            """);
        Server.SetToolCallResponse("test-model", "patch", new
        {
            path = probe,
            patch = "@@ -1,3 +1,3 @@\n alpha-1057\n-patchme-line-1057\n+patched-line-1057\n omega-1057\n"
        });

        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("apply the patch to the probe file");

        // The out-of-workspace path raises the approval prompt; approve with
        // "a" (always allow). Done when the outcome line reaches the cumulative
        // stream, or when the approved run settles at idle — a deny would idle
        // too, and the stream asserts below fail loudly for it.
        bool prompted = false;
        var approvalDeadline = TimeSpan.FromSeconds(40);
        var approvalSw = System.Diagnostics.Stopwatch.StartNew();
        while (approvalSw.Elapsed < approvalDeadline)
        {
            if (Session.RawText.Contains("Patched ", StringComparison.Ordinal))
            {
                break;
            }

            string[] snap = NormalizedLines();
            if (snap.Any(x => x.Contains("permission required", StringComparison.Ordinal)))
            {
                prompted = true;
                Session.SendKey("a");
            }
            else if (prompted && snap.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)))
            {
                break;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        // The patch executed for real: its outcome line and preview header
        // streamed through the PTY (scroll-robust: the cumulative byte stream,
        // not the 30-row viewport the follow-up turns scroll past).
        await Assert.That(await WaitForRawTextAsync("Patched ", TimeSpan.FromSeconds(20)).ConfigureAwait(false)).IsTrue().Because("patch result must stream its outcome line");
        await Assert.That(await WaitForRawTextAsync("Patch preview:", TimeSpan.FromSeconds(20)).ConfigureAwait(false)).IsTrue().Because("patch result must stream its preview header");

        // ...and the tool ran for real, not just rendered: the probe changed on disk.
        string onDisk = await File.ReadAllTextAsync(probe).ConfigureAwait(false);
        await Assert.That(onDisk.Contains("patched-line-1057", StringComparison.Ordinal)).IsTrue().Because("patch must add the new probe content on disk");
        await Assert.That(onDisk.Contains("patchme-line-1057", StringComparison.Ordinal)).IsFalse().Because("patch must remove the old probe content from disk");

        await Assert.That(Server.ReceivedRequests.Count).IsGreaterThanOrEqualTo(1).Because("tool loop must issue at least the budgeted request");
    }
}
