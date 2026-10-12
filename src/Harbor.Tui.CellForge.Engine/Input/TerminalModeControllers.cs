using System.Runtime.InteropServices;

namespace Harbor.Tui.CellForge.Input;

/// <summary>
///     Platform selection for the raw-mode controller (issue #1249, bring-up
///     slice 2). Windows is no longer a deferred platform: the Win32
///     Console-API controller is the platform controller there, exactly as
///     termios is everywhere else.
/// </summary>
/// <remarks>
///     <para>
///         The selection used to live inline in <c>ReplRunner</c> as a
///         <c>RuntimeInformation</c> ternary, which made the Windows leg
///         unverifiable off Windows — the one decision slice 1 changed (which
///         controller a platform receives) had no test able to run on a Linux
///         CI runner. Splitting the pure decision
///         (<see cref="Create" />) from the ambient probe
///         (<see cref="CreateForCurrentPlatform" />) lets every OS assert the
///         controller it is handed, and keeps the platform bit a value instead
///         of an environment read.
///     </para>
///     <para>
///         Neither controller is guarded here. <see cref="WindowsVtModeController.Enter" />
///         throws <see cref="PlatformNotSupportedException" /> off Windows and
///         <see cref="UnixTermiosModeController.Enter" /> reports its errno, so
///         a controller handed the wrong host fails loudly at <c>Enter</c> —
///         which is precisely the failure the REPL probe turns into a
///         reported, logged fallback rather than a crash.
///     </para>
/// </remarks>
public static class TerminalModeControllers
{
    /// <summary>
    ///     The raw-mode controller for the named platform: the Win32
    ///     Console-API controller on Windows (<c>SetConsoleMode</c> with
    ///     <c>ENABLE_VIRTUAL_TERMINAL_INPUT</c> on the input handle,
    ///     <c>ENABLE_VIRTUAL_TERMINAL_PROCESSING</c> on the output handle),
    ///     the termios controller everywhere else.
    /// </summary>
    public static ITerminalModeController Create(bool platformIsWindows) =>
        platformIsWindows
            ? new WindowsVtModeController()
            : new UnixTermiosModeController();

    /// <summary>The raw-mode controller for the platform this process runs on.</summary>
    public static ITerminalModeController CreateForCurrentPlatform() =>
        Create(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
}
