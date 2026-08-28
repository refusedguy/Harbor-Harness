using System.Runtime.InteropServices;

#pragma warning disable S108, S2486 // Best-effort console interop — empty catch intentionally ignored (no console / redirected handle).

namespace Harbor.Tui.ConsoleEx.Input;

/// <summary>
/// Windows VT-input controller — Win32 bring-up (design §5.2 / §6).
/// Mirrors <see cref="UnixTermiosModeController"/> for Windows:
/// disables <c>LINE_INPUT | ECHO_INPUT | PROCESSED_INPUT</c> and enables
/// <c>VIRTUAL_TERMINAL_INPUT</c> on the input handle, enables
/// <c>VIRTUAL_TERMINAL_PROCESSING</c> on the output handle.
/// Crash-safe and idempotent: <see cref="Restore"/> after a failed
/// <see cref="Enter"/> is a no-op.
/// </summary>
public sealed class WindowsVtModeController : ITerminalModeController
{
    // stdin / stdout handle ids (WinBase.h)
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;

    // input mode flags (WinCon.h)
    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableVirtualTerminalInput = 0x0200;

    // output mode flags
    private const uint EnableProcessedOutput = 0x0001;
    private const uint EnableWrapAtEolOutput = 0x0002;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    public bool IsRaw { get; private set; }

    private uint _originalInputMode;
    private uint _originalOutputMode;
    private bool _hasOriginal;
    private bool _outputCaptured;
    private IntPtr _stdinHandle;
    private IntPtr _stdoutHandle;
    private uint _originalInputCp;
    private uint _originalOutputCp;
    private bool _hasOriginalCp;
    private System.Text.Encoding? _originalInputEncoding;
    private System.Text.Encoding? _originalOutputEncoding;

    public void Enter()
    {
        if (IsRaw)
        {
            return;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("WindowsVtModeController is only meaningful on Windows.");
        }

        _stdinHandle = GetStdHandle(StdInputHandle);
        _stdoutHandle = GetStdHandle(StdOutputHandle);

        if (_stdinHandle == new IntPtr(-1) || _stdinHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetStdHandle(STD_INPUT_HANDLE) returned invalid handle — stdin is not a console.");
        }

        if (!GetConsoleMode(_stdinHandle, out uint inputMode))
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"GetConsoleMode(stdin) failed (Win32 {err}) — stdin is not a console or handle is redirected.");
        }

        uint outputMode = 0;
        bool hasOutput = false;
        if (_stdoutHandle != new IntPtr(-1) && _stdoutHandle != IntPtr.Zero && GetConsoleMode(_stdoutHandle, out outputMode))
        {
            hasOutput = true;
        }

        if (!_hasOriginal)
        {
            _originalInputMode = inputMode;
            _originalOutputMode = outputMode;
            _outputCaptured = hasOutput;
            _hasOriginal = true;
        }

        // Preserve code pages / .NET encodings — both matter for Cyrillic / UTF-8.
        // GetConsoleCP/OutputCP reflect the Win32 codepage; Console.Input/OutputEncoding
        // reflect the BCL's decoder buffer. Cyrillic typed as UTF-8 (D0 BF for п)
        // must survive end-to-end, so we force CP 65001 + UTF8 on entry and restore on exit.
        if (!_hasOriginalCp)
        {
            try { _originalInputCp = GetConsoleCP(); } catch { _originalInputCp = 0; }
            try { _originalOutputCp = GetConsoleOutputCP(); } catch { _originalOutputCp = 0; }
            try { _originalInputEncoding = Console.InputEncoding; } catch { }
            try { _originalOutputEncoding = Console.OutputEncoding; } catch { }
            _hasOriginalCp = true;
        }

        uint newInputMode = inputMode & ~(EnableEchoInput | EnableLineInput | EnableProcessedInput);
        newInputMode |= EnableVirtualTerminalInput;

        if (!SetConsoleMode(_stdinHandle, newInputMode))
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"SetConsoleMode(stdin, raw) failed (Win32 {err}).");
        }

        if (hasOutput)
        {
            uint newOutputMode = outputMode | EnableVirtualTerminalProcessing | EnableWrapAtEolOutput | EnableProcessedOutput;
            // Best-effort for output — failure is non-fatal but we still try to keep VT processing on.
            if (!SetConsoleMode(_stdoutHandle, newOutputMode))
            {
                // Roll back input mode before surfacing failure.
                _ = SetConsoleMode(_stdinHandle, _originalInputMode);
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"SetConsoleMode(stdout, VT) failed (Win32 {err}).");
            }
        }

        // Force UTF-8 codepages — best-effort, never fails the Enter.
        TrySetUtf8CodePagesAndEncodings();

        IsRaw = true;
    }

    public void Restore()
    {
        if (!IsRaw || !_hasOriginal)
        {
            return;
        }

        // Restore input unconditionally; output only if we captured a valid mode.
        if (_stdinHandle != IntPtr.Zero && _stdinHandle != new IntPtr(-1))
        {
            _ = SetConsoleMode(_stdinHandle, _originalInputMode);
        }

        if (_outputCaptured && _stdoutHandle != IntPtr.Zero && _stdoutHandle != new IntPtr(-1))
        {
            _ = SetConsoleMode(_stdoutHandle, _originalOutputMode);
        }

        // Restore codepages / encodings — best-effort, never throw from Restore.
        RestoreCodePagesAndEncodings();

        IsRaw = false;
    }

    private void TrySetUtf8CodePagesAndEncodings()
    {
        const uint CpUtf8 = 65001;
        try { _ = SetConsoleCP(CpUtf8); } catch { }
        try { _ = SetConsoleOutputCP(CpUtf8); } catch { }
        try { Console.InputEncoding = System.Text.Encoding.UTF8; } catch { }
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
    }

    private void RestoreCodePagesAndEncodings()
    {
        if (_hasOriginalCp)
        {
            if (_originalInputCp != 0)
            {
                try { _ = SetConsoleCP(_originalInputCp); } catch { }
            }
            if (_originalOutputCp != 0)
            {
                try { _ = SetConsoleOutputCP(_originalOutputCp); } catch { }
            }
        }

        if (_originalInputEncoding is not null)
        {
            try { Console.InputEncoding = _originalInputEncoding; } catch { }
        }

        if (_originalOutputEncoding is not null)
        {
            try { Console.OutputEncoding = _originalOutputEncoding; } catch { }
        }
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32", SetLastError = true)]
    private static extern uint GetConsoleCP();

    [DllImport("kernel32", SetLastError = true)]
    private static extern uint GetConsoleOutputCP();

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetConsoleCP(uint wCodePageID);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);

#pragma warning restore S108, S2486
}
