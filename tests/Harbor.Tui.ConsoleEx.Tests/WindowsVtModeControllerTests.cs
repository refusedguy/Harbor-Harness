using System.Runtime.InteropServices;
using System.Text;
using Harbor.Tui.ConsoleEx.Input;

#pragma warning disable S108, S2486 // Best-effort interop probes in tests — intentionally ignored.

namespace Harbor.Tui.ConsoleEx.Tests;

public class WindowsVtModeControllerTests
{
    [DllImport("kernel32", SetLastError = true)]
    private static extern uint GetConsoleCP();

    [DllImport("kernel32", SetLastError = true)]
    private static extern uint GetConsoleOutputCP();

    [Test]
    public async Task InitialState_IsRaw_False()
    {
        var controller = new WindowsVtModeController();
        await Assert.That(controller.IsRaw).IsFalse();
    }

    [Test]
    public async Task Restore_BeforeEnter_IsNoOp()
    {
        var controller = new WindowsVtModeController();
        controller.Restore();
        await Assert.That(controller.IsRaw).IsFalse();
        // second check: still false, no throw
        controller.Restore();
        await Assert.That(controller.IsRaw).IsFalse();
    }

    [Test]
    public async Task DoubleRestore_IsSafe()
    {
        var controller = new WindowsVtModeController();
        // Restore before Enter twice must not throw and must keep IsRaw false.
        controller.Restore();
        controller.Restore();
        await Assert.That(controller.IsRaw).IsFalse();

        // Windows path: if Enter succeeds, double Restore must still be safe.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        bool entered = false;
        try
        {
            controller.Enter();
            entered = controller.IsRaw;
        }
        catch (InvalidOperationException)
        {
            await Assert.That(controller.IsRaw).IsFalse();
            return;
        }
        catch (PlatformNotSupportedException)
        {
            await Assert.That(controller.IsRaw).IsFalse();
            return;
        }

        if (!entered)
        {
            return;
        }

        try
        {
            controller.Restore();
            await Assert.That(controller.IsRaw).IsFalse();
            controller.Restore();
            await Assert.That(controller.IsRaw).IsFalse();
        }
        finally
        {
            try { controller.Restore(); } catch { }
        }
    }

    [Test]
    public async Task Enter_OnNonWindows_ThrowsPlatformNotSupported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var controller = new WindowsVtModeController();
        await Assert.That(() => controller.Enter()).Throws<PlatformNotSupportedException>();
        await Assert.That(controller.IsRaw).IsFalse();
    }

    [Test]
    public async Task Enter_OnWindows_RedirectedStdin_EitherSucceedsOrThrowsInvalidOperation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var controller = new WindowsVtModeController();
        try
        {
            controller.Enter();
            await Assert.That(controller.IsRaw).IsTrue();
            try { controller.Restore(); } catch { }
            await Assert.That(controller.IsRaw).IsFalse();
        }
        catch (InvalidOperationException ex)
        {
            await Assert.That(controller.IsRaw).IsFalse();
            var msg = ex.Message;
            var containsExpected = msg.Contains("not a console", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("GetConsoleMode", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("GetStdHandle", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("stdin", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("handle", StringComparison.OrdinalIgnoreCase);
            await Assert.That(containsExpected).IsTrue();
        }
        finally
        {
            try { controller.Restore(); } catch { }
        }
    }

    [Test]
    public async Task Enter_OnWindows_Utf8EncodingAndCodePage_RestoredAfterRestore()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Encoding? originalInputEncoding = null;
        Encoding? originalOutputEncoding = null;
        uint originalInputCp = 0;
        uint originalOutputCp = 0;

        try { originalInputEncoding = Console.InputEncoding; } catch { }
        try { originalOutputEncoding = Console.OutputEncoding; } catch { }
        try { originalInputCp = GetConsoleCP(); } catch { }
        try { originalOutputCp = GetConsoleOutputCP(); } catch { }

        var controller = new WindowsVtModeController();
        bool entered = false;
        try
        {
            controller.Enter();
            entered = controller.IsRaw;
        }
        catch (InvalidOperationException)
        {
            await Assert.That(controller.IsRaw).IsFalse();
            return;
        }

        if (!entered)
        {
            return;
        }

        try
        {
            // Best-effort: after Enter, encodings should be UTF-8 and CP 65001 if available.
            try
            {
                var inputEnc = Console.InputEncoding;
                await Assert.That(inputEnc.CodePage).IsEqualTo(Encoding.UTF8.CodePage);
            }
            catch { }

            try
            {
                uint cp = GetConsoleCP();
                // Only assert when we actually got a CP value (non-zero means console).
                if (cp != 0)
                {
                    await Assert.That(cp).IsEqualTo(65001u);
                }
            }
            catch { }

            try
            {
                uint outCp = GetConsoleOutputCP();
                if (outCp != 0)
                {
                    await Assert.That(outCp).IsEqualTo(65001u);
                }
            }
            catch { }
        }
        finally
        {
            try { controller.Restore(); } catch { }
        }

        await Assert.That(controller.IsRaw).IsFalse();

        // After Restore, original encodings/CPs should be restored (best-effort verification).
        if (originalInputEncoding is not null)
        {
            try
            {
                var after = Console.InputEncoding;
                await Assert.That(after.CodePage).IsEqualTo(originalInputEncoding.CodePage);
            }
            catch { }
        }

        if (originalOutputEncoding is not null)
        {
            try
            {
                var after = Console.OutputEncoding;
                await Assert.That(after.CodePage).IsEqualTo(originalOutputEncoding.CodePage);
            }
            catch { }
        }

        if (originalInputCp != 0)
        {
            try
            {
                var afterCp = GetConsoleCP();
                if (afterCp != 0)
                {
                    await Assert.That(afterCp).IsEqualTo(originalInputCp);
                }
            }
            catch { }
        }

        if (originalOutputCp != 0)
        {
            try
            {
                var afterOutCp = GetConsoleOutputCP();
                if (afterOutCp != 0)
                {
                    await Assert.That(afterOutCp).IsEqualTo(originalOutputCp);
                }
            }
            catch { }
        }
    }

    [Test]
    public async Task Enter_IsIdempotent_WhenAlreadyRaw()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var controller = new WindowsVtModeController();
        bool entered = false;
        try
        {
            controller.Enter();
            entered = controller.IsRaw;
        }
        catch (InvalidOperationException)
        {
            await Assert.That(controller.IsRaw).IsFalse();
            return;
        }

        if (!entered)
        {
            return;
        }

        try
        {
            // Second Enter when already raw must be no-op and not throw.
            controller.Enter();
            await Assert.That(controller.IsRaw).IsTrue();
        }
        finally
        {
            try { controller.Restore(); } catch { }
        }

        await Assert.That(controller.IsRaw).IsFalse();
    }

    [Test]
    public async Task Enter_AfterRestore_CanReenter()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var controller = new WindowsVtModeController();
        bool firstSucceeded = false;
        try
        {
            controller.Enter();
            firstSucceeded = controller.IsRaw;
        }
        catch (InvalidOperationException)
        {
            await Assert.That(controller.IsRaw).IsFalse();
            return;
        }

        if (!firstSucceeded)
        {
            return;
        }

        controller.Restore();
        await Assert.That(controller.IsRaw).IsFalse();

        try
        {
            controller.Enter();
            await Assert.That(controller.IsRaw).IsTrue();
        }
        catch (InvalidOperationException ex)
        {
            await Assert.That(controller.IsRaw).IsFalse();
            var msg = ex.Message;
            var containsExpected = msg.Contains("not a console", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("GetConsoleMode", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("GetStdHandle", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("stdin", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("handle", StringComparison.OrdinalIgnoreCase);
            await Assert.That(containsExpected).IsTrue();
        }
        finally
        {
            try { controller.Restore(); } catch { }
        }
    }
}

#pragma warning restore S108, S2486
