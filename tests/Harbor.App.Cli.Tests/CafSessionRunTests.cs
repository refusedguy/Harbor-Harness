using Harbor.App.Cli.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     CAF-A (#1286): parity + framework-behavior tests for <c>sessions</c> and
///     <c>run</c> routed through the single ConsoleAppFramework builder in
///     <see cref="CafPilotVerbs" />. Host-free paths only (unknown sub, missing
///     args, bare usage, <c>run list</c>): bare invocations must stay
///     byte-identical to the manual verbs; <c>--help</c> comes from the framework.
///     CAF-F (#1288) types the <c>search --session/-s</c> flag the manual parser
///     accepts anywhere in the sub-args, so flag-carrying host-free paths
///     (missing query, non-search sub) are pinned too.
/// </summary>
/// <remarks>
///     The bare <see cref="NotInParallelAttribute" /> is load-bearing: these
///     tests swap the process-wide <see cref="Console.Out" /> and
///     <see cref="Console.Error" /> (as do <c>CafPilotTests</c> and
///     <c>CommandDispatchTests.Program_MainDispatch_UsesICommandArray</c>);
///     without the constraint a concurrent swap steals our output and the
///     capture reads back empty.
///     <para>
///         The parity and <c>--help</c> assertions themselves live in
///         <see cref="CafVerbTestBase" /> — this class keeps only the verb
///         and the tail.
///     </para>
/// </remarks>
[NotInParallel]
public class CafSessionRunTests : CafVerbTestBase
{
    [Test]
    public async Task Sessions_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub"];
        await AssertMatchesManualByteForByte(
            "sessions", tail, () => SessionsVerb.RunAsync(NullLogger.Instance, tail), expectedExit: 2);
    }

    [Test]
    public async Task Sessions_Rename_MissingArgs_MatchesManualByteForByte()
    {
        string[] tail = ["rename"];
        await AssertMatchesManualByteForByte(
            "sessions", tail, () => SessionsVerb.RunAsync(NullLogger.Instance, tail), expectedExit: 2);
    }

    [Test]
    public async Task Sessions_Search_MissingQuery_MatchesManualByteForByte()
    {
        string[] tail = ["search"];
        await AssertMatchesManualByteForByte(
            "sessions", tail, () => SessionsVerb.RunAsync(NullLogger.Instance, tail), expectedExit: 2);
    }

    [Test]
    public async Task Sessions_Search_SessionFlag_MissingQuery_MatchesManualByteForByte()
    {
        string[] tail = ["search", "--session", "abc123"];
        await AssertMatchesManualByteForByte(
            "sessions", tail, () => SessionsVerb.RunAsync(NullLogger.Instance, tail), expectedExit: 2);
    }

    [Test]
    public async Task Sessions_Search_ShortSessionFlag_MissingQuery_MatchesManualByteForByte()
    {
        string[] tail = ["search", "-s", "abc123"];
        await AssertMatchesManualByteForByte(
            "sessions", tail, () => SessionsVerb.RunAsync(NullLogger.Instance, tail), expectedExit: 2);
    }

    [Test]
    public async Task Sessions_NonSearch_WithSessionFlag_MatchesManualByteForByte()
    {
        string[] tail = ["--session", "abc123", "rename"];
        await AssertMatchesManualByteForByte(
            "sessions", tail, () => SessionsVerb.RunAsync(NullLogger.Instance, tail), expectedExit: 2);
    }

    [Test]
    public async Task Sessions_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("sessions");
    }

    [Test]
    public async Task Run_Bare_MatchesManualByteForByte()
    {
        await AssertMatchesManualByteForByte("run", [], () => RunTaskVerb.RunAsync([]), expectedExit: 2);
    }

    [Test]
    public async Task Run_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus"];
        await AssertMatchesManualByteForByte("run", tail, () => RunTaskVerb.RunAsync(tail), expectedExit: 1);
    }

    [Test]
    public async Task Run_List_MatchesManualByteForByte()
    {
        string[] tail = ["list"];
        await AssertMatchesManualByteForByte("run", tail, () => RunTaskVerb.RunAsync(tail), expectedExit: 0);
    }

    [Test]
    public async Task Run_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("run");
    }
}
