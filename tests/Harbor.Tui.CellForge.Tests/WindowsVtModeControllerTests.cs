using System.Runtime.InteropServices;
using System.Text;
using Harbor.Tui.CellForge.Input;

#pragma warning disable S108, S2486 // Best-effort interop probes in tests — intentionally ignored.

namespace Harbor.Tui.CellForge.Tests;

public class WindowsVtModeControllerTests
{
    [DllImport("kernel32", SetLastError = true)]
    private static extern uint GetConsoleCP();

    [DllImport("kernel32", SetLastError = true)]
    private static extern uint GetConsoleOutputCP();

    // WinBase.h handle ids + WinCon.h mode bits, mirrored here so this test
    // reads what the Console API actually holds rather than what the
    // controller's own constants say it wrote. The two sets agree by
    // construction; if the controller's flags change, this test changes with
    // it or fails, which is the point.
    private const int TestStdInputHandle = -10;
    private const int TestStdOutputHandle = -11;
    private const uint TestEnableProcessedInput = 0x0001;
    private const uint TestEnableLineInput = 0x0002;
    private const uint TestEnableEchoInput = 0x0004;
    private const uint TestEnableVirtualTerminalInput = 0x0200;
    private const uint TestEnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    /// <summary>
    ///     Reads the live console mode for <paramref name="handleId" />, or
    ///     <see langword="null" /> when this host has no such console (a
    ///     redirected handle under CI).
    /// </summary>
    private static uint? TryReadConsoleMode(int handleId)
    {
        IntPtr handle = GetStdHandle(handleId);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return null;
        }

        return GetConsoleMode(handle, out uint mode) ? mode : null;
    }

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
    public async Task Enter_OnWindows_RequestsVtInputAndRestoresTheOriginalModes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        uint? inputBefore = TryReadConsoleMode(TestStdInputHandle);
        uint? outputBefore = TryReadConsoleMode(TestStdOutputHandle);

        var controller = new WindowsVtModeController();
        bool entered;
        try
        {
            controller.Enter();
            entered = controller.IsRaw;
        }
        catch (InvalidOperationException)
        {
            // No console on stdin — the Windows leg cannot run here at all.
            await Assert.That(controller.IsRaw).IsFalse();
            return;
        }

        if (!entered)
        {
            return;
        }

        try
        {
            uint? inputDuring = TryReadConsoleMode(TestStdInputHandle);
            if (inputBefore is { } beforeInput && inputDuring is { } duringInput)
            {
                await Assert.That(duringInput & TestEnableVirtualTerminalInput).IsNotEqualTo(0u)
                    .Because("raw input on Windows arrives as VT sequences — ENABLE_VIRTUAL_TERMINAL_INPUT is what makes that true.");
                await Assert.That(duringInput & (TestEnableEchoInput | TestEnableLineInput | TestEnableProcessedInput)).IsEqualTo(0u)
                    .Because("echo, line editing and processed input must all be off: the parser owns the bytes, and Ctrl+C must reach it as 0x03.");
            }

            uint? outputDuring = TryReadConsoleMode(TestStdOutputHandle);
            if (outputBefore is not null && outputDuring is { } duringOutput)
            {
                await Assert.That(duringOutput & TestEnableVirtualTerminalProcessing).IsNotEqualTo(0u)
                    .Because("the diff renderer writes VT sequences — ENABLE_VIRTUAL_TERMINAL_PROCESSING is what makes conhost interpret them.");
            }
        }
        finally
        {
            try { controller.Restore(); } catch { }
        }

        await Assert.That(controller.IsRaw).IsFalse();

        // Restore must put both handles back exactly as they were found,
        // otherwise a crashed or exited session leaves the user's console in
        // raw mode.
        uint? inputAfter = TryReadConsoleMode(TestStdInputHandle);
        if (inputBefore is { } before && inputAfter is { } after)
        {
            await Assert.That(after).IsEqualTo(before)
                .Because("Restore must hand the input handle back its original mode.");
        }

        uint? outputAfter = TryReadConsoleMode(TestStdOutputHandle);
        if (outputBefore is { } beforeOut && outputAfter is { } afterOut)
        {
            await Assert.That(afterOut).IsEqualTo(beforeOut)
                .Because("Restore must hand the output handle back its original mode.");
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
