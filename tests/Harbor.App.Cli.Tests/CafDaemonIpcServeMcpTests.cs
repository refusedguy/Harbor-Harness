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
///     <para>
///         The parity and <c>--help</c> assertions themselves live in
///         <see cref="CafVerbTestBase" /> — this class keeps only the verb
///         and the tail.
///     </para>
/// </remarks>
[NotInParallel]
public class CafDaemonIpcServeMcpTests : CafVerbTestBase
{
    [Test]
    public async Task Daemon_Bare_MatchesManualByteForByte()
    {
        await AssertMatchesManualByteForByte("daemon", [], () => HelpVerbs.RunDaemonCommand([]), expectedExit: 1);
    }

    [Test]
    public async Task Daemon_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        await AssertMatchesManualByteForByte("daemon", tail, () => HelpVerbs.RunDaemonCommand(tail), expectedExit: 1);
    }

    [Test]
    public async Task Daemon_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("daemon");
    }

    [Test]
    public async Task Status_Bare_MatchesManualByteForByte()
    {
        await AssertMatchesManualByteForByte("status", [], () => HelpVerbs.RunStatusCommand([]));
    }

    [Test]
    public async Task Status_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("status");
    }

    [Test]
    public async Task Serve_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("serve");
    }

    [Test]
    public async Task Mcp_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        await AssertMatchesManualByteForByte("mcp", tail, () => HelpVerbs.RunMcpCommand(tail), expectedExit: 2);
    }

    [Test]
    public async Task Mcp_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("mcp");
    }
}
