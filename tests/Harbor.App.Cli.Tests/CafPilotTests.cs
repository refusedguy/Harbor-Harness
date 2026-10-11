using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     LB2 pilot (#1235): parity + framework-behavior tests for the two
///     ConsoleAppFramework-routed verbs (<c>version</c>, <c>storage</c>).
///     Bare invocations must stay byte-identical to <see cref="HelpVerbs" />;
///     <c>--help</c> comes from the framework.
/// </summary>
public class CafPilotTests
{
    [Test]
    public async Task Version_Bare_PrintsHarborAndRuntime()
    {
        var (exit, output) = await RunCafAsync([], CafPilotVerbs.RunVersionAsync);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Harbor");
        await Assert.That(output).Contains(".NET");
    }

    [Test]
    public async Task Version_Bare_MatchesHelpVerbsByteForByte()
    {
        string direct = CaptureOut(() => HelpVerbs.PrintVersion());
        var (exit, viaCaf) = await RunCafAsync([], CafPilotVerbs.RunVersionAsync);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(viaCaf).IsEqualTo(direct);
    }

    [Test]
    public async Task Version_Help_ExitsZeroWithUsage()
    {
        var (exit, output) = await RunCafAsync(["--help"], CafPilotVerbs.RunVersionAsync);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Storage_Bare_ListsBackends()
    {
        var (exit, output) = await RunCafAsync([], CafPilotVerbs.RunStorageAsync);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("jsonl");
        await Assert.That(output).Contains("memory");
        await Assert.That(output).Contains("sqlite");
    }

    [Test]
    public async Task Storage_Bare_MatchesHelpVerbsByteForByte()
    {
        string direct = CaptureOut(() => HelpVerbs.PrintStorageOptions());
        var (exit, viaCaf) = await RunCafAsync([], CafPilotVerbs.RunStorageAsync);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(viaCaf).IsEqualTo(direct);
    }

    [Test]
    public async Task Storage_Help_ExitsZeroWithUsage()
    {
        var (exit, output) = await RunCafAsync(["--help"], CafPilotVerbs.RunStorageAsync);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
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

    private static async Task<(int Exit, string Output)> RunCafAsync(
        string[] args, Func<string[], Task<int>> run)
    {
        var original = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);
            int exit = await run(args).ConfigureAwait(false);
            return (exit, sw.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
