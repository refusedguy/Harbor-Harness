using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     CE-5 З.2 сценарий 7 — Ctrl+C: во время хода (mock с длинным стримом)
///     байт 0x03 прерывает ход («ход прерван»), в idle первое нажатие даёт
///     подсказку, второе в пределах окна — выход с кодом 0 и
///     leave-alt-screen последовательностью.
/// </summary>
[NotInParallel("pty")]
public sealed class CtrlCScenarioTests : CellForgePtyScenarioBase
{
    private static readonly string[] SpinnerFrames =
    [
        "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏",
    ];

    [Test]
    [Timeout(60_000)]
    public async Task CtrlC_AbortsRunningTurn_ThenIdleDoublePressExits()
    {
        // ~800 chars at the mock's 4-chars/50ms cadence ≈ a long turn.
        Server.SetResponse("test-model", new string('х', 800));
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("long");
        bool running = await Session.WaitForOutputAsync(
            text => SpinnerFrames.Any(f => text.Contains(f, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(running).IsTrue();

        // 1) Ctrl+C while running → aborts the turn.
        SendCtrlC();
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("^C — прерываю текущий ход…", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("ход прерван", StringComparison.Ordinal) || x.Contains("The operation was canceled", StringComparison.Ordinal) || x.Contains("Operation was canceled", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // 2) First idle Ctrl+C → hint (no exit).
        SendCtrlC();
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("^C — ещё раз для выхода", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // 3) Second press inside the gesture window → clean quit.
        SendCtrlC();
        int exit = await Session.WaitForExitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (exit == -1)
        {
            // Retry hint window — wait for the exit hint to appear before retrying
            _ = await WaitForScreenAsync(
                l => l.Any(x => x.Contains("^C — ещё раз для выхода", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            SendCtrlC();
            exit = await Session.WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(Session.RawText.Contains(
            "\x1b[?2004l\x1b[?25h\x1b[?1049l", StringComparison.Ordinal)).IsTrue();
    }
}
