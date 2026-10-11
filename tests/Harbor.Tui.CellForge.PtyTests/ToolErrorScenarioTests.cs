using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — tool-error path through the PTY: the mock answers
///     with a <c>read</c> tool call on a probe path that does not exist on
///     disk. The out-of-workspace path raises the approval prompt (workspace
///     confinement downgrades Allow to Ask), so the test drives the prompt's
///     own <c>[y]/[n]/[a]</c> keys — <c>a</c> (always allow) is remembered for
///     the run — the same pattern as <c>ToolCallStreamingScenarioTests</c>
///     (#1056) and the diff trio (#1122). Right after approving, the test
///     switches the mock to a plain text response so follow-up turns settle
///     instead of re-executing the canned call (the mock re-serves per
///     request and the home-config <c>maxSteps</c> budget does not end the
///     run — see #1118). The approved call executes and fails: its error
///     ("File not found") streams to the timeline, the run goes idle, and
///     nothing appears on disk. Only tests; the product is untouched.
/// </summary>
[NotInParallel("pty")]
public sealed class ToolErrorScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(120_000)]
    public async Task ToolCall_ReadMissingFile_StreamsError_AndTurnSettles()
    {
        // Probe path that does not exist — unique marker, never created.
        string probe = Path.Combine(TempHome, "pty-toolerr-probe-423b.txt");

        WriteHomeConfig("""
            {
              "provider": "mock",
              "model": "mock/test-model",
              "agent": "code",
              "onboarded": true,
              "maxSteps": 1
            }
            """);
        Server.SetToolCallResponse("test-model", "read", new { path = probe });

        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("read the missing probe file");

        // Approve with "a" (remembered for the run), then stop the tool flood
        // at the mock so the run settles after this one execution. Re-prompt
        // tolerant: approve again until the follow-up text lands.
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
                    Server.SetResponse("test-model", "probe-toolerr-done-423b");
                }
            }

            if (snap.Any(x => x.Contains("probe-toolerr-done-423b", StringComparison.Ordinal)))
            {
                break;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        await Assert.That(approved).IsTrue().Because($"approval prompt never appeared:\n{ScreenText}");

        // The follow-up turn landed as text, then the run went idle.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("probe-toolerr-done-423b", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // The failed call executed for real: the read error streamed through
        // the PTY wire (cumulative raw stream keeps it after scroll-off).
        await Assert.That(Session.RawText.Contains("File not found", StringComparison.Ordinal)).IsTrue().Because("tool error card must stream the read failure");
        await Assert.That(Session.RawText.Contains("pty-toolerr-probe-423b", StringComparison.Ordinal)).IsTrue().Because("tool card args must reference the probe file");

        // ...and nothing was created on disk by the failed read.
        await Assert.That(File.Exists(probe)).IsFalse().Because("failed read must not create the probe file");

        await Assert.That(Server.ReceivedRequests.Count).IsGreaterThanOrEqualTo(2).Because("tool loop must issue the tool request plus the text follow-up");
    }
}
