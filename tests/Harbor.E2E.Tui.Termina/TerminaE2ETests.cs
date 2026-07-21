using Harbor.E2E.Framework;
namespace Harbor.E2E.Tui.Termina;
/// <summary>
///     End-to-end tests for the Termina-based interactive TUI renderer
///     (<c>HARBOR_TUI=termina</c>). Drives the CLI inside a PTY and asserts on the
///     ANSI-stripped screen buffer. See <c>docs/E2E_TESTING.md</c> for the
///     platform matrix.
/// </summary>
[Category("E2E")]
[NotInParallel]
public class TerminaE2ETests : E2eTestBase
{
    private const string CliProjectPath = "apps/Harbor.App.Cli/Harbor.App.Cli.csproj";
    private const string TuiName = "termina";

    /// <summary>
    ///     Sentinel string that appears in the renderer's footer/header
    ///     immediately after boot. Used as a stable "TUI is up" signal —
    ///     more reliable than brand text which varies by configured provider.
    /// </summary>
    private const string BootSentinel = "INPUT";
    private static readonly TimeSpan BootTimeout = TimeSpan.FromSeconds(20);

    private static async Task<bool> WaitBootAsync(TuiDriver driver)
    {
        bool saw = await driver.WaitForTextAsync(BootSentinel, BootTimeout).ConfigureAwait(false);
        if (!saw)
        {
            string screen = await driver.ReadScreenAsync().ConfigureAwait(false);
            string head = screen.Length > 600 ? screen[..600] : screen;
            Console.WriteLine($"[TUI-E2E] boot sentinel '{BootSentinel}' not seen. Screen (first 600 chars):\n{head}");
        }
        return saw;
    }

    /// <summary>The renderer boots and shows the welcome banner.</summary>
    [Test]
    [Category("E2E")]
    public async Task Start_ShowsWelcomeBanner()
    {
        if (!EnsurePtyAvailable()) return;

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);

        bool saw = await WaitBootAsync(driver).ConfigureAwait(false);
        await Assert.That(saw).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        int exit = await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(0);
    }

    /// <summary>The <c>/help</c> slash command is dispatched to the renderer.</summary>
    [Test]
    [Category("E2E")]
    public async Task SlashHelp_IsDispatched()
    {
        if (!EnsurePtyAvailable()) return;

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("/help\r").ConfigureAwait(false);
        bool saw = await driver.WaitForTextAsync("/help", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(saw).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    /// <summary>Ctrl-C aborts the running TUI.</summary>
    [Test]
    [Category("E2E")]
    public async Task CtrlC_AbortsTui()
    {
        if (!EnsurePtyAvailable()) return;

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendKeyAsync(ConsoleKey.C, ConsoleModifiers.Control).ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        await Assert.That(driver.IsRunning).IsFalse();
    }
}
