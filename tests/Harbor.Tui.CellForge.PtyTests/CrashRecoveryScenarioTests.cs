using Harbor.E2E.Framework;
using Harbor.E2E.Framework.Pty;
using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Sprint Testing-Strategy З.1 — raw-mode recovery after crash: SIGKILL
///     приложения во время хода НЕ чинит терминал за него — termios остаётся
///     в raw-режиме (ICANON/ECHO выключены), и это наблюдаемо через master-fd
///     пока master открыт. Восстановление — ответственность НОВОЙ сессии:
///     свежий PtySession (cat) на новом PTY работает штатно.
/// </summary>
[NotInParallel("pty")]
public sealed class CrashRecoveryScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(45_000)]
    public async Task SigKillMidRun_LeavesRawMode_NextSessionRecovers()
    {
        // Linux: the termios byte-offset assertions below are asm-generic.
        PtySession.RequireLinux();

        Server.SetResponse("test-model", new string('х', 800));
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("crash-me");
        bool running = await Session.WaitForOutputAsync(
            text => text.Contains("ход", StringComparison.Ordinal) || text.Contains("…", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(running).IsTrue();

        // SIGKILL mid-run — no cleanup handlers run at all.
        Session.Kill();
        int exit = await Session.WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(137); // 128 + SIGKILL

        // The pty master outlives the child: the raw mode the app entered is
        // still set on the slave side — a crashed TUI does NOT restore it.
        byte[] after = Session.CaptureTermios();
        uint lflag = BitConverter.ToUInt32(after, 12);
        const uint ICANON = 0x2;
        const uint ECHO = 0x8;
        await Assert.That((lflag & ICANON) == 0).IsTrue();
        await Assert.That((lflag & ECHO) == 0).IsTrue();

        // Recovery: a brand-new session on a fresh PTY works end-to-end.
        await using var probe = PtySession.Start(new PtyStartSpec("cat", [], Cols: 80, Rows: 24));
        probe.WriteLine("recovered-after-crash");
        bool echoed = await probe.WaitForTextAsync(
            "recovered-after-crash", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(echoed).IsTrue();
    }
}
