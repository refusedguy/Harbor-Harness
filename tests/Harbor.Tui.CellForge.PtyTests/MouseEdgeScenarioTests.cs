using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Sprint Testing-Strategy З.1 — mouse-mode edges: (a) SGR drag-поток и
///     release без предварительного press игнорируются без побочных эффектов
///     (нет падения, нет ложного approval, нет запроса к LLM); (b) после
///     scrollback'а wheel-up возвращает вниз wheel-down — «живой» низ ленты
///     снова на экране.
/// </summary>
[NotInParallel("pty")]
public sealed class MouseEdgeScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(30_000)]
    public async Task DragAndReleaseWithoutPress_IgnoredGracefully()
    {
        Server.SetResponse("test-model", "EDGE-OK");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        // Drag with button held (motion flag) + a bare release — no press
        // ever anchored a selection. Must be swallowed silently.
        Session.SendKey("\x1b[<32;10;5M");  // SGR drag, button 0, col10 row5
        Session.SendKey("\x1b[<32;14;9M");  // drag moved
        Session.SendKey("\x1b[<0;14;9m");   // release without prior press
        await Task.Delay(400).ConfigureAwait(false);

        // No crash, no LLM turn, app still processes normal input.
        await Assert.That(Server.RequestCount).IsEqualTo(0);
        Session.SendKey("still\r");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("EDGE-OK", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    [Test]
    [Timeout(60_000)]
    public async Task WheelDown_AfterWheelUp_ReturnsToLiveBottom()
    {
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        const string welcomeMarker = "[consoleex]";
        int turns = 0;
        // Short responses stream in ~0.2 s per turn; a handful of turns per
        // timeline row quickly overflows the 30-row viewport.
        while (NormalizedLines().Any(x => x.Contains(welcomeMarker, StringComparison.Ordinal)) && turns < 30)
        {
            string marker = $"D{turns}marker";
            Server.SetResponse("test-model", marker);
            Session.SendKey($"u{turns}\r");
            _ = await WaitForScreenAsync(
                l => l.Any(x => x.Contains(marker, StringComparison.Ordinal)),
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            turns++;
        }

        // Overflow actually achieved: the oldest content left the viewport.
        await Assert.That(NormalizedLines()
            .Any(x => x.Contains(welcomeMarker, StringComparison.Ordinal))).IsFalse();

        // Wheel up until the top content re-enters the viewport.
        bool revealed = false;
        for (int tick = 0; tick < 40 && !revealed; tick++)
        {
            Session.SendKey("\x1b[<64;10;5M");
            await Task.Delay(120).ConfigureAwait(false);
            revealed = NormalizedLines().Any(x => x.Contains(welcomeMarker, StringComparison.Ordinal));
        }

        await Assert.That(revealed).IsTrue();

        // Wheel down walks back to the live bottom — the oldest content
        // leaves the viewport again and the newest marker is visible.
        bool back = false;
        for (int tick = 0; tick < 40 && !back; tick++)
        {
            Session.SendKey("\x1b[<65;10;5M");
            await Task.Delay(120).ConfigureAwait(false);
            back = !NormalizedLines().Any(x => x.Contains(welcomeMarker, StringComparison.Ordinal))
                   && NormalizedLines().Any(x => x.Contains($"D{turns - 1}marker", StringComparison.Ordinal));
        }

        await Assert.That(back).IsTrue().Because($"screen:\n{ScreenText}");
    }
}
