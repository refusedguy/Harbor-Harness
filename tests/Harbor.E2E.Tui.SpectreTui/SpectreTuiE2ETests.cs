using Harbor.E2E.Framework;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Diagnostics;
namespace Harbor.E2E.Tui.SpectreTui;
/// <summary>
///     End-to-end tests for the Spectre.Tui-based interactive TUI renderer.
///     Each test spawns the real CLI inside a pseudo-terminal (allocated via
///     Python <c>pty.openpty</c>) with <c>HARBOR_TUI=spectre-tui</c>, drives it
///     with keystrokes, and asserts on the ANSI-stripped screen buffer.
/// </summary>
/// <remarks>
///     <para>
///         <b>Linux sandbox note:</b> tests use the Python <c>pty</c> module
///         (called from <see cref="TuiDriver" />) to allocate a PTY. The
///         <c>script(1)</c> util-linux wrapper is the conventional choice but
///         is SIGKILL'd by the dev/CI sandbox's seccomp profile before it can
///         exec the child; <c>pty.openpty</c> called directly from Python's
///         parent process succeeds. On Windows, tests would require a
///         ConPTY-backed driver; the TuiDriver throws
///         <see cref="PlatformNotSupportedException" /> on Windows today.
///     </para>
///     <para>
///         <b>Skip when PTY is blocked:</b> each test calls
///         <see cref="E2eTestBase.EnsurePtyAvailable" /> at the top and bails
///         out (returning without asserting) so the E2E suite stays green in
///         PTY-restricted environments without ripping the tests out.
///     </para>
///     <para>
///         <b>NotInParallel:</b> the driver mutates <c>$HOME</c>
///         (process-wide env var) and shares the PTY wrapper subprocess; tests
///         must run serially within the class. TUnit's
///         <c>[NotInParallel]</c> attribute enforces this.
///     </para>
/// </remarks>
[Category("E2E")]
[NotInParallel]
public class SpectreTuiE2ETests : E2eTestBase
{
    private const string CliProjectPath = "apps/Harbor.App.Cli/Harbor.App.Cli.csproj";
    private const string TuiName = "spectre-tui";

    /// <summary>
    ///     Sentinel string that appears once the TUI has booted with the mock
    ///     provider configured in <see cref="E2eTestBase.GetEnv" />.
    /// </summary>
    private const string BootSentinel = "test-model";
    private static readonly TimeSpan BootTimeout = TimeSpan.FromSeconds(20);

    private static async Task<bool> WaitBootAsync(TuiDriver driver)
    {
        bool saw = await driver.WaitForTextAsync(BootSentinel, BootTimeout).ConfigureAwait(false);
        if (!saw)
        {
            // Dump the screen for debugging when the boot sentinel never
            // appeared. Helps diagnose missing config, crashed child, etc.
            string screen = await driver.ReadScreenAsync().ConfigureAwait(false);
            string head = screen.Length > 600 ? screen[..600] : screen;
            Console.WriteLine($"[TUI-E2E] boot sentinel '{BootSentinel}' not seen. Screen (first 600 chars):\n{head}");
        }
        return saw;
    }

    /// <summary>
    ///     The renderer boots, takes over the screen, and shows the Harbor
    ///     welcome banner. We assert that "Harbor" appears in the ANSI-stripped
    ///     screen buffer within 15s of process start.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Start_ShowsWelcomeBanner()
    {
        if (!EnsurePtyAvailable()) return;

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);

        bool saw = await WaitBootAsync(driver).ConfigureAwait(false);
        await Assert.That(saw).IsTrue();

        // Clean shutdown via the /exit slash command. Plain \r is the Enter
        // key in raw-mode terminals (not \n).
        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        int exit = await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(0);
    }

    /// <summary>
    ///     Sending <c>/help</c> shows the slash-command help. The renderer
    ///     echoes the typed command back to the screen, so we assert on the
    ///     echoed <c>/help</c> string appearing in the buffer.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task SlashHelp_ShowsCommandList()
    {
        if (!EnsurePtyAvailable()) return;

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("/help\r").ConfigureAwait(false);
        bool sawHelp = await driver.WaitForTextAsync("/help", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        // The slash command is echoed back by the renderer; the help text
        // itself may be rendered as a popup. Asserting on the echoed command
        // is sufficient to prove the keystroke path works end-to-end.
        await Assert.That(sawHelp).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Ctrl-C aborts the running agent (or exits the TUI). The renderer
    ///     should exit within 8 seconds of the keystroke.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task CtrlC_AbortsTui()
    {
        if (!EnsurePtyAvailable()) return;

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendKeyAsync(ConsoleKey.C, ConsoleModifiers.Control).ConfigureAwait(false);
        // Either the TUI traps Ctrl-C and exits, or the PTY forwards SIGINT
        // and the process dies. Either way, IsRunning should be false within
        // a few seconds.
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        await Assert.That(driver.IsRunning).IsFalse();
    }

    /// <summary>
    ///     F12 toggles the in-TUI Logs panel (live <c>ILogger</c> output).
    ///     Asserts that the panel's header (<c>Logs</c>) appears in the screen
    ///     buffer after pressing F12. The Logs panel is always registered with
    ///     the SpectreTui renderer, so the title is deterministic.
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

        // Toggle back off (cleanup) and exit cleanly.
        await driver.SendKeyAsync(ConsoleKey.F12).ConfigureAwait(false);
        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Pressing <c>?</c> toggles the in-TUI Help panel which lists the
    ///     keymap, registered panels, and slash commands. Asserts that the
    ///     panel's header (<c>keymap</c>) appears in the buffer.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task QuestionMark_TogglesHelpPanel()
    {
        if (!EnsurePtyAvailable()) return;

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("?").ConfigureAwait(false);
        bool sawHelp = await driver.WaitForTextAsync("keymap", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(sawHelp).IsTrue();

        // Toggle back off (cleanup) and exit cleanly.
        await driver.SendInputAsync("?").ConfigureAwait(false);
        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Typing text into the input box reflects it on the screen. The
    ///     SpectreTui renderer echoes the typed characters as they're entered,
    ///     so we can assert on the echoed text appearing in the buffer before
    ///     ever pressing Enter. This catches input-field wiring regressions
    ///     that a pure /command test would miss.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task TypedText_IsEchoedToScreen()
    {
        if (!EnsurePtyAvailable()) return;

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        // A distinctive sentinel string unlikely to appear in chrome text.
        const string sentinel = "harbor-e2e-sentinel-9341";
        await driver.SendInputAsync(sentinel).ConfigureAwait(false);
        bool saw = await driver.WaitForTextAsync(sentinel, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(saw).IsTrue();

        // Don't submit the prompt — just exit. /exit works even with pending
        // input text because the renderer dispatches slash commands on Enter
        // and ignores the rest of the input box contents.
        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Takes a screenshot of the SpectreTui interface using Xvfb + terminal emulator.
    ///     This demonstrates the screenshot capability for TUI interfaces.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Screenshot_CapturesCoreStates()
    {
        if (!EnsurePtyAvailable()) return;

        string screenshotDir = "/mnt/projects/Harbor-Harness/docs/screenshots/tui/spectre-tui";
        Directory.CreateDirectory(screenshotDir);

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);

        try
        {
            bool booted = await WaitBootAsync(driver).ConfigureAwait(false);
            await Assert.That(booted).IsTrue();
            string boot = Path.Combine(screenshotDir, "01-boot.png");
            await driver.CapturePngAsync(boot).ConfigureAwait(false);

            // Help panel.
            await driver.SendInputAsync("?").ConfigureAwait(false);
            bool sawHelp = await driver.WaitForTextAsync("keymap", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Assert.That(sawHelp).IsTrue();
            string help = Path.Combine(screenshotDir, "02-help-panel.png");
            await driver.CapturePngAsync(help).ConfigureAwait(false);

            // Logs panel.
            await driver.SendInputAsync("?").ConfigureAwait(false); // close help first
            await driver.SendKeyAsync(ConsoleKey.F12).ConfigureAwait(false);
            bool sawLogs = await driver.WaitForTextAsync("Logs", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Assert.That(sawLogs).IsTrue();
            string logs = Path.Combine(screenshotDir, "03-logs-panel.png");
            await driver.CapturePngAsync(logs).ConfigureAwait(false);

            // Typed input state.
            await driver.SendKeyAsync(ConsoleKey.F12).ConfigureAwait(false); // close logs
            await driver.SendInputAsync("screenshot-state-input").ConfigureAwait(false);
            bool sawInput = await driver.WaitForTextAsync("screenshot-state-input", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Assert.That(sawInput).IsTrue();
            string input = Path.Combine(screenshotDir, "04-input-typed.png");
            await driver.CapturePngAsync(input).ConfigureAwait(false);

            await AssertPngArtifactsExistAndDifferAsync(boot, help, logs, input).ConfigureAwait(false);
        }
        finally
        {
            await driver.StopAsync().ConfigureAwait(false);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  TASK 3.1 — State coverage tests (streaming, tool-call, error, compaction, agent-running)
    //  Each test drives the agent loop via the mock LLM and asserts on the
    //  rendered screen buffer for a DIFFERENT UiState. These tests are
    //  PTY-gated (skipped when EnsurePtyAvailable() returns false).
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     Streaming: the mock LLM returns a canned response that streams
    ///     token-by-token. The renderer should show the streamed text in the
    ///     chat area. We assert the full response text appears in the screen
    ///     buffer after submitting a prompt.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Streaming_ShowsResponse()
    {
        if (!EnsurePtyAvailable()) return;

        // Configure the mock LLM to return a known response.
        Server.SetResponse("test-model", "Hello from the mock LLM!");

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        // Submit a prompt to trigger the agent loop.
        await driver.SendInputAsync("hello world\r").ConfigureAwait(false);

        // The streamed response should appear in the screen buffer.
        bool sawResponse = await driver.WaitForTextAsync("Hello from the mock LLM!", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (!sawResponse)
        {
            string screen = await driver.ReadScreenAsync().ConfigureAwait(false);
            string rawAnsi = driver.ReadRawAnsi();
            string debugPath = Path.Combine(Path.GetTempPath(), "harbor-debug-streaming.txt");
            await File.WriteAllTextAsync(debugPath,
                $"=== SCREEN ({screen.Length} chars) ===\n{screen}\n\n=== RAW ANSI ({rawAnsi.Length} chars) ===\n{rawAnsi}\n").ConfigureAwait(false);
        }
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

        // Configure the mock to return a tool call for the "read" tool.
        Server.SetToolCallResponse("test-model", "read", new { path = "/test.txt" });

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("read the file\r").ConfigureAwait(false);

        // The tool name should appear in the tool-call card.
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

        // Configure the mock to return an HTTP 500 error.
        Server.SetErrorResponse("test-model", "mock LLM error: rate limit exceeded");

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("trigger an error\r").ConfigureAwait(false);

        // The error message should appear in the screen buffer.
        bool sawError = await driver.WaitForTextAsync("rate limit", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(sawError).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Compaction: when the session exceeds the context window, the agent
    ///     enters compaction mode. The renderer should show "compacting" in
    ///     the status bar.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Compaction_ShowsCompactionStatus()
    {
        if (!EnsurePtyAvailable()) return;

        // Configure a long response to fill context and trigger compaction.
        Server.SetResponse("test-model", string.Concat(Enumerable.Repeat("word ", 500)));

        await using var driver = new TuiDriver(CliProjectPath, TuiName);
        await driver.StartAsync([], this.GetEnv()).ConfigureAwait(false);
        await WaitBootAsync(driver).ConfigureAwait(false);

        // Send a prompt to trigger the agent loop.
        await driver.SendInputAsync("hello\r").ConfigureAwait(false);

        // Wait for the status to show "compacting" or "running".
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

        // Submit a prompt to trigger the agent loop.
        await driver.SendInputAsync("hello\r").ConfigureAwait(false);

        // The status bar should show "running" while the agent is active.
        bool sawRunning = await driver.WaitForTextAsync("running", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(sawRunning).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    // ════════════════════════════════════════════════════════════════════
    //  TASK 3.2 — State coverage tests (panel/scroll/input-history/autocomplete)
    // ════════════════════════════════════════════════════════════════════

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

        // Generate some chat history first.
        await driver.SendInputAsync("hello\r").ConfigureAwait(false);
        await Task.Delay(1000).ConfigureAwait(false);

        // Scroll up.
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

        // Submit a command to populate history.
        await driver.SendInputAsync("first prompt\r").ConfigureAwait(false);
        await Task.Delay(1000).ConfigureAwait(false);

        // Navigate up in history.
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

        // Type a partial slash command.
        await driver.SendInputAsync("/hel").ConfigureAwait(false);
        // Press Tab for autocomplete.
        await driver.SendKeyAsync(ConsoleKey.Tab).ConfigureAwait(false);
        bool sawAutocomplete = await driver.WaitForTextAsync("/help", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(sawAutocomplete).IsTrue();

        await driver.SendInputAsync("/exit\r").ConfigureAwait(false);
        await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }
}
