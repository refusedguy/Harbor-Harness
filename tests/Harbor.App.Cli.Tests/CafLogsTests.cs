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
/// </remarks>
[NotInParallel]
public class CafLogsTests
{
    [Test]
    public async Task Logs_Bare_MatchesManualByteForByte()
    {
        var direct = await CaptureAsync(() => HelpVerbs.RunLogsCommand([]));
        var viaCaf = await RunCafAsync("logs", []);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Logs_List_MatchesManualByteForByte()
    {
        string[] tail = ["--list"];
        var direct = await CaptureAsync(() => HelpVerbs.RunLogsCommand(tail));
        var viaCaf = await RunCafAsync("logs", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Logs_ShortList_MatchesManualByteForByte()
    {
        string[] tail = ["-l"];
        var direct = await CaptureAsync(() => HelpVerbs.RunLogsCommand(tail));
        var viaCaf = await RunCafAsync("logs", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Logs_Last_MatchesManualByteForByte()
    {
        string[] tail = ["--last"];
        var direct = await CaptureAsync(() => HelpVerbs.RunLogsCommand(tail));
        var viaCaf = await RunCafAsync("logs", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Logs_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        var direct = await CaptureAsync(() => HelpVerbs.RunLogsCommand(tail));
        var viaCaf = await RunCafAsync("logs", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(1);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Logs_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("logs", ["--help"]);
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
