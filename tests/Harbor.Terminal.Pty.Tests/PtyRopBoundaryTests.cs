using CSharpFunctionalExtensions;
using Harbor.Terminal.Pty;

namespace Harbor.Terminal.Pty.Tests;

/// <summary>
///     ROP start boundary from #204: <see cref="PtyProcess.TryStart" /> carries
///     the step + errno diagnostic on the rail instead of throwing, and
///     <see cref="PtyProcess.Start" /> stays a thin throwing compat wrapper.
/// </summary>
public class PtyStartBoundaryTests
{
    [Test]
    public async Task StartSpec_Defaults_MatchDocumentedShape()
    {
        var spec = new PtyStartSpec("/bin/sh");

        await Assert.That(spec.FileName).IsEqualTo("/bin/sh");
        await Assert.That(spec.Cols).IsEqualTo(120);
        await Assert.That(spec.Rows).IsEqualTo(32);
        await Assert.That(spec.SearchPath).IsTrue();
    }

    [Test]
    public async Task TryStart_EmptyFileName_FailsWithoutTouchingPty()
    {
        // Pure validation — deterministic on every platform, no PTY allocated.
        Result<PtyProcess> result = PtyProcess.TryStart(new PtyStartSpec(string.Empty));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error.Contains("empty-file-name", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task TryStart_WhitespaceFileName_FailsWithoutTouchingPty()
    {
        Result<PtyProcess> result = PtyProcess.TryStart(new PtyStartSpec("   "));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error.Contains("empty-file-name", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Start_EmptyFileName_ThrowsIOExceptionWithReason()
    {
        // Compat wrapper pins the exception type (IOException, not
        // PlatformNotSupportedException) for the pre-PTY validation failure.
        Exception? thrown = null;
        try
        {
            _ = PtyProcess.Start(new PtyStartSpec(string.Empty));
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown is IOException).IsTrue();
        await Assert.That(thrown!.Message.Contains("empty-file-name", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task TryStart_GhostBinary_FailsWithSpawnStepAndRc()
    {
        if (!PtyProcess.IsSupported)
        {
            return;
        }

        Result<PtyProcess> result = PtyProcess.TryStart(new PtyStartSpec("harbor-no-such-pty-binary"));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error.Contains("posix_spawnp", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Error.Contains("rc=", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task TryStart_ShellExitZero_SucceedsAndReaps()
    {
        if (!PtyProcess.IsSupported || !File.Exists("/bin/sh"))
        {
            return;
        }

        Result<PtyProcess> result = PtyProcess.TryStart(new PtyStartSpec(
            "/bin/sh",
            Args: ["-c", "exit 0"],
            SearchPath: false));

        await Assert.That(result.IsSuccess).IsTrue();

        await using PtyProcess pty = result.Value;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int exitCode = await pty.WaitForExitAsync(cts.Token);

        await Assert.That(exitCode).IsEqualTo(0);
    }
}
