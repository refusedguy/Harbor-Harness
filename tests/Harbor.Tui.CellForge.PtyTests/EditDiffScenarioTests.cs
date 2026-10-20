using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #1057 — diff rendering through the PTY (edit): the mock answers
///     with an <c>edit</c> tool call on an isolated-<c>$HOME</c> probe file.
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
///     call (the mock re-serves per request, and the home-config
///     <c>maxSteps</c> budget ends the run now — #1118 — so the budget is
///     set to the two turns this scenario needs). The
///     approved call runs exactly once: its success header (tool + ok pill)
///     stays on the settled timeline next to the approval trail, and the
///     probe changes on disk.
/// </summary>
public sealed class EditDiffScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(120_000)]
    public async Task EditCall_ContextDiffStreams_AndFileChangesOnDisk()
    {
        // Probe file inside the isolated $HOME; markers are unique per issue.
        string probe = Path.Combine(TempHome, "pty-edit-probe-1057.txt");
        await File.WriteAllTextAsync(probe, "header-line-1057\noldmarker-line-1057\nfooter-line-1057\n").ConfigureAwait(false);

        // Two turns, and no more: the tool call and the follow-up text. The
        // home-config budget ends the run now (#1118) — it used not to.
        WriteHomeConfig("""
            {
              "provider": "mock",
              "model": "mock/test-model",
              "agent": "code",
              "onboarded": true,
              "maxSteps": 2
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
        // "a" (always allow, remembered for the run), then stop the tool flood
        // at the mock so the run settles after this one execution. Re-prompt
        // tolerant: a lost key leaves the turn waiting at the prompt (turns are
        // sequential), so approve again until the follow-up text lands.
        bool approved = await AnswerApprovalGateAsync(
            "permission required",
            "probe-edit-done-1057",
            TimeSpan.FromSeconds(30),
            answerKey: "a",
            onFirstAnswer: () => Server.SetResponse("test-model", "probe-edit-done-1057")).ConfigureAwait(false);

        await Assert.That(approved).IsTrue().Because($"approval prompt never appeared:\n{ScreenText}");

        // The approved call executed and the follow-up turn landed as text,
        // then the run went idle — only now is the timeline fully settled.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("probe-edit-done-1057", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // The edit card settled on the timeline: success header with the ok
        // pill, the approval trail, and — since #1137 — the body rows: the
        // completed card re-measures its own slot even when an approval gate
        // follows it, so "Edited …" / "Diff (context):" paint.
        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("✔ edit", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("[ok]", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("✓ approved (always)", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("pty-edit-probe-1057", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("Edited ", StringComparison.Ordinal))).IsTrue().Because($"success body must paint after Complete (#1137):\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("Diff (context):", StringComparison.Ordinal))).IsTrue().Because($"diff body must paint after Complete (#1137):\n{ScreenText}");

        // ...and the tool ran for real, not just rendered: the probe changed on disk.
        string onDisk = await File.ReadAllTextAsync(probe).ConfigureAwait(false);
        await Assert.That(onDisk.Contains("newmarker-line-1057", StringComparison.Ordinal)).IsTrue().Because("edit must replace the probe content on disk");
        await Assert.That(onDisk.Contains("oldmarker-line-1057", StringComparison.Ordinal)).IsFalse().Because("edit must remove the old probe content from disk");

        await Assert.That(Server.ReceivedRequests.Count).IsGreaterThanOrEqualTo(2).Because("tool loop must issue the tool request plus the text follow-up");
    }
}
