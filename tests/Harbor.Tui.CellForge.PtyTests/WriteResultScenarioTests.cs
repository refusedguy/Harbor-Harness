using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #1057 — file-tool result through the PTY (write, the non-diff
///     baseline of the edit/write/patch trio): the mock answers with a
///     <c>write</c> tool call creating a new file under the isolated
///     <c>$HOME</c>. Paths outside the workspace resolve to an approval prompt
///     (workspace confinement downgrades Allow to Ask), so the test drives the
///     prompt's own <c>[y]/[n]/[a]</c> keys — <c>a</c> (always allow) is
///     remembered for the run — then the Created card streams and the file
///     lands on disk. This is the prompt-driving variant from the issue body,
///     the same pattern as <c>ToolCallStreamingScenarioTests</c> (#1056): the
///     persisted-preseed variant cannot work in the interactive path because
///     the CellForgeModule <c>IPermissionService</c> override is constructed
///     without a config store (live defect, see #1125).
///     <para />
///     The mock re-serves the canned call per request, so turns 2+ keep
///     executing it after turn 1 creates the file (reporting "Overwrote" and
///     scrolling the 30-row grid past the turn-1 "Created" card) — the outcome
///     assert therefore reads <c>Session.RawText</c>, the cumulative PTY byte
///     stream, instead of the viewport. (The home-config <c>maxSteps</c> budget
///     does not end the run after one turn — see #1118.) Marker asserts only
///     (streaming cadence is nondeterministic — celldiff §8).
/// </summary>
[NotInParallel("pty")]
public sealed class WriteResultScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(90_000)]
    public async Task WriteCall_CreatedCardStreams_AndFileLandsOnDisk()
    {
        // Target file does NOT exist yet: the first execution must
        // create it under the isolated $HOME; the content marker is unique
        // per issue.
        string probe = Path.Combine(TempHome, "pty-write-probe-1057.txt");

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
        Server.SetToolCallResponse("test-model", "write", new
        {
            path = probe,
            content = "written-line-1057\n"
        });

        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("create the probe file");

        // The out-of-workspace path raises the approval prompt; approve with
        // "a" (always allow, remembered for the run) until the outcome streams.
        // Re-prompt tolerant: if a later iteration prompts again, approve again.
        var approvalDeadline = TimeSpan.FromSeconds(40);
        var approvalSw = System.Diagnostics.Stopwatch.StartNew();
        while (approvalSw.Elapsed < approvalDeadline)
        {
            if (Session.RawText.Contains("Created ", StringComparison.Ordinal))
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

        // The write executed for real: its outcome line streamed through the
        // PTY (scroll-robust: the cumulative byte stream, not the 30-row
        // viewport the "Overwrote" follow-ups scroll past).
        bool streamedOutcome = await WaitForRawTextAsync("Created ", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        bool probeExists = File.Exists(probe);
        string probeState = probeExists ? await File.ReadAllTextAsync(probe).ConfigureAwait(false) : "<missing>";
        await Assert.That(streamedOutcome).IsTrue().Because(
            $"screen:\n{ScreenText}\nrawLen={Session.RawText.Length} requests={Server.ReceivedRequests.Count} probeExists={probeExists} probe=[{probeState}]");

        // ...and the tool ran for real, not just rendered: the file lands on
        // disk with the exact content (the card carries no content lines, so
        // the disk is the content proof here; follow-up turns overwrite with
        // identical content, so this stays stable under the flood).
        string onDisk = await File.ReadAllTextAsync(probe).ConfigureAwait(false);
        await Assert.That(onDisk.Contains("written-line-1057", StringComparison.Ordinal)).IsTrue().Because("write must create the probe file with the given content");

        await Assert.That(Server.ReceivedRequests.Count).IsGreaterThanOrEqualTo(1).Because("tool loop must issue at least the budgeted request");
    }
}
