using Harbor.E2E.Framework;
using Harbor.E2E.Framework.Pty;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Санитарные тесты самого PtySession (CE-5 Зона 1): прежде чем гонять
///     CellForge-сценарии, доказываем что харнесс умеет ровно то, для чего
///     существует — байты в обе стороны, winsize, наблюдаемость termios
///     через master-fd ПОСЛЕ смерти ребёнка, коды выхода.
///     <para />
///     No <c>[NotInParallel]</c> key (#1789): every test here spawns its OWN
///     pseudo-terminal (<c>PtySession.Start</c> → its own
///     <c>posix_openpt</c>) and its own child (<c>cat</c>/<c>sh</c>), and none
///     of them writes process-wide state — so there is no shared resource to
///     serialize against. The key these tests used to carry serialized them
///     against 39 unrelated scenario classes for nothing.
/// </summary>
public class PtyHarnessSanityTests
{
    [Test]
    [Timeout(30_000)]
    public async Task Cat_EchoesLinesThroughPty()
    {
        PtySession.RequireUnix();
        await using var session = PtySession.Start(new PtyStartSpec("cat", [], Cols: 80, Rows: 24));

        session.WriteLine("hello-pty");
        bool echoed = await session.WaitForTextAsync("hello-pty", TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        await Assert.That(echoed).IsTrue();
    }

    [Test]
    [Timeout(30_000)]
    public async Task Resize_IsVisibleToChildStty()
    {
        PtySession.RequireUnix();
        // stty size prints "<rows> <cols>" of its controlling terminal.
        await using var session = PtySession.Start(new PtyStartSpec(
            "sh", ["-c", "sleep 0.5; stty size"], Cols: 80, Rows: 24));
        await session.ResizeAsync(100, 30).ConfigureAwait(false);

        int exit = await session.WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(session.RawText).Contains("30 100");
    }

    [Test]
    [Timeout(30_000)]
    public async Task Termios_ChangeSurvivesChildExit_AndIsObservableViaMaster()
    {
        PtySession.RequireLinux();
        // Baseline BEFORE the child flips raw mode (child sleeps first).
        await using var session = PtySession.Start(new PtyStartSpec(
            "sh", ["-c", "sleep 0.4; stty raw -echo"], Cols: 80, Rows: 24));
        byte[] before = session.CaptureTermios();

        int exit = await session.WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(0);

        byte[] after = session.CaptureTermios();

        // The change persisted on the pty while the master stays open — this
        // is exactly what scenario CE-5 З.8 asserts against the real app.
        await Assert.That(after.SequenceEqual(before)).IsFalse();
        uint lflag = BitConverter.ToUInt32(after, 12);
        const uint ICANON = 0x2;
        const uint ECHO = 0x8;
        await Assert.That((lflag & ICANON) == 0).IsTrue();
        await Assert.That((lflag & ECHO) == 0).IsTrue();
    }

    [Test]
    [Timeout(30_000)]
    public async Task ExitCode_PropagatesFromChild()
    {
        PtySession.RequireUnix();
        await using var session = PtySession.Start(new PtyStartSpec(
            "sh", ["-c", "exit 7"], Cols: 80, Rows: 24));

        int exit = await session.WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        await Assert.That(exit).IsEqualTo(7);
    }
}
