using TUnit.Assertions;

namespace Harbor.Tui.ConsoleEx.PtyTests;

/// <summary>
///     CE-5 З.2 сценарий 4 — Mouse. SGR wheel-scroll live-over-Pty plus the
///     click-to-decide chain: a pending approval gate's hint-row buttons are
///     resolved by a left SGR press/route through the REPL bridge (routes
///     presses into <c>TryRouteApprovalClick</c> BEFORE wheel handling).
/// </summary>
[NotInParallel("pty")]
public sealed class MouseScenarioTests : ConsoleExPtyScenarioBase
{
    private const string WheelUpSeq = "\x1b[<64;10;5M";  // SGR wheel up @ col10,row5
    private const string ApprovalHintMarker = "[y] approve";
    private const string FinalAnswerMarker = "CLICKAPPROVEDONE";

    [Test]
    [Timeout(60_000)]
    public async Task SgrClick_OnApprovalHintRow_ResolvesPendingGate()
    {
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        string targetPath = Path.Combine(TempHome, "pty-click-approval.txt");
        Server.SetToolCallResponse("test-model", "write", new { path = targetPath, content = "clicked" });
        SubmitLine("approve-by-click");

        // The gate card shows up once the tool execution hits the Ask rule
        // and the consoleex permission asker materializes the card.
        await WaitForScreenAsync(
            l => l.Any(x => x.Contains(ApprovalHintMarker, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        // Flip the canned response BEFORE approving: the second LLM request
        // (fired after the write executes) then returns plain text, so the
        // run terminates deterministically instead of looping on tool calls.
        Server.SetResponse("test-model", FinalAnswerMarker);

        (int row, int col) hint = FindHintPosition();
        Session.SendKey($"\x1b[<0;{hint.col + 1};{hint.row + 1}M");
        Session.SendKey($"\x1b[<0;{hint.col + 1};{hint.row + 1}m");

        // Decision recorded ⇒ write executed ⇒ file lands; next turn answers.
        await WaitForScreenAsync(
            l => l.Any(x => x.Contains(FinalAnswerMarker, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(File.Exists(targetPath)).IsTrue();
    }

    /// <summary>
    ///     Locate the rendered <c>[y]</c> hint button on the emulated grid.
    ///     The parser stores zero-based coordinates while the SGR wire encoding
    ///     is one-based — hence the +1 at the send site.
    /// </summary>
    private (int Row, int Col) FindHintPosition()
    {
        var lines = NormalizedLines();
        for (int i = 0; i < lines.Length; i++)
        {
            int col = lines[i].IndexOf(ApprovalHintMarker, StringComparison.Ordinal);
            if (col >= 0)
            {
                return (i, col);
            }
        }

        throw new InvalidOperationException($"'{ApprovalHintMarker}' not visible. Screen:\n{ScreenText}");
    }

    [Test]
    [Timeout(30_000)]
    public async Task SgrWheelUp_ScrollsTimelineBack_RevealingTopContent()
    {
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        const string welcomeMarker = "[consoleex]";
        int turns = 0;
        while (NormalizedLines().Any(x => x.Contains(welcomeMarker, StringComparison.Ordinal)) && turns < 12)
        {
            string marker = $"R{turns}marker";
            Server.SetResponse("test-model", marker);
            SubmitLine($"u{turns}");
            // Screen-grid wait: streamed timeline text lands as separate
            // cursor-positioned runs in the raw stream (see SubmitScenario).
            _ = await WaitForScreenAsync(
                l => l.Any(x => x.Contains(marker, StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            turns++;
        }

        // Overflow achieved: welcome scrolled out of the viewport.
        await Assert.That(turns).IsLessThanOrEqualTo(12);

        // Wheel up repeatedly until the top content comes back into view.
        bool revealed = false;
        for (int tick = 0; tick < 24 && !revealed; tick++)
        {
            Session.SendKey(WheelUpSeq);
            await Task.Delay(120).ConfigureAwait(false);
            revealed = NormalizedLines().Any(x => x.Contains(welcomeMarker, StringComparison.Ordinal));
        }

        await Assert.That(revealed).IsTrue();
    }
}
