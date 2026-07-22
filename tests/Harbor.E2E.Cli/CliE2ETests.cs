using Harbor.E2E.Framework;
using System.IO;
using System;
namespace Harbor.E2E.Cli;
/// <summary>
///     End-to-end tests for the Harbor CLI one-shot commands. Each test spawns
///     a real <c>Harbor.App.Cli</c> subprocess via <see cref="CliDriver" />,
///     feeds it args + env (pointing at the in-process <see cref="MockLlmServer" />
///     when needed), and asserts on captured stdout.
/// </summary>
/// <remarks>
///     All tests are tagged <c>[Category("E2E")]</c> so they can be filtered
///     with <c>dotnet test --filter "Category=E2E"</c> and run separately from
///     the fast unit test suite in CI.
/// </remarks>
[Category("E2E")]
public class CliE2ETests : E2eTestBase
{
    private const string CliProjectPath = "apps/Harbor.App.Cli/Harbor.App.Cli.csproj";

    /// <summary>
    ///     <c>harbor --version</c> exits 0 and prints the .NET runtime version.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task VersionCommand_PrintsVersion()
    {
        await using var driver = new CliDriver(CliProjectPath);
        await driver.StartAsync(["--version"], this.GetEnv()).ConfigureAwait(false);
        int exit = await driver.WaitForExitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        string output = await driver.ReadScreenAsync().ConfigureAwait(false);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Harbor");
        await Assert.That(output).Contains(".NET");
    }

    /// <summary>
    ///     <c>harbor help</c> exits 0 and lists the available commands.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task HelpCommand_ListsCommands()
    {
        await using var driver = new CliDriver(CliProjectPath);
        await driver.StartAsync(["help"], this.GetEnv()).ConfigureAwait(false);
        int exit = await driver.WaitForExitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        string output = await driver.ReadScreenAsync().ConfigureAwait(false);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("ask");
        await Assert.That(output).Contains("providers");
        await Assert.That(output).Contains("sessions");
    }

    /// <summary>
    ///     <c>harbor tui</c> lists every TUI renderer name so the user can
    ///     pick one via <c>HARBOR_TUI</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task TuiCommand_ListsAllRenderers()
    {
        await using var driver = new CliDriver(CliProjectPath);
        await driver.StartAsync(["tui"], this.GetEnv()).ConfigureAwait(false);
        int exit = await driver.WaitForExitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        string output = await driver.ReadScreenAsync().ConfigureAwait(false);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("spectre-tui");
        await Assert.That(output).Contains("termina");
        await Assert.That(output).Contains("terminal-gui");
        await Assert.That(output).Contains("razor");
    }

    /// <summary>
    ///     <c>harbor storage</c> lists all storage backends.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task StorageCommand_ListsBackends()
    {
        await using var driver = new CliDriver(CliProjectPath);
        await driver.StartAsync(["storage"], this.GetEnv()).ConfigureAwait(false);
        int exit = await driver.WaitForExitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        string output = await driver.ReadScreenAsync().ConfigureAwait(false);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("jsonl");
        await Assert.That(output).Contains("memory");
        await Assert.That(output).Contains("sqlite");
    }

    /// <summary>
    ///     <c>harbor providers</c> registers the OpenAI-compatible providers
    ///     (anthropic, openai) and always-on ollama.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task ProvidersCommand_ListsAllRegisteredProviders()
    {
        await using var driver = new CliDriver(CliProjectPath);
        await driver.StartAsync(["providers"], this.GetEnv()).ConfigureAwait(false);
        int exit = await driver.WaitForExitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        string output = await driver.ReadScreenAsync().ConfigureAwait(false);

        await Assert.That(exit).IsEqualTo(0);
        // Ollama is always registered (minimal builds). The full build adds
        // anthropic + openai + any JSON providers (including our mock).
        await Assert.That(output).Contains("ollama");
    }

    /// <summary>
    ///     <c>harbor ask "..."</c> streams the canned mock response back to stdout.
    ///     This is the truest end-to-end test of the agent pipeline: CLI →
    ///     HostBuilder → AgentLoop → OpenAiCompatibleLlmClient → MockLlmServer
    ///     (in-process HTTP) → SSE stream → renderer → stdout.
    /// </summary>
    /// <remarks>
    ///     The test sets <c>HARBOR_TUI=plain</c> so the streamed text is written
    ///     to <c>Console.Out</c> directly. Interactive renderers (spectre-tui,
    ///     termina, …) take over the alt-screen buffer and require a PTY, which
    ///     the CLI driver doesn't allocate — those are covered by the per-renderer
    ///     E2E test projects under <c>tests/Harbor.E2E.Tui.*</c>.
    /// </remarks>
    [Test]
    [Category("E2E")]
    public async Task AskCommand_WithMockServer_ReturnsResponse()
    {
        this.Server.SetResponse("test-model", "Hello from mock LLM!");

        await using var driver = new CliDriver(CliProjectPath);
        var env = this.GetEnv();
        // Plain renderer: writes streamed text directly to Console.Out for
        // non-interactive ask mode (interactive renderers need a PTY).
        env["HARBOR_TUI"] = "plain";
        await driver.StartAsync(["ask", "What is the answer?"], env).ConfigureAwait(false);
        int exit = await driver.WaitForExitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        string output = await driver.ReadScreenAsync().ConfigureAwait(false);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Hello from mock LLM!");
        // Verify the mock server actually received a chat-completion request.
        await Assert.That(this.Server.ReceivedRequests.Count).IsGreaterThan(0);
    }

    /// <summary>
    ///     Captures CLI output as a screenshot artifact for documentation.
    ///     This demonstrates the text capture capability for CLI interfaces.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task VersionCommand_CapturesScreenshot()
    {
        await using var driver = new CliDriver(CliProjectPath);
        await driver.StartAsync(["--version"], this.GetEnv()).ConfigureAwait(false);
        int exit = await driver.WaitForExitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        await Assert.That(exit).IsEqualTo(0);

        // Capture the output to docs/screenshots/cli/
        string screenshotPath = "/mnt/projects/Harbor-Harness/docs/screenshots/cli/01-version.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
        await driver.CaptureScreenAsync(screenshotPath).ConfigureAwait(false);

        await Assert.That(File.Exists(screenshotPath)).IsTrue();
        string captured = await File.ReadAllTextAsync(screenshotPath).ConfigureAwait(false);
        await Assert.That(captured).Contains("Harbor");
    }
}
