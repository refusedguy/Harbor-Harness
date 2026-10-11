using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — permission-deny path through the PTY: the mock
///     answers with a <c>read</c> tool call on an isolated-<c>$HOME</c> probe
///     file, and the test denies it at the approval prompt with <c>n</c>.
///     The denial ("Permission denied", see
///     <c>ToolDispatcher.CheckPermissionAsync</c>) streams to the timeline as
///     the tool result, the probe stays byte-identical on disk, and the run
///     settles at idle after the mock is switched to a plain text response
///     (the mock re-serves the canned call per request and the home-config
///     <c>maxSteps</c> budget does not end the run — see #1118 — so without
///     the switch the deny loop never settles). The denial streams as an
///     error-state card (glyph <c>✖</c>) next to the <c>✗ denied</c>
///     approval trail (the symmetric counterpart of the <c>✓ approved</c>
///     trail asserted by #1122). The denial BODY ("Permission denied") is
///     deliberately not asserted: like the success bodies in #1122, it does
///     not paint on the settled timeline (stale 1-row layout when the card
///     is not last — see #1137). Same prompt-driving pattern
///     as <c>ToolCallStreamingScenarioTests</c> (#1056) and the diff trio
///     (#1122). Only tests; the product is untouched.
/// </summary>
[NotInParallel("pty")]
public sealed class PermissionDenyScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(120_000)]
    public async Task ToolCall_DeniedAtPrompt_FileUnchanged_AndTurnSettles()
    {
        // Probe file inside the isolated $HOME; content is the deny proof.
        string probe = Path.Combine(TempHome, "pty-deny-probe-423b.txt");
        const string probeContent = "pty-deny-line-alpha-423b\npty-deny-line-beta-423b\n";
        await File.WriteAllTextAsync(probe, probeContent).ConfigureAwait(false);

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

        SubmitLine("read the probe file");

        // Deny with "n" (one-shot — a re-served call re-prompts, so deny
        // again until the text follow-up lands), switching the mock to text
        // on the first denial so the run can settle.
        bool denied = false;
        var denySw = System.Diagnostics.Stopwatch.StartNew();
        while (denySw.Elapsed < TimeSpan.FromSeconds(30))
        {
            string[] snap = NormalizedLines();
            if (snap.Any(x => x.Contains("permission required", StringComparison.Ordinal)))
            {
                Session.SendKey("n");
                if (!denied)
                {
                    denied = true;
                    Server.SetResponse("test-model", "probe-deny-done-423b");
                }
            }

            if (snap.Any(x => x.Contains("probe-deny-done-423b", StringComparison.Ordinal)))
            {
                break;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        await Assert.That(denied).IsTrue().Because($"approval prompt never appeared:\n{ScreenText}");

        // The follow-up turn landed as text, then the run went idle.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("probe-deny-done-423b", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // The denial completed the card as an error (header glyph), next to
        // the deny trail on the settled timeline. The denial body itself is
        // not asserted (stale-layout note as in #1137/#1122).
        await Assert.That(Session.RawText.Contains("✖", StringComparison.Ordinal)).IsTrue().Because("denied tool call must complete its card in the error state");
        await Assert.That(Session.RawText.Contains("pty-deny-probe-423b", StringComparison.Ordinal)).IsTrue().Because("tool card args must reference the probe file");
        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("✗ denied", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("read", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");

        // ...and the denied read never touched the probe on disk.
        string onDisk = await File.ReadAllTextAsync(probe).ConfigureAwait(false);
        await Assert.That(onDisk).IsEqualTo(probeContent).Because("denied read must leave the probe file byte-identical");

        await Assert.That(Server.ReceivedRequests.Count).IsGreaterThanOrEqualTo(2).Because("tool loop must issue the tool request plus the text follow-up");
    }
}
