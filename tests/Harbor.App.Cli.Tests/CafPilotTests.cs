using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     LB2 pilot (#1235): parity + framework-behavior tests for the two
///     ConsoleAppFramework-routed verbs (<c>version</c>, <c>storage</c>).
///     Bare invocations must stay byte-identical to <see cref="HelpVerbs" />;
///     <c>--help</c> comes from the framework.
/// </summary>
/// <remarks>
///     The bare <see cref="NotInParallelAttribute" /> is load-bearing:
///     TUnit runs classes in parallel and these tests swap the process-wide
///     <see cref="Console.Out" /> (as does
///     <c>CommandDispatchTests.Program_MainDispatch_UsesICommandArray</c>);
///     without the constraint a concurrent swap steals our output and the
///     capture reads back empty (e2e shard: 2/6 red with exit 0).
///     <para>
///         The <c>--help</c> assertion itself lives in
///         <see cref="CafVerbTestBase" /> — this class keeps the verb-specific
///         output checks and the stdout-only parity arms.
///     </para>
/// </remarks>
[NotInParallel]
public class CafPilotTests : CafVerbTestBase
{
    [Test]
    public async Task Version_Bare_PrintsHarborAndRuntime()
    {
        var (exit, output) = await RunCafOutAsync("version", []);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Harbor");
        await Assert.That(output).Contains(".NET");
    }

    [Test]
    public async Task Version_Bare_MatchesHelpVerbsByteForByte()
    {
        string direct = CaptureOut(() => HelpVerbs.PrintVersion());
        var (exit, viaCaf) = await RunCafOutAsync("version", []);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(viaCaf).IsEqualTo(direct);
    }

    [Test]
    public async Task Version_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("version");
    }

    [Test]
    public async Task Storage_Bare_ListsBackends()
    {
        var (exit, output) = await RunCafOutAsync("storage", []);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("jsonl");
        await Assert.That(output).Contains("memory");
        await Assert.That(output).Contains("sqlite");
    }

    [Test]
    public async Task Storage_Bare_MatchesHelpVerbsByteForByte()
    {
        string direct = CaptureOut(() => HelpVerbs.PrintStorageOptions());
        var (exit, viaCaf) = await RunCafOutAsync("storage", []);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(viaCaf).IsEqualTo(direct);
    }

    [Test]
    public async Task Storage_ExtraPositional_MatchesHelpVerbsByteForByte()
    {
        // CAF-F (#1288): the manual dispatch ignored the tail
        // ("storage" => HelpVerbs.PrintStorageOptions()), so an extra
        // positional must still print the backends (exit 0) instead of
        // failing closed at the parser (exit 1).
        string direct = CaptureOut(() => HelpVerbs.PrintStorageOptions());
        var (exit, viaCaf) = await RunCafOutAsync("storage", ["bogus-extra"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(viaCaf).IsEqualTo(direct);
    }

    [Test]
    public async Task Storage_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("storage");
    }
}
