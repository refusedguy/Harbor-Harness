using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     CAF-D (#1289): parity + framework-behavior tests for <c>daemon</c>,
///     <c>status</c> (ipc), <c>serve</c> and <c>mcp</c> routed through the
///     single ConsoleAppFramework builder in <see cref="CafPilotVerbs" />.
///     Host-free paths only (bare usage, unknown sub): bare invocations must
///     stay byte-identical to the manual verbs; <c>--help</c> comes from the
///     framework. <c>serve</c> bare starts the foreground host (blocks on the
///     agent host build), so it has no host-free parity path — only the
///     framework <c>--help</c> check; <c>mcp</c> bare reads the real
///     <c>mcp.json</c> overlay, so only the config-free unknown-sub path is
///     pinned here (<c>McpLoginRunnerTests</c> covers list/login/logout
///     against an isolated config).
/// </summary>
/// <remarks>
///     The bare <see cref="NotInParallelAttribute" /> is load-bearing: these
///     tests swap the process-wide <see cref="Console.Out" /> and
///     <see cref="Console.Error" /> (as do <c>CafPilotTests</c>,
///     <c>CafSessionRunTests</c> and <c>CafProvidersAuthConfigTests</c>);
///     without the constraint a concurrent swap steals our output and the
///     capture reads back empty. The <c>status</c> tests assume the default
///     daemon-on build, matching the <c>#if HARBOR_WITH_DAEMON</c>
///     registration in <c>Program</c>.
/// </remarks>
[NotInParallel]
public class CafDaemonIpcServeMcpTests
{
    [Test]
    public async Task Daemon_Bare_MatchesManualByteForByte()
    {
        var direct = await CaptureAsync(() => HelpVerbs.RunDaemonCommand([]));
        var viaCaf = await RunCafAsync("daemon", []);
        await Assert.That(viaCaf.Exit).IsEqualTo(1);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Daemon_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        var direct = await CaptureAsync(() => HelpVerbs.RunDaemonCommand(tail));
        var viaCaf = await RunCafAsync("daemon", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(1);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Daemon_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("daemon", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Status_Bare_MatchesManualByteForByte()
    {
        var direct = await CaptureAsync(() => HelpVerbs.RunStatusCommand([]));
        var viaCaf = await RunCafAsync("status", []);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Status_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("status", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Serve_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("serve", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Mcp_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        var direct = await CaptureAsync(() => HelpVerbs.RunMcpCommand(tail));
        var viaCaf = await RunCafAsync("mcp", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Mcp_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("mcp", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    private static async Task<(int Exit, string Out, string Err)> CaptureAsync(Func<Task<int>> run)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        try
        {
            var swOut = new StringWriter();
            var swErr = new StringWriter();
            Console.SetOut(swOut);
            Console.SetError(swErr);
            int exit = await run().ConfigureAwait(false);
            return (exit, swOut.ToString(), swErr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    private static Task<(int Exit, string Out, string Err)> RunCafAsync(string verb, string[] rest)
    {
        Environment.ExitCode = 0;
        return CaptureAsync(() => CafPilotVerbs.RunAsync(verb, rest));
    }
}
