using Harbor.App.Cli.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     CAF-E slice 2 (#1290): parity + framework-behavior tests for
///     <c>ask</c>, <c>events</c>, <c>plugin</c>, <c>skill</c> and
///     <c>help</c> routed through the single ConsoleAppFramework builder in
///     <see cref="CafPilotVerbs" />. Host-free paths only: bare invocations
///     and unknown positionals must stay byte-identical to the manual verbs;
///     <c>--help</c> comes from the framework. <c>headless</c>/<c>ide</c>, a
///     valid <c>ask</c> prompt and <c>events --watch</c> build the host
///     (block on the agent host or the daemon), so they have no host-free
///     parity path — only the framework <c>--help</c> check (same as
///     <c>serve</c> in CAF-D). The <c>ide</c> test assumes the default
///     daemon-on build, matching the <c>#if HARBOR_WITH_DAEMON</c>
///     registration in <c>Program</c>.
/// </summary>
/// <remarks>
///     The bare <see cref="NotInParallelAttribute" /> is load-bearing: these
///     tests swap the process-wide <see cref="Console.Out" /> and
///     <see cref="Console.Error" /> (as do <c>CafPilotTests</c>,
///     <c>CafSessionRunTests</c> and <c>CafProvidersAuthConfigTests</c>);
///     without the constraint a concurrent swap steals our output and the
///     capture reads back empty. <c>plugin</c>/<c>skill</c> bare-list reads
///     the real plugin/skills roots, but both arms run in the same process
///     so the comparison stays exact; install/uninstall paths touch the real
///     filesystem and are deliberately untested (same as <c>--follow</c>/
///     <c>--clean</c> in <c>CafLogsTests</c>).
///     <para>
///         The parity and <c>--help</c> assertions themselves live in
///         <see cref="CafVerbTestBase" /> — this class keeps only the verb
///         and the tail.
///     </para>
/// </remarks>
[NotInParallel]
public class CafMiscTests : CafVerbTestBase
{
    [Test]
    public async Task Ask_Bare_MatchesManualByteForByte()
    {
        await AssertMatchesManualByteForByte(
            "ask", [], () => AskVerb.RunAsync(NullLogger.Instance, []), expectedExit: 1);
    }

    [Test]
    public async Task Ask_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("ask");
    }

    [Test]
    public async Task Events_Bare_MatchesManualByteForByte()
    {
        await AssertMatchesManualByteForByte("events", [], () => EventsVerb.RunAsync([]));
    }

    [Test]
    public async Task Events_UnknownPositional_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        await AssertMatchesManualByteForByte("events", tail, () => EventsVerb.RunAsync(tail), expectedExit: 2);
    }

    [Test]
    public async Task Events_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("events");
    }

    [Test]
    public async Task Headless_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("headless");
    }

    [Test]
    public async Task Ide_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("ide");
    }

    [Test]
    public async Task Plugin_Bare_MatchesManualByteForByte()
    {
        await AssertMatchesManualByteForByte(
            "plugin", [], () => new PluginsCommand(Console.Out, Console.Error).ExecuteAsync([]));
    }

    [Test]
    public async Task Plugin_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        await AssertMatchesManualByteForByte(
            "plugin", tail, () => new PluginsCommand(Console.Out, Console.Error).ExecuteAsync(tail), expectedExit: 2);
    }

    [Test]
    public async Task Plugin_InstallNoArg_MatchesManualByteForByte()
    {
        string[] tail = ["install"];
        await AssertMatchesManualByteForByte(
            "plugin", tail, () => new PluginsCommand(Console.Out, Console.Error).ExecuteAsync(tail), expectedExit: 2);
    }

    [Test]
    public async Task Plugin_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("plugin");
    }

    [Test]
    public async Task Skill_Bare_MatchesManualByteForByte()
    {
        await AssertMatchesManualByteForByte(
            "skill", [], () => new SkillsCommand(Console.Out, Console.Error).ExecuteAsync([]));
    }

    [Test]
    public async Task Skill_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        await AssertMatchesManualByteForByte(
            "skill", tail, () => new SkillsCommand(Console.Out, Console.Error).ExecuteAsync(tail), expectedExit: 2);
    }

    [Test]
    public async Task Skill_InstallNoArg_MatchesManualByteForByte()
    {
        string[] tail = ["install"];
        await AssertMatchesManualByteForByte(
            "skill", tail, () => new SkillsCommand(Console.Out, Console.Error).ExecuteAsync(tail), expectedExit: 2);
    }

    [Test]
    public async Task Skill_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("skill");
    }

    [Test]
    public async Task Help_Bare_MatchesManualByteForByte()
    {
        // The manual arm prints to stdout only, so this case compares stdout
        // alone — it does not fit the three-way parity helper.
        string direct = CaptureOut(() => HelpVerbs.PrintHelp());
        var (exit, viaCaf, _) = await RunCafAsync("help", []);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(viaCaf).IsEqualTo(direct);
    }
}
