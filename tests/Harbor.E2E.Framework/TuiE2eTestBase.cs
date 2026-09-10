using System.IO;
using System.Threading.Tasks;
using Harbor.E2E.Framework;

namespace Harbor.E2E.Framework;

/// <summary>Shared base for renderer-specific E2E tests that drive <see cref="TuiDriver"/>.</summary>
public abstract class TuiE2eTestBase : E2eTestBase
{
    private const string CliProjectPath = "apps/Harbor.App.Cli/Harbor.App.Cli.csproj";
    private const string BootSentinel = "test-model";

    /// <summary>Renderer name passed to <c>HARBOR_TUI</c>.</summary>
    protected abstract string TuiName { get; }

    /// <summary>Boot timeout for this renderer.</summary>
    protected virtual TimeSpan BootTimeout => TimeSpan.FromSeconds(20);

    /// <summary>Optional screenshot directory.</summary>
    protected virtual string? DefaultScreenshotDir => null;

    /// <summary>Whether this renderer requires PTY.</summary>
    protected virtual bool RequiresPty => true;

    /// <summary>Optional screenshot directory passed to <see cref="TuiDriver"/>.</summary>
    protected virtual string? ScreenshotDir => null;

    /// <summary>Start the TUI driver and wait for the boot sentinel.</summary>
    protected async Task<TuiDriver> StartTuiAsync(string? screenshotDir = null)
    {
        if (RequiresPty) EnsurePtyAvailable();
        screenshotDir ??= ScreenshotDir;
        var driver = screenshotDir is null
            ? new TuiDriver(CliProjectPath, TuiName)
            : new TuiDriver(CliProjectPath, TuiName, screenshotDir);
        await driver.StartAsync([], GetEnv()).ConfigureAwait(false);
        bool saw = await WaitForBootAsync(driver).ConfigureAwait(false);
        if (!saw)
        {
            string screen = await driver.ReadScreenAsync().ConfigureAwait(false);
            string head = screen.Length > 600 ? screen[..600] : screen;
            System.Console.WriteLine($"[TUI-E2E] boot sentinel '{BootSentinel}' not seen. Screen (first 600 chars):\n{head}");
        }
        return driver;
    }

    /// <summary>Clean shutdown via <c>/exit</c>.</summary>
    protected static async Task ExitTuiAsync(TuiDriver driver)
    {
        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    protected async Task<bool> WaitForBootAsync(TuiDriver driver)
    {
        bool saw = await driver.WaitForTextAsync(BootSentinel, BootTimeout).ConfigureAwait(false);
        if (!saw)
        {
            string screen = await driver.ReadScreenAsync().ConfigureAwait(false);
            string head = screen.Length > 600 ? screen[..600] : screen;
            System.Console.WriteLine($"[TUI-E2E] boot sentinel '{BootSentinel}' not seen. Screen (first 600 chars):\n{head}");
        }
        return saw;
    }
}
