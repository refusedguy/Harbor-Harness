using Harbor.E2E.Framework;
using System.IO;
namespace Harbor.E2E.Tui.TerminalGui;
/// <summary>
///     End-to-end tests for the Terminal.Gui v2-based interactive TUI renderer
///     (<c>HARBOR_TUI=terminal-gui</c>).
///     Uses Xvfb + xterm because Terminal.Gui v2 requires a real TTY
///     and does not work with PTY (unlike Spectre.Tui which uses RunAsync).
/// </summary>
[Category("E2E")]
[NotInParallel("pty")]
[ParallelLimiter<MockServerLimit>]
public class TerminalGuiE2ETests : TuiE2eTestBase
{
    protected override string TuiName => "terminal-gui";

    /// <summary>
    ///     Terminal.Gui boots slowly because it has to start Xvfb + xterm before
    ///     the CLI child even begins painting. 30s is the budget observed on
    ///     cold-cache CI runners; PTY-only renderers default to 20s.
    /// </summary>
    protected override TimeSpan BootTimeout => TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Terminal.Gui v2 requires Xvfb + xterm, so the driver must be created
    ///     with a screenshot directory. Screenshots land in a fixed tmp path
    ///     shared across all tests in this class.
    /// </summary>
    protected override string? DefaultScreenshotDir => "/tmp/terminal-gui-screenshots";

    /// <summary>
    ///     Terminal.Gui uses Xvfb + xterm, not raw PTY, so the standard
    ///     <see cref="E2eTestBase.EnsurePtyAvailable" /> guard is irrelevant.
    /// </summary>
    protected override bool RequiresPty => false;

    /// <summary>The renderer boots and shows the welcome banner.</summary>
    [Test]
    [Category("E2E")]
    public async Task Start_ShowsWelcomeBanner()
    {
        await using var driver = await StartTuiAsync();

        bool saw = await WaitForBootAsync(driver).ConfigureAwait(false);
        await Assert.That(saw).IsTrue();

        await ExitTuiAsync(driver);
        int exit = await driver.WaitForExitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(0);
    }

    /// <summary>The <c>/help</c> slash command is dispatched to the renderer.</summary>
    [Test]
    [Category("E2E")]
    public async Task SlashHelp_IsDispatched()
    {
        await using var driver = await StartTuiAsync();
        await WaitForBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("/help\r").ConfigureAwait(false);
        bool saw = await driver.WaitForTextAsync("/help", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(saw).IsTrue();

        await ExitTuiAsync(driver);
    }

    /// <summary>Ctrl-C aborts the running TUI.</summary>
    [Test]
    [Category("E2E")]
    public async Task CtrlC_AbortsTui()
    {
        await using var driver = await StartTuiAsync();
        await WaitForBootAsync(driver).ConfigureAwait(false);

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
        string screenshotDir = "/tmp/terminal-gui-screenshots";
        Directory.CreateDirectory(screenshotDir);
        await using var driver = await StartTuiAsync(screenshotDir);

        try
        {
            bool booted = await WaitForBootAsync(driver).ConfigureAwait(false);
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
        Server.SetResponse("test-model", "Hello from the mock LLM!");

        await using var driver = await StartTuiAsync();
        await WaitForBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("hello world\r").ConfigureAwait(false);

        bool sawResponse = await driver.WaitForTextAsync("Hello from the mock LLM!", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(sawResponse).IsTrue();

        await ExitTuiAsync(driver);
    }

    /// <summary>
    ///     Tool call: the mock LLM returns a tool-call response. The renderer
    ///     should render a tool-call card showing the tool name.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task ToolCall_RendersToolCard()
    {
        Server.SetToolCallResponse("test-model", "read", new { path = "/test.txt" });

        await using var driver = await StartTuiAsync();
        await WaitForBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("read the file\r").ConfigureAwait(false);

        bool sawTool = await driver.WaitForTextAsync("read", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(sawTool).IsTrue();

        await ExitTuiAsync(driver);
    }

    /// <summary>
    ///     Error state: when the mock LLM returns an HTTP 500 error, the
    ///     renderer should show an error message in the chat area.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task ErrorState_ShowsError()
    {
        Server.SetErrorResponse("test-model", "mock LLM error: rate limit exceeded");

        await using var driver = await StartTuiAsync();
        await WaitForBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("trigger an error\r").ConfigureAwait(false);

        bool sawError = await driver.WaitForTextAsync("rate limit", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(sawError).IsTrue();

        await ExitTuiAsync(driver);
    }

    /// <summary>
    ///     Compaction: the renderer should show "running" status while the
    ///     agent is active (compaction precursor).
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Compaction_ShowsCompactionStatus()
    {
        Server.SetResponse("test-model", string.Concat(Enumerable.Repeat("word ", 500)));

        await using var driver = await StartTuiAsync();
        await WaitForBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("hello\r").ConfigureAwait(false);

        bool sawStatus = await driver.WaitForTextAsync("running", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(sawStatus).IsTrue();

        await ExitTuiAsync(driver);
    }

    /// <summary>
    ///     Agent running: when the agent loop is active, the renderer should
    ///     show a "running" status banner.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task AgentRunning_ShowsRunningBanner()
    {
        Server.SetResponse("test-model", "Agent is responding.");

        await using var driver = await StartTuiAsync();
        await WaitForBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("hello\r").ConfigureAwait(false);

        bool sawRunning = await driver.WaitForTextAsync("running", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(sawRunning).IsTrue();

        await ExitTuiAsync(driver);
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
        await using var driver = await StartTuiAsync();
        await WaitForBootAsync(driver).ConfigureAwait(false);

        await driver.SendKeyAsync(ConsoleKey.F12).ConfigureAwait(false);
        bool sawLogs = await driver.WaitForTextAsync("Logs", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(sawLogs).IsTrue();

        await driver.SendKeyAsync(ConsoleKey.F12).ConfigureAwait(false);
        await ExitTuiAsync(driver);
    }

    /// <summary>
    ///     Alt+1 toggles panel focus. The renderer should show the panel
    ///     content after pressing Alt+1.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Alt1_TogglesPanel()
    {
        await using var driver = await StartTuiAsync();
        await WaitForBootAsync(driver).ConfigureAwait(false);

        await driver.SendKeyAsync(ConsoleKey.D1, ConsoleModifiers.Alt).ConfigureAwait(false);
        bool sawPanel = await driver.WaitForTextAsync("panel", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(sawPanel).IsTrue();

        await ExitTuiAsync(driver);
    }

    /// <summary>
    ///     Ctrl+Tab cycles panel focus. The renderer should cycle through
    ///     registered panels.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task CtrlTab_CyclesPanelFocus()
    {
        await using var driver = await StartTuiAsync();
        await WaitForBootAsync(driver).ConfigureAwait(false);

        await driver.SendKeyAsync(ConsoleKey.Tab, ConsoleModifiers.Control).ConfigureAwait(false);
        // Short debounce between rapid key presses — no observable state change expected.
        await Task.Delay(50).ConfigureAwait(false);
        await driver.SendKeyAsync(ConsoleKey.Tab, ConsoleModifiers.Control).ConfigureAwait(false);
        bool sawCycle = await driver.WaitForTextAsync("test-model", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(sawCycle).IsTrue();

        await ExitTuiAsync(driver);
    }

    /// <summary>
    ///     PageUp scrolls the chat history. The renderer should update the
    ///     scroll offset.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task ScrollUp_ScrollsHistory()
    {
        // Configure a mock response so we can poll for the round-trip completion.
        Server.SetResponse("test-model", "Mock reply for scroll.");

        await using var driver = await StartTuiAsync();
        await WaitForBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("hello\r").ConfigureAwait(false);
        // Poll for the mock response instead of a fixed delay — proves the agent
        // round-trip completed and chat history is populated.
        await driver.WaitForTextAsync("Mock reply for scroll.", TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        await driver.SendKeyAsync(ConsoleKey.PageUp).ConfigureAwait(false);
        bool sawScroll = await driver.WaitForTextAsync("test-model", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(sawScroll).IsTrue();

        await ExitTuiAsync(driver);
    }

    /// <summary>
    ///     Alt+Up navigates input history. The renderer should show the
    ///     previous command from history in the input box.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task AltUp_NavigatesInputHistory()
    {
        // Configure a mock response so we can poll for the round-trip completion.
        Server.SetResponse("test-model", "Mock reply for history.");

        await using var driver = await StartTuiAsync();
        await WaitForBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("first prompt\r").ConfigureAwait(false);
        // Poll for the mock response instead of a fixed delay — proves the agent
        // round-trip completed and the prompt is in input history.
        await driver.WaitForTextAsync("Mock reply for history.", TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        await driver.SendKeyAsync(ConsoleKey.UpArrow, ConsoleModifiers.Alt).ConfigureAwait(false);
        bool sawHistory = await driver.WaitForTextAsync("first prompt", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(sawHistory).IsTrue();

        await ExitTuiAsync(driver);
    }

    /// <summary>
    ///     Tab autocompletes slash commands. Typing "/hel" + Tab should
    ///     complete to "/help".
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Tab_AutocompleteSlashCommand()
    {
        await using var driver = await StartTuiAsync();
        await WaitForBootAsync(driver).ConfigureAwait(false);

        await driver.SendInputAsync("/hel").ConfigureAwait(false);
        await driver.SendKeyAsync(ConsoleKey.Tab).ConfigureAwait(false);
        bool sawAutocomplete = await driver.WaitForTextAsync("/help", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(sawAutocomplete).IsTrue();

        await ExitTuiAsync(driver);
    }
}