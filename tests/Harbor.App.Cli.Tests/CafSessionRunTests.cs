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
/// </remarks>
[NotInParallel]
public class CafSessionRunTests
{
    [Test]
    public async Task Sessions_UnknownSub_MatchesManualByteForByte()
    {
        var direct = await CaptureAsync(() => SessionsVerb.RunAsync(NullLogger.Instance, ["bogus-sub"]));
        var viaCaf = await RunCafAsync("sessions", ["bogus-sub"]);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Sessions_Rename_MissingArgs_MatchesManualByteForByte()
    {
        var direct = await CaptureAsync(() => SessionsVerb.RunAsync(NullLogger.Instance, ["rename"]));
        var viaCaf = await RunCafAsync("sessions", ["rename"]);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Sessions_Search_MissingQuery_MatchesManualByteForByte()
    {
        var direct = await CaptureAsync(() => SessionsVerb.RunAsync(NullLogger.Instance, ["search"]));
        var viaCaf = await RunCafAsync("sessions", ["search"]);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Sessions_Search_SessionFlag_MissingQuery_MatchesManualByteForByte()
    {
        string[] tail = ["search", "--session", "abc123"];
        var direct = await CaptureAsync(() => SessionsVerb.RunAsync(NullLogger.Instance, tail));
        var viaCaf = await RunCafAsync("sessions", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Sessions_Search_ShortSessionFlag_MissingQuery_MatchesManualByteForByte()
    {
        string[] tail = ["search", "-s", "abc123"];
        var direct = await CaptureAsync(() => SessionsVerb.RunAsync(NullLogger.Instance, tail));
        var viaCaf = await RunCafAsync("sessions", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Sessions_NonSearch_WithSessionFlag_MatchesManualByteForByte()
    {
        string[] tail = ["--session", "abc123", "rename"];
        var direct = await CaptureAsync(() => SessionsVerb.RunAsync(NullLogger.Instance, tail));
        var viaCaf = await RunCafAsync("sessions", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Sessions_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("sessions", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Run_Bare_MatchesManualByteForByte()
    {
        var direct = await CaptureAsync(() => RunTaskVerb.RunAsync([]));
        var viaCaf = await RunCafAsync("run", []);
        await Assert.That(viaCaf.Exit).IsEqualTo(2);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Run_UnknownSub_MatchesManualByteForByte()
    {
        var direct = await CaptureAsync(() => RunTaskVerb.RunAsync(["bogus"]));
        var viaCaf = await RunCafAsync("run", ["bogus"]);
        await Assert.That(viaCaf.Exit).IsEqualTo(1);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Run_List_MatchesManualByteForByte()
    {
        var direct = await CaptureAsync(() => RunTaskVerb.RunAsync(["list"]));
        var viaCaf = await RunCafAsync("run", ["list"]);
        await Assert.That(viaCaf.Exit).IsEqualTo(0);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Run_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("run", ["--help"]);
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
