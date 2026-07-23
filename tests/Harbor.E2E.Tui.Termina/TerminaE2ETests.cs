using Harbor.E2E.Framework;
using System.IO;
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
    private const string BootSentinel = "test-model";
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

    /// <summary>
    ///     Captures multiple screenshots to verify key UI states are preserved:
    ///     boot, slash-help echo, and typed-input state.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Screenshot_CapturesCoreStates()
    {
        if (!EnsurePtyAvailable()) return;

        string screenshotDir = "/mnt/projects/Harbor-Harness/docs/screenshots/tui/termina";
        Directory.CreateDirectory(screenshotDir);
        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);

        try
        {
            bool booted = await WaitBootAsync(driver).ConfigureAwait(false);
            await Assert.That(booted).IsTrue();
            string boot = Path.Combine(screenshotDir, "01-boot.png");
            await driver.CapturePngAsync(boot).ConfigureAwait(false);

            await driver.SendInputAsync("/help\r").ConfigureAwait(false);
            bool sawHelp = await driver.WaitForTextAsync("/help", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Assert.That(sawHelp).IsTrue();
            string help = Path.Combine(screenshotDir, "02-help.png");
            await driver.CapturePngAsync(help).ConfigureAwait(false);

            await driver.SendInputAsync("screenshot-state-input").ConfigureAwait(false);
            bool sawInput = await driver.WaitForTextAsync("screenshot-state-input", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Assert.That(sawInput).IsTrue();
            string input = Path.Combine(screenshotDir, "03-input-typed.png");
            await driver.CapturePngAsync(input).ConfigureAwait(false);

            await AssertPngArtifactsExistAndDifferAsync(boot, help, input).ConfigureAwait(false);
        }
        finally
        {
            await driver.StopAsync().ConfigureAwait(false);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  TASK 3.1 — State coverage tests (streaming, tool-call, error, compaction, agent-running)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     Streaming: the mock LLM returns a canned response that streams
    ///     token-by-token. The renderer should show the streamed text in the
    ///     chat area.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Streaming_ShowsResponse()
    {
        if (!EnsurePtyAvailable()) return;

        Server.SetResponse("test-model", "Hello from the mock LLM!");

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("hello world\r").ConfigureAwait(false);

        bool sawResponse = await driver.WaitForTextAsync("Hello from the mock LLM!", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(sawResponse).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Tool call: the mock LLM returns a tool-call response. The renderer
    ///     should render a tool-call card showing the tool name.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task ToolCall_RendersToolCard()
    {
        if (!EnsurePtyAvailable()) return;

        Server.SetToolCallResponse("test-model", "read", new { path = "/test.txt" });

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("read the file\r").ConfigureAwait(false);

        bool sawTool = await driver.WaitForTextAsync("read", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(sawTool).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Error state: when the mock LLM returns an HTTP 500 error, the
    ///     renderer should show an error message in the chat area.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task ErrorState_ShowsError()
    {
        if (!EnsurePtyAvailable()) return;

        Server.SetErrorResponse("test-model", "mock LLM error: rate limit exceeded");

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("trigger an error\r").ConfigureAwait(false);

        bool sawError = await driver.WaitForTextAsync("rate limit", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(sawError).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Compaction: the renderer should show "running" status while the
    ///     agent is active (compaction precursor).
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Compaction_ShowsCompactionStatus()
    {
        if (!EnsurePtyAvailable()) return;

        Server.SetResponse("test-model", string.Concat(Enumerable.Repeat("word ", 500)));

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("hello\r").ConfigureAwait(false);

        bool sawStatus = await driver.WaitForTextAsync("running", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(sawStatus).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Agent running: when the agent loop is active, the renderer should
    ///     show a "running" status banner.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task AgentRunning_ShowsRunningBanner()
    {
        if (!EnsurePtyAvailable()) return;

        Server.SetResponse("test-model", "Agent is responding.");

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("hello\r").ConfigureAwait(false);

        bool sawRunning = await driver.WaitForTextAsync("running", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(sawRunning).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    // ════════════════════════════════════════════════════════════════════
    //  TASK 3.2 — State coverage tests (panel/scroll/input-history/autocomplete)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     F12 toggles the Logs panel. The renderer should show the panel
    ///     header after pressing F12.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task F12_TogglesLogsPanel()
    {
        if (!EnsurePtyAvailable()) return;

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendKeyAsync(ConsoleKey.F12).ConfigureAwait(false);
        bool sawLogs = await driver.WaitForTextAsync("Logs", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(sawLogs).IsTrue();

        await driver.SendKeyAsync(ConsoleKey.F12).ConfigureAwait(false);
        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Alt+1 toggles panel focus. The renderer should show the panel
    ///     content after pressing Alt+1.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Alt1_TogglesPanel()
    {
        if (!EnsurePtyAvailable()) return;

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendKeyAsync(ConsoleKey.D1, ConsoleModifiers.Alt).ConfigureAwait(false);
        bool sawPanel = await driver.WaitForTextAsync("panel", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(sawPanel).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Ctrl+Tab cycles panel focus. The renderer should cycle through
    ///     registered panels.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task CtrlTab_CyclesPanelFocus()
    {
        if (!EnsurePtyAvailable()) return;

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendKeyAsync(ConsoleKey.Tab, ConsoleModifiers.Control).ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false);
        await driver.SendKeyAsync(ConsoleKey.Tab, ConsoleModifiers.Control).ConfigureAwait(false);
        bool sawCycle = await driver.WaitForTextAsync("test-model", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(sawCycle).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    /// <summary>
    ///     PageUp scrolls the chat history. The renderer should update the
    ///     scroll offset.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task ScrollUp_ScrollsHistory()
    {
        if (!EnsurePtyAvailable()) return;

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("hello\r").ConfigureAwait(false);
        await Task.Delay(1000).ConfigureAwait(false);

        await driver.SendKeyAsync(ConsoleKey.PageUp).ConfigureAwait(false);
        bool sawScroll = await driver.WaitForTextAsync("test-model", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(sawScroll).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Alt+Up navigates input history. The renderer should show the
    ///     previous command from history in the input box.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task AltUp_NavigatesInputHistory()
    {
        if (!EnsurePtyAvailable()) return;

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("first prompt\r").ConfigureAwait(false);
        await Task.Delay(1000).ConfigureAwait(false);

        await driver.SendKeyAsync(ConsoleKey.UpArrow, ConsoleModifiers.Alt).ConfigureAwait(false);
        bool sawHistory = await driver.WaitForTextAsync("first prompt", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(sawHistory).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Tab autocompletes slash commands. Typing "/hel" + Tab should
    ///     complete to "/help".
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Tab_AutocompleteSlashCommand()
    {
        if (!EnsurePtyAvailable()) return;

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("/hel").ConfigureAwait(false);
        await driver.SendKeyAsync(ConsoleKey.Tab).ConfigureAwait(false);
        bool sawAutocomplete = await driver.WaitForTextAsync("/help", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(sawAutocomplete).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }
}
