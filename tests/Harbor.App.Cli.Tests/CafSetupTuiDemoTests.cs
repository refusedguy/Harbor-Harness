using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     CAF-E (#1290): parity + framework-behavior tests for <c>setup</c>,
///     <c>tui</c> and <c>demo</c> routed through the single
///     ConsoleAppFramework builder in <see cref="CafPilotVerbs" />. Host-free
///     paths only: <c>tui</c>/<c>demo</c> parse errors (exit 2, byte-identical
///     to the manual verbs) and the framework <c>--help</c> checks.
///     <c>setup</c> bare runs the onboarding wizard (builds the host), and
///     valid <c>tui</c>/<c>demo</c> invocations attach to the daemon or play
///     the scripted scenes — none of those have a host-free parity path
///     (same as <c>serve</c> in CAF-D).
/// </summary>
/// <remarks>
///     The bare <see cref="NotInParallelAttribute" /> is load-bearing: these
///     tests swap the process-wide <see cref="Console.Out" /> and
///     <see cref="Console.Error" /> (as do <c>CafPilotTests</c>,
///     <c>CafSessionRunTests</c> and <c>CafProvidersAuthConfigTests</c>);
///     without the constraint a concurrent swap steals our output and the
///     capture reads back empty.
///     <para>
///         The parity and <c>--help</c> assertions themselves live in
///         <see cref="CafVerbTestBase" /> — this class keeps only the verb
///         and the tail.
///     </para>
/// </remarks>
[NotInParallel]
public class CafSetupTuiDemoTests : CafVerbTestBase
{
    [Test]
    public async Task Tui_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        await AssertMatchesManualByteForByte("tui", tail, () => HelpVerbs.RunTuiCommand(tail), expectedExit: 2);
    }

    [Test]
    public async Task Demo_UnknownPositional_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        await AssertMatchesManualByteForByte("demo", tail, () => HelpVerbs.RunDemoCommand(tail), expectedExit: 2);
    }

    [Test]
    public async Task Demo_BadScene_MatchesManualByteForByte()
    {
        string[] tail = ["--scene", "bogus"];
        await AssertMatchesManualByteForByte("demo", tail, () => HelpVerbs.RunDemoCommand(tail), expectedExit: 2);
    }

    [Test]
    public async Task Demo_ShortSceneAlias_MatchesManualByteForByte()
    {
        string[] tail = ["-s", "bogus"];
        await AssertMatchesManualByteForByte("demo", tail, () => HelpVerbs.RunDemoCommand(tail), expectedExit: 2);
    }

    [Test]
    public async Task Demo_BadTui_MatchesManualByteForByte()
    {
        string[] tail = ["--tui", "bogus"];
        await AssertMatchesManualByteForByte("demo", tail, () => HelpVerbs.RunDemoCommand(tail), expectedExit: 2);
    }

    [Test]
    public async Task Setup_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("setup");
    }

    [Test]
    public async Task Tui_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("tui", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
        // #1290: the renderer list lives under --help by contract (038f17a9),
        // pinned end-to-end by CliE2ETests.TuiCommand_ListsAllRenderers — the
        // framework help carries it in the command summary.
        await Assert.That(output).Contains("spectre-tui");
        await Assert.That(output).Contains("termina");
    }

    [Test]
    public async Task Demo_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("demo");
    }
}
