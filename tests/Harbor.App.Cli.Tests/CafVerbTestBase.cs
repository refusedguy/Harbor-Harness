using Harbor.App.Cli.Commands;
using TUnit.Assertions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Shared fixture for the CAF (ConsoleAppFramework) parity suites — the
///     <c>Caf*Tests</c> classes that pin every CLI verb's framework-routed
///     behaviour against the manual <see cref="HelpVerbs" /> dispatch.
///     <para>
///         The invariant every one of those suites asserts is the SAME three
///         things, re-typed per verb:
///     </para>
///     <list type="number">
///         <item>
///             the framework route and the manual route produce byte-identical
///             stdout, stderr and exit code;
///         </item>
///         <item>
///             the framework's <c>--help</c> exits 0 and prints a usage block;
///         </item>
///         <item>
///             the console swap is undone, whatever the verb did.
///         </item>
///     </list>
///     <para>
///         What varies is the VERB, the tail, and (for a handful of cases) the
///         expected exit code — so that is what this base takes as parameters.
///         The per-verb assertion bodies live here, once.
///     </para>
///     <para>
///         Why this base lives in the test project and NOT in
///         <c>Harbor.TestKit</c>: it is product-specific — it drives
///         <see cref="CafPilotVerbs" />, which lives in
///         <c>apps/Harbor.App.Cli</c>. A TestKit-hosted base would push the CLI
///         assembly into the closure of every TestKit consumer, and the
///         epic's rule 3 ("TestKit must not push new product assemblies into
///         consumers' closures") forbids exactly that. A base class in one test
///         project referenced by another would be worse still — a cycle.
///     </para>
/// </summary>
public abstract class CafVerbTestBase
{
    /// <summary>
    ///     Runs <paramref name="run" /> with the process console swapped for
    ///     string writers and restores the original streams afterwards.
    /// </summary>
    protected static async Task<(int Exit, string Out, string Err)> CaptureAsync(Func<Task<int>> run)
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

    /// <summary>Synchronous flavour of <see cref="CaptureAsync" /> — stdout only.</summary>
    protected static string CaptureOut(Func<int> run)
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

    /// <summary>
    ///     Runs a verb through the framework builder and captures stdout, stderr
    ///     and the exit code.
    /// </summary>
    protected static Task<(int Exit, string Out, string Err)> RunCafAsync(string verb, string[] rest)
    {
        // #1290: CAF --help leaves Environment.ExitCode untouched, so a stale
        // 1/2 from a neighbouring parse-failure test leaks into this result
        // (e2e shard: Version_Help/Storage_Help red with exit 1). Every CAF
        // route resets it before it runs.
        Environment.ExitCode = 0;
        return CaptureAsync(() => CafPilotVerbs.RunAsync(verb, rest));
    }

    /// <summary>Stdout-only flavour of <see cref="RunCafAsync" />.</summary>
    protected static async Task<(int Exit, string Output)> RunCafOutAsync(string verb, string[] rest)
    {
        Environment.ExitCode = 0;
        var original = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);
            int exit = await CafPilotVerbs.RunAsync(verb, rest).ConfigureAwait(false);
            return (exit, sw.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    /// <summary>
    ///     The framework's <c>--help</c> contract: exit 0 and a usage block.
    /// </summary>
    protected static async Task AssertHelpExitsZeroWithUsage(string verb)
    {
        var (exit, output, _) = await RunCafAsync(verb, ["--help"]).ConfigureAwait(false);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Usage");
    }

    /// <summary>
    ///     The parity contract: the framework route must be byte-identical to
    ///     the manual route. <paramref name="expectedExit" /> pins the exit
    ///     code the verb must produce when the manual route does not already
    ///     pin it.
    /// </summary>
    protected static async Task AssertMatchesManualByteForByte(
        string verb,
        string[] rest,
        Func<Task<int>> manual,
        int? expectedExit = null)
    {
        var direct = await CaptureAsync(manual).ConfigureAwait(false);
        var viaCaf = await RunCafAsync(verb, rest).ConfigureAwait(false);
        if (expectedExit is { } expected)
        {
            await Assert.That(viaCaf.Exit).IsEqualTo(expected);
        }

        await Assert.That(viaCaf.Exit).IsEqualTo(direct.Exit);
        await Assert.That(viaCaf.Out).IsEqualTo(direct.Out);
        await Assert.That(viaCaf.Err).IsEqualTo(direct.Err);
    }
}
