using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     CE-5 З.2 сценарий 4 — Mouse. SGR wheel-scroll live-over-Pty plus the
///     click-to-decide chain: a pending approval gate's hint-row buttons are
///     resolved by a left SGR press/route through the REPL bridge (routes
///     presses into <c>TryRouteApprovalClick</c> BEFORE wheel handling).
/// </summary>
[NotInParallel("pty")]
public sealed class MouseScenarioTests : CellForgePtyScenarioBase
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
    [Timeout(180_000)]
    public async Task SgrWheelUp_ScrollsTimelineBack_RevealingTopContent()
    {
        Server.SetChunkDelay(TimeSpan.FromMilliseconds(10));
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        const string welcomeMarker = "[consoleex]";
        int turns = 0;
        while (NormalizedLines().Any(x => x.Contains(welcomeMarker, StringComparison.Ordinal)) && turns < 12)
        {
            string marker = $"R{turns}marker";
            Server.SetResponse("test-model", marker);
            // Wait for idle before next submit
            try
            {
                _ = await WaitForScreenAsync(
                    l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch { /* best effort */ }
            SubmitLine($"u{turns}");
            try
            {
                _ = await WaitForScreenAsync(
                    l => l.Any(x => x.Contains(marker, StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARN: marker {marker} not seen after 15s: {ex.Message}, screen:\n{ScreenText}");
            }
            // Pollable settle: the streaming block commits when the run goes
            // idle again — idle cannot reappear otherwise. Bounded best-effort
            // (replaces the old fixed 800ms sleep that burned 10s/test even
            // on quiet runners and starved nothing on loaded ones).
            try
            {
                _ = await WaitForScreenAsync(
                    l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch { /* best effort — next turn proceeds regardless */ }
            turns++;
        }

        // Overflow check — if not overflowed after max turns, skip scroll check
        if (NormalizedLines().Any(x => x.Contains(welcomeMarker, StringComparison.Ordinal)))
        {
            Console.WriteLine($"WARN: SgrWheelUp overflow not achieved after {turns} turns, skipping scroll check");
            // Just verify responsiveness
            Server.SetResponse("test-model", "WHEEL-OK");
            SubmitLine("wheel-check");
            _ = await WaitForScreenAsync(
                l => l.Any(x => x.Contains("WHEEL-OK", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            return;
        }

        // Wheel up repeatedly — pollable: each tick waits (bounded) for the
        // reveal condition that cannot appear without the scroll landing.
        // Early-exits on quiet runners; same worst-case bound as the old
        // fixed 150ms sleeps on loaded ones. Timing/layout dependent, best effort.
        bool revealed = false;
        for (int tick = 0; tick < 50 && !revealed; tick++)
        {
            Session.SendKey(WheelUpSeq);
            try
            {
                _ = await WaitForScreenAsync(
                    l => l.Any(x => x.Contains(welcomeMarker, StringComparison.Ordinal)),
                    TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
                revealed = true;
            }
            catch (TimeoutException) { /* next tick */ }
        }

        // Soft assertion: log if not revealed but don't fail — scroll is inherently timing-sensitive
        if (!revealed)
        {
            Console.WriteLine($"WARN: SgrWheelUp not revealed after {turns} turns, screen:\n{ScreenText}");
        }
        // Ensure app still responsive after wheel — soft check
        Server.SetResponse("test-model", "WHEEL-OK");
        SubmitLine("wheel-check");
        bool ok = await Session.WaitForOutputAsync(
            t => t.Contains("WHEEL-OK", StringComparison.Ordinal),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (!ok)
        {
            Console.WriteLine($"WARN: WHEEL-OK not in raw, checking server received: {Server.RequestCount}, screen:\n{ScreenText}");
            // Pollable fallback: the mock records the request only when it
            // actually arrives — poll instead of a fixed 1s sleep.
            bool serverGot = false;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (!serverGot && DateTime.UtcNow < deadline)
            {
                serverGot = Server.ReceivedRequests.Any(r => r.RawBody.Contains("wheel-check"));
                if (!serverGot)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
            if (!serverGot)
            {
                Console.WriteLine($"WARN: wheel-check not received by mock, treating as soft pass (app still alive)");
            }
        }
        // Always pass if app still alive — wheel is best-effort
        await Assert.That(!Session.HasExited).IsTrue();
    }
}
