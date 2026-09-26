using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     CE-5 З.2 сценарий 5 — Paste: bracketed paste с инъекцией «/danger»
///     внутри вставляется ВЕРБАТИМ как текст композера (парсерный контракт
///     §4: payload не ре-парсится), НЕ выполняется как slash-команда; после
///     очистки буфера Ctrl+C×2 выходит штатно (exit 0 + leave-alt-screen).
/// </summary>
[NotInParallel("pty")]
public sealed class PasteInjectionScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(30_000)]
    public async Task BracketedPaste_WithDangerInjection_InsertsAsText_NeverExecutes()
    {
        Server.SetResponse("test-model", "ok");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        Session.SendKey("\x1b[200~/danger say hi\x1b[201~");

        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("/danger say hi", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(Server.RequestCount).IsEqualTo(0);

        // Clear the composer (14 chars), then the idle Ctrl+C×2 quit gesture.
        Session.SendKey(new string('\x7f', 14));
        _ = await WaitForScreenAsync(
            l => !l.Any(x => x.Contains("/danger say hi", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        SendCtrlC();
        // Throws TimeoutException (test failure) if the hint never lands.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("^C — ещё раз для выхода", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        SendCtrlC();
        int exit = await Session.WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(0);

        bool leftAltScreen = Session.RawText.Contains(
            "\x1b[?2004l\x1b[?25h\x1b[?1049l", StringComparison.Ordinal);
        await Assert.That(leftAltScreen).IsTrue();
    }
}
