using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — the notebook-error path through the PTY: the mock
///     answers with a <c>notebook get</c> tool call whose key was never written.
///     <c>NotebookTool.ExecuteAsync</c> loads the per-session store (a missing
///     file yields an empty dictionary, so there is no write and no directory
///     creation), then <c>GetNoteCommand</c> returns
///     <c>ToolResult.Error("No note with key '…'")</c>. Nothing reaches a real
///     process, an external network or a persisted note — the only I/O is the
///     read-only existence check on the absent notes file, which is exactly the
///     hermetic property the bash-error slice (#2420) established for its own
///     tool seam.
///
///     The tool's <c>Path</c> safety profile still raises the default approval
///     gate, so the test answers it with <c>a</c> (always allow, remembered for
///     the run) through the shared helper. The step budget is set to the single
///     turn this scenario needs, which ends the run right after the failed tool
///     executes (no follow-up completion is requested). The helper's done
///     marker is the card's error glyph, which appears only after that failed
///     execution — not the initial <c>idle</c> status, which is already painted
///     before the submit and would end the wait too early.
///
///     The failed call completes the card in the error state (glyph <c>✖</c>),
///     the run goes idle, and the notes root stays empty. The error BODY is
///     deliberately not asserted: on the settled timeline it does not paint
///     (stale 1-row layout when the card is not last — #1137), so the header
///     glyph plus the echoed key are the stable surface. Only tests; product
///     code and architecture gates are untouched.
/// </summary>
public sealed class NotebookErrorScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(120_000)]
    public async Task NotebookGet_MissingKey_StreamsError_AndTurnSettles()
    {
        // A key that was never written — unique marker, never persisted.
        const string missingKey = "pty-nbget-missing-423l";

        WriteHomeConfig("""
            {
              "provider": "mock",
              "model": "mock/test-model",
              "agent": "code",
              "onboarded": true,
              "maxSteps": 1
            }
            """);
        Server.SetToolCallResponse("test-model", "notebook", new
        {
            action = "get",
            key = missingKey
        });

        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("read the missing notebook key");

        // Answer the approval gate with "a" (remembered for the run). With a
        // one-turn budget the failed tool execution ends the run, so no second
        // completion is requested and the tool call is not re-served. The done
        // marker is the card's error glyph — distinct from the idle status that
        // is already on screen before the submit — so the wait only ends after
        // the failed execution has actually painted. Both states are observed,
        // not sleeps.
        bool approved = await AnswerApprovalGateAsync(
            "permission required",
            "✖",
            TimeSpan.FromSeconds(30),
            answerKey: "a").ConfigureAwait(false);
        await Assert.That(approved).IsTrue().Because($"approval prompt never appeared:\n{ScreenText}");

        // The failed call's args are on the settled timeline before the run
        // ends; the helper already waited out the idle status above.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains(missingKey, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // The card completed in the error state and echoed the requested key.
        string raw = Session.RawText;
        bool errored = raw.Contains('✖');
        bool echoed = raw.Contains(missingKey, StringComparison.Ordinal);
        await Assert.That(errored && echoed).IsTrue().Because($"error glyph={errored}, key echo={echoed}:\n{ScreenText}");

        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("notebook", StringComparison.Ordinal))).IsTrue()
            .Because($"the tool name must survive on the settled timeline:\n{ScreenText}");

        // A failed get writes nothing: the per-session notes file is absent.
        string notesRoot = Path.Combine(TempHome, ".harbor", "notes");
        bool noNotes = !Directory.Exists(notesRoot) || Directory.GetFiles(notesRoot, "*.json").Length == 0;
        await Assert.That(noNotes).IsTrue().Because("a failed notebook get must not persist a notes file");

        // One turn, no follow-up completion — the budget stopped the loop.
        await Assert.That(Server.ReceivedRequests.Count).IsEqualTo(1)
            .Because("maxSteps=1 must end the run after the tool request, with no follow-up call");
    }
}
