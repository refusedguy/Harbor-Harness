using System.Runtime.InteropServices;

namespace Harbor.Tui.ConsoleEx.Input;

/// <summary>
/// Windows VT-input stub (design §6 matrix). CE-0 stubs the Windows branch
/// behind this interface per the sprint contract: the byte pipeline is
/// platform-agnostic, only the mode controller is OS-specific. Full
/// SetConsoleMode implementation (ENABLE_VIRTUAL_TERMINAL_INPUT, disable
/// LINE/ECHO/PROCESSED) lands with the Windows bring-up sprint; until then a
/// ConsoleEx renderer on Windows must refuse to initialize and fall back to
/// AnsiTuiRenderer.
/// </summary>
public sealed class WindowsVtModeController : ITerminalModeController
{
    public bool IsRaw { get; private set; }

    public void Enter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("WindowsVtModeController is only meaningful on Windows.");
        }

        throw new PlatformNotSupportedException(
            "ConsoleEx input requires Windows 10+ VT input (SetConsoleMode); not wired in CE-0 — " +
            "the renderer must fall back to AnsiTuiRenderer.");
    }

    public void Restore() => IsRaw = false;
}
