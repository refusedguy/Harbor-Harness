using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     CAF-C (#1288): parity + framework-behavior tests for <c>logs</c> routed
///     through the single ConsoleAppFramework builder in
///     <see cref="CafPilotVerbs" />. Unlike CAF-A/CAF-B (raw positional tails),
///     the handler declares the verb's flags explicitly (<c>--list</c>,
///     <c>--last</c>, <c>--follow</c>, <c>--clean</c> with single-letter
///     aliases) because flags ARE the interface here; bare + flag + error-usage
///     paths must stay byte-identical to <c>LogsCommand</c>;
///     <c>--help</c> comes from the framework.
/// </summary>
/// <remarks>
///     The bare <see cref="NotInParallelAttribute" /> is load-bearing: these
///     tests swap the process-wide <see cref="Console.Out" /> and
///     <see cref="Console.Error" /> (as do <c>CafPilotTests</c>,
///     <c>CafSessionRunTests</c> and <c>CafProvidersAuthConfigTests</c>);
///     without the constraint a concurrent swap steals our output and the
///     capture reads back empty. <c>--follow</c> (tails forever) and
///     <c>--clean</c> (prompts, deletes real log files) are deliberately
///     untested.
///     <para>
///         The parity and <c>--help</c> assertions themselves live in
///         <see cref="CafVerbTestBase" /> — this class keeps only the verb
///         and the tail.
///     </para>
/// </remarks>
[NotInParallel]
public class CafLogsTests : CafVerbTestBase
{
    [Test]
    public async Task Logs_Bare_MatchesManualByteForByte()
    {
        await AssertMatchesManualByteForByte("logs", [], () => HelpVerbs.RunLogsCommand([]));
    }

    [Test]
    public async Task Logs_List_MatchesManualByteForByte()
    {
        string[] tail = ["--list"];
        await AssertMatchesManualByteForByte("logs", tail, () => HelpVerbs.RunLogsCommand(tail));
    }

    [Test]
    public async Task Logs_ShortList_MatchesManualByteForByte()
    {
        string[] tail = ["-l"];
        await AssertMatchesManualByteForByte("logs", tail, () => HelpVerbs.RunLogsCommand(tail));
    }

    [Test]
    public async Task Logs_Last_MatchesManualByteForByte()
    {
        string[] tail = ["--last"];
        await AssertMatchesManualByteForByte("logs", tail, () => HelpVerbs.RunLogsCommand(tail));
    }

    [Test]
    public async Task Logs_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        await AssertMatchesManualByteForByte("logs", tail, () => HelpVerbs.RunLogsCommand(tail), expectedExit: 1);
    }

    [Test]
    public async Task Logs_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("logs");
    }
}
