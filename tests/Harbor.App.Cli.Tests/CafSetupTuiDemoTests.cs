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
/// </remarks>
[NotInParallel]
public class CafSetupTuiDemoTests
{
    [Test]
    public async Task Tui_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        var direct = await CaptureAsync(() => HelpVerbs.RunTuiCommand(tail));
        var viaCaf = await RunCafAsync("tui", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Demo_UnknownPositional_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        var direct = await CaptureAsync(() => HelpVerbs.RunDemoCommand(tail));
        var viaCaf = await RunCafAsync("demo", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Demo_BadScene_MatchesManualByteForByte()
    {
        string[] tail = ["--scene", "bogus"];
        var direct = await CaptureAsync(() => HelpVerbs.RunDemoCommand(tail));
        var viaCaf = await RunCafAsync("demo", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Demo_ShortSceneAlias_MatchesManualByteForByte()
    {
        string[] tail = ["-s", "bogus"];
        var direct = await CaptureAsync(() => HelpVerbs.RunDemoCommand(tail));
        var viaCaf = await RunCafAsync("demo", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Demo_BadTui_MatchesManualByteForByte()
    {
        string[] tail = ["--tui", "bogus"];
        var direct = await CaptureAsync(() => HelpVerbs.RunDemoCommand(tail));
        var viaCaf = await RunCafAsync("demo", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Setup_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("setup", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Tui_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("tui", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Demo_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("demo", ["--help"]);
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
