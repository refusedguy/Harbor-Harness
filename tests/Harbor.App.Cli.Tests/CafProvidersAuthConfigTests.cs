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
/// </remarks>
[NotInParallel]
public class CafProvidersAuthConfigTests
{
    [Test]
    public async Task Providers_Bare_MatchesManualByteForByte()
    {
        var direct = await CaptureAsync(() => ProviderVerbs.RunListProvidersAsync(NullLogger.Instance));
        var viaCaf = await RunCafAsync("providers", []);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Providers_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("providers", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Models_InvalidProvider_MatchesManualByteForByte()
    {
        const string bogus = "!!!invalid!!!";
        var direct = await CaptureAsync(() => ProviderVerbs.RunListModelsAsync(NullLogger.Instance, bogus));
        var viaCaf = await RunCafAsync("models", [bogus]);
        await Assert.That(viaCaf.Exit).IsEqualTo(1);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Models_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("models", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Auth_Bare_MatchesManualByteForByte()
    {
        var direct = await CaptureAsync(() => AuthVerb.RunAsync(NullLogger.Instance, []));
        var viaCaf = await RunCafAsync("auth", []);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Auth_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("auth", ["--help"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    [Test]
    public async Task Config_UnknownSub_MatchesManualByteForByte()
    {
        string[] tail = ["bogus-sub-xyz"];
        var direct = await CaptureAsync(() => ConfigVerb.RunAsync(NullLogger.Instance, tail));
        var viaCaf = await RunCafAsync("config", tail);
        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }

    [Test]
    public async Task Config_Help_ExitsZeroWithUsage()
    {
        var (exit, output, _) = await RunCafAsync("config", ["--help"]);
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
