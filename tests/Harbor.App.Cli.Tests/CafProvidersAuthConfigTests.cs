using Harbor.App.Cli.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     CAF-B (#1287): parity + framework-behavior tests for <c>providers</c>,
///     <c>models</c>, <c>auth</c> and <c>config</c> routed through the single
///     ConsoleAppFramework builder in <see cref="CafPilotVerbs" />. Host-backed
///     paths build a fresh host on each side, so both arms stay byte-identical
///     by construction; <c>--help</c> comes from the framework.
/// </summary>
/// <remarks>
///     The bare <see cref="NotInParallelAttribute" /> is load-bearing: these
///     tests swap the process-wide <see cref="Console.Out" /> and
///     <see cref="Console.Error" /> (as do <c>CafPilotTests</c> and
///     <c>CafSessionRunTests</c>); without the constraint a concurrent swap
///     steals our output and the capture reads back empty.
///     <para>
///         The parity and <c>--help</c> assertions themselves live in
///         <see cref="CafVerbTestBase" /> — this class keeps only the verb
///         and the tail.
///     </para>
/// </remarks>
[NotInParallel]
public class CafProvidersAuthConfigTests : CafVerbTestBase
{
    [Test]
    public async Task Providers_Bare_MatchesManualByteForByte()
    {
        await AssertMatchesManualByteForByte(
            "providers", [], () => ProviderVerbs.RunListProvidersAsync(NullLogger.Instance));
    }

    [Test]
    public async Task Providers_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("providers");
    }

    [Test]
    public async Task Models_InvalidProvider_MatchesManualByteForByte()
    {
        const string bogus = "!!!invalid!!!";
        string[] tail = [bogus];
        await AssertMatchesManualByteForByte(
            "models", tail, () => ProviderVerbs.RunListModelsAsync(NullLogger.Instance, bogus), expectedExit: 1);
    }

    [Test]
    public async Task Models_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("models");
    }

    [Test]
    public async Task Auth_Bare_MatchesManualByteForByte()
    {
        await AssertMatchesManualByteForByte("auth", [], () => AuthVerb.RunAsync(NullLogger.Instance, []));
    }

    [Test]
    public async Task Auth_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("auth");
    }

    [Test]
    public async Task Config_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        await AssertMatchesManualByteForByte(
            "config", tail, () => ConfigVerb.RunAsync(NullLogger.Instance, tail));
    }

    [Test]
    public async Task Config_Help_ExitsZeroWithUsage()
    {
        await AssertHelpExitsZeroWithUsage("config");
    }
}
