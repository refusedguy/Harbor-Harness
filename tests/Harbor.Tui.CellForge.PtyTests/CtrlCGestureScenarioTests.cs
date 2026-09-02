using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Sprint Testing-Strategy З.1 — Ctrl+C семантика, одиночное vs двойное:
///     (a) Ctrl+C при НЕпустом композере очищает буфер (readline-контракт),
///     НЕ считает жестом выхода — следующее нажатие даёт подсказку, а не выход;
///     (b) жест выхода живёт в окне 2000 мс: второе нажатие ПОСЛЕ окна даёт
///     подсказку снова, второе ВНУТРИ окна — штатный выход.
/// </summary>
[NotInParallel("pty")]
public sealed class CtrlCGestureScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(30_000)]
    public async Task CtrlC_WithNonEmptyBuffer_ClearsBuffer_NotQuitGesture()
    {
        Server.SetResponse("test-model", "ok");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        // Buffer has text → Ctrl+C clears it (Edited), never the abort gesture.
        SubmitLineText("draft text");
        await Task.Delay(300).ConfigureAwait(false);
        SendCtrlC();
        await Task.Delay(400).ConfigureAwait(false);

        // No exit hint, buffer cleared (the draft is gone from the screen).
        string[] lines = NormalizedLines();
        await Assert.That(lines.Any(x => x.Contains("draft text", StringComparison.Ordinal))).IsFalse();
        await Assert.That(lines.Any(x => x.Contains("^C — ещё раз для выхода", StringComparison.Ordinal))).IsFalse();

        // Now the buffer is empty → this Ctrl+C is the FIRST quit-gesture press.
        SendCtrlC();
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("^C — ещё раз для выхода", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // …and the second press inside the window exits cleanly.
        SendCtrlC();
        int exit = await Session.WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(0);
    }

    [Test]
    [Timeout(45_000)]
    public async Task QuitGesture_SecondPressAfterWindowExpires_HintsAgain()
    {
        Server.SetResponse("test-model", "ok");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        // First press → hint.
        SendCtrlC();
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("^C — ещё раз для выхода", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Past the 2000 ms gesture window the press no longer quits.
        await Task.Delay(2300).ConfigureAwait(false);
        SendCtrlC();
        await Task.Delay(500).ConfigureAwait(false);

        // Still alive: a FRESH hint, not an exit. (Count plain-substring
        // matches — the rendered line starts with the literal '^' character.)
        string[] after = NormalizedLines();
        int hints = after.Count(x => x.Contains("^C — ещё раз для выхода", StringComparison.Ordinal));
        await Assert.That(hints).IsGreaterThanOrEqualTo(1);
        await Assert.That(Session.HasExited).IsFalse();

        // And the double-press INSIDE the window still quits.
        SendCtrlC();
        int exit = await Session.WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(0);
    }

    /// <summary>Type into the composer WITHOUT submitting (raw bytes, no newline).</summary>
    private void SubmitLineText(string text) => Session.SendKey(text);
}
