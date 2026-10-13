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
/// </remarks>
[NotInParallel]
public class CafMiscTests
{
    [Test]
    public async Task Ask_Bare_MatchesManualByteForByte()
    {
        var direct = await CaptureAsync(() => AskVerb.RunAsync(NullLogger.Instance, []));
        var viaCaf = await RunCafAsync("ask", []);
        await Assert.That(viaCaf.Exit).IsEqualTo(1);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Ask_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("ask", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Events_Bare_MatchesManualByteForByte()
    {
        var direct = await CaptureAsync(() => EventsVerb.RunAsync([]));
        var viaCaf = await RunCafAsync("events", []);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Events_UnknownPositional_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        var direct = await CaptureAsync(() => EventsVerb.RunAsync(tail));
        var viaCaf = await RunCafAsync("events", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Events_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("events", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Headless_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("headless", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Ide_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("ide", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Plugin_Bare_MatchesManualByteForByte()
    {
        var direct = await CaptureAsync(() => new PluginsCommand(Console.Out, Console.Error).ExecuteAsync([]));
        var viaCaf = await RunCafAsync("plugin", []);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Plugin_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        var direct = await CaptureAsync(() => new PluginsCommand(Console.Out, Console.Error).ExecuteAsync(tail));
        var viaCaf = await RunCafAsync("plugin", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Plugin_InstallNoArg_MatchesManualByteForByte()
    {
        string[] tail = ["install"];
        var direct = await CaptureAsync(() => new PluginsCommand(Console.Out, Console.Error).ExecuteAsync(tail));
        var viaCaf = await RunCafAsync("plugin", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Plugin_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("plugin", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Skill_Bare_MatchesManualByteForByte()
    {
        var direct = await CaptureAsync(() => new SkillsCommand(Console.Out, Console.Error).ExecuteAsync([]));
        var viaCaf = await RunCafAsync("skill", []);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Skill_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        var direct = await CaptureAsync(() => new SkillsCommand(Console.Out, Console.Error).ExecuteAsync(tail));
        var viaCaf = await RunCafAsync("skill", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Skill_InstallNoArg_MatchesManualByteForByte()
    {
        string[] tail = ["install"];
        var direct = await CaptureAsync(() => new SkillsCommand(Console.Out, Console.Error).ExecuteAsync(tail));
        var viaCaf = await RunCafAsync("skill", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Skill_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("skill", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Help_Bare_MatchesManualByteForByte()
    {
        string direct = CaptureOut(() => HelpVerbs.PrintHelp());
        var (exit, viaCaf, _) = await RunCafAsync("help", []);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(viaCaf).IsEqualTo(direct);
    }

    private static string CaptureOut(Func<int> run)
    {
        var original = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);
            run();
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
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
