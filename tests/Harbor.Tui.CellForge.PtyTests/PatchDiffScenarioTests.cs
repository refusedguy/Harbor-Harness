using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #1057 — diff rendering through the PTY (patch): the mock answers
///     with a <c>patch</c> tool call on an isolated-<c>$HOME</c> probe file.
///     Paths outside the workspace resolve to an approval prompt (workspace
///     confinement downgrades Allow to Ask), so the test drives the prompt's
///     own <c>[y]/[n]/[a]</c> keys — <c>a</c> (always allow) is remembered for
///     the run — the same pattern as <c>ToolCallStreamingScenarioTests</c>
///     (#1056). The persisted-preseed variant cannot work in the interactive
///     path because the CellForgeModule <c>IPermissionService</c> override is
///     constructed without a config store (live defect, see #1125).
///     <para />
///     Right after approving, the test switches the mock to a plain text
///     response so follow-up turns settle instead of re-executing the canned
///     call (the mock re-serves per request and the home-config
///     <c>maxSteps</c> budget does not end the run — see #1118; without the
///     switch the flood scrolls the turn-1 card off the 30-row grid). The
///     approved call runs exactly once: its success header (tool + ok pill)
///     stays on the settled timeline next to the approval trail, and the
///     probe is patched on disk.
/// </summary>
[NotInParallel("pty")]
public sealed class PatchDiffScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(120_000)]
    public async Task PatchCall_PreviewStreams_AndFileChangesOnDisk()
    {
        // Probe file inside the isolated $HOME; markers are unique per issue.
        string probe = Path.Combine(TempHome, "pty-patch-probe-1057.txt");
        await File.WriteAllTextAsync(probe, "alpha-1057\npatchme-line-1057\nomega-1057\n").ConfigureAwait(false);

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
        // "a" (always allow, remembered for the run), then stop the tool flood
        // at the mock so the run settles after this one execution. Re-prompt
        // tolerant: a lost key leaves the turn waiting at the prompt (turns are
        // sequential), so approve again until the follow-up text lands.
        bool approved = false;
        var approvalSw = System.Diagnostics.Stopwatch.StartNew();
        while (approvalSw.Elapsed < TimeSpan.FromSeconds(30))
        {
            string[] snap = NormalizedLines();
            if (snap.Any(x => x.Contains("permission required", StringComparison.Ordinal)))
            {
                Session.SendKey("a");
                if (!approved)
                {
                    approved = true;
                    Server.SetResponse("test-model", "probe-patch-done-1057");
                }
            }

            if (snap.Any(x => x.Contains("probe-patch-done-1057", StringComparison.Ordinal)))
            {
                break;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        await Assert.That(approved).IsTrue().Because($"approval prompt never appeared:\n{ScreenText}");

        // The approved call executed and the follow-up turn landed as text,
        // then the run went idle — only now is the timeline fully settled.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("probe-patch-done-1057", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // The patch card settled on the timeline: success header with the ok
        // pill, the approval trail, and — since #1137 — the body rows: the
        // completed card re-measures its own slot even when an approval gate
        // follows it, so "Patched …" / "Patch preview:" paint.
        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("✔ patch", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("[ok]", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("✓ approved (always)", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("pty-patch-probe-1057", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("Patched ", StringComparison.Ordinal))).IsTrue().Because($"success body must paint after Complete (#1137):\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("Patch preview:", StringComparison.Ordinal))).IsTrue().Because($"patch preview must paint after Complete (#1137):\n{ScreenText}");

        // ...and the tool ran for real, not just rendered: the probe changed on disk.
        string onDisk = await File.ReadAllTextAsync(probe).ConfigureAwait(false);
        await Assert.That(onDisk.Contains("patched-line-1057", StringComparison.Ordinal)).IsTrue().Because("patch must add the new probe content on disk");
        await Assert.That(onDisk.Contains("patchme-line-1057", StringComparison.Ordinal)).IsFalse().Because("patch must remove the old probe content from disk");

        await Assert.That(Server.ReceivedRequests.Count).IsGreaterThanOrEqualTo(2).Because("tool loop must issue the tool request plus the text follow-up");
    }
}
