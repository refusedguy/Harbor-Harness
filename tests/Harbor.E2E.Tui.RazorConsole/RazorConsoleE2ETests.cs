using Harbor.E2E.Framework;
using TUnit.Core.Enums;

namespace Harbor.E2E.Tui.RazorConsole;

/// <summary>
///     End-to-end tests for the RazorConsole-based interactive TUI renderer
///     (<c>HARBOR_TUI=razor</c>).
/// </summary>
[Category("E2E")]
public class RazorConsoleE2ETests : E2eTestBase
{
    private const string CliProjectPath = "apps/Harbor.App.Cli/Harbor.App.Cli.csproj";
    private const string TuiName = "razor";

    /// <summary>The renderer boots and shows the welcome banner.</summary>
    [Test]
    [Category("E2E")]
    public async Task Start_ShowsWelcomeBanner()
    {
        if (!EnsurePtyAvailable()) return;

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync(args: [], env: GetEnv()).ConfigureAwait(false);

        bool saw = await driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
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
        await driver.StartAsync(args: [], env: GetEnv()).ConfigureAwait(false);
        await driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(15)).ConfigureAwait(false);

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
        await driver.StartAsync(args: [], env: GetEnv()).ConfigureAwait(false);
        await driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        await driver.SendKeyAsync(ConsoleKey.C, ConsoleModifiers.Control).ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        await Assert.That(driver.IsRunning).IsFalse();
    }
}
