using Harbor.Tui.CellForge.Input;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Platform selection for the raw-mode controller (#1249 bring-up slice 2).
///     The decision is pure in its platform bit precisely so these run on
///     every OS the CI matrix covers (windows / ubuntu / macos): before the
///     seam existed, the only place that named a controller was a
///     <c>RuntimeInformation</c> ternary inside the composition root, so on a
///     Linux runner nothing at all verified what Windows would be handed.
/// </summary>
public class TerminalModeControllersTests
{
    [Test]
    public async Task WindowsPlatform_SelectsTheWin32Controller()
    {
        ITerminalModeController controller = TerminalModeControllers.Create(platformIsWindows: true);

        await Assert.That(controller).IsTypeOf<WindowsVtModeController>()
            .Because("Windows raw mode is the Win32 Console-API controller (SetConsoleMode + VIRTUAL_TERMINAL_INPUT).");
        await Assert.That(controller.IsRaw).IsFalse()
            .Because("a fresh controller must not claim raw mode before Enter is called.");
    }

    [Test]
    public async Task NonWindowsPlatform_SelectsTheTermiosController()
    {
        ITerminalModeController controller = TerminalModeControllers.Create(platformIsWindows: false);

        await Assert.That(controller).IsTypeOf<UnixTermiosModeController>()
            .Because("every non-Windows platform raw-modes through termios.");
        await Assert.That(controller.IsRaw).IsFalse();
    }

    [Test]
    public async Task EveryCall_ReturnsAnIndependentControllerInstance()
    {
        ITerminalModeController first = TerminalModeControllers.Create(platformIsWindows: true);
        ITerminalModeController second = TerminalModeControllers.Create(platformIsWindows: true);

        await Assert.That(first).IsNotSameReferenceAs(second)
            .Because("a controller owns saved terminal state — two callers must never share one.");
    }

    [Test]
    public async Task CurrentPlatform_AgreesWithTheHostOS()
    {
        ITerminalModeController controller = TerminalModeControllers.CreateForCurrentPlatform();

        if (OperatingSystem.IsWindows())
        {
            await Assert.That(controller).IsTypeOf<WindowsVtModeController>()
                .Because("the host is Windows, so the Win32 controller is the platform controller.");
        }
        else
        {
            await Assert.That(controller).IsTypeOf<UnixTermiosModeController>()
                .Because("the host is not Windows, so termios is the platform controller.");
        }
    }
}
