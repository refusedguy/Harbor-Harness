using TUnit.Assertions;

namespace Harbor.Tui.ConsoleEx.PtyTests;

/// <summary>
///     CE-5 З.2 сценарий 8 — Termios sanity: ПОСЛЕ штатного выхода
///     терминал восстановлен — tcgetattr(master) после смерти ребёнка
///     байт-в-байт равен снимку ДО запуска (PtySession.InitialTermios,
///     снятый до spawn). Это ровно класс бага CE-4: 49-байтный struct Termios
///     позволял ядру писать мимо — и это не ловил ни один из 366 тестов.
/// </summary>
[NotInParallel("pty")]
public sealed class TermiosRestoreScenarioTests : ConsoleExPtyScenarioBase
{
    [Test]
    [Timeout(30_000)]
    public async Task AfterGracefulExit_TermiosRestoredToPreLaunchSnapshot()
    {
        await StartAppAsync(100, 30).ConfigureAwait(false);
        byte[] baseline = Session.InitialTermios;

        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        // Graceful exit through the slash command.
        SubmitLine("/exit");
        int exit = await Session.WaitForExitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(0);

        // The pty survives the child while the master stays open — any raw
        // mode left behind by the app is observable right here.
        byte[] after = Session.CaptureTermios();

        // ICANON|ECHO must be back on (they are OFF in raw mode).
        uint lflagAfter = BitConverter.ToUInt32(after, 12);
        const uint ICANON = 0x2;
        const uint ECHO = 0x8;
        await Assert.That((lflagAfter & ICANON) != 0).IsTrue();
        await Assert.That((lflagAfter & ECHO) != 0).IsTrue();

        // Full 60-byte equality against the pre-launch snapshot.
        await Assert.That(after.SequenceEqual(baseline)).IsTrue();
    }
}
