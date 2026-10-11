using ConsoleAppFramework;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     LB2 pilot (#1235): ConsoleAppFramework (incremental source generator,
///     AOT-first) on exactly two verbs — <c>version</c> and <c>storage</c>.
///     Everything else keeps the manual dispatcher in <c>Program</c>;
///     Spectre.Console.Cli / System.CommandLine stay forbidden.
/// </summary>
/// <remarks>
///     <para>
///         Each entry point builds a single-command CAF app over the args
///         <em>after</em> the verb (global <c>--script</c>/<c>--loglevel</c>
///         stripping in <c>Program.Main</c> runs first, unchanged), so
///         <c>-h|--help</c> and flag parsing come from the framework.
///         Handlers delegate to <see cref="HelpVerbs" /> — the output text
///         stays byte-for-byte identical to the manual path.
///     </para>
///     <para>
///         Exit code: CAF sets <see cref="Environment.ExitCode" /> from the
///         handler's <c>int</c> return (1 on parse/validation failure); the
///         entry points thread that value back to the caller.
///     </para>
/// </remarks>
internal static class CafPilotVerbs
{
    internal static async Task<int> RunVersionAsync(string[] rest)
    {
        await ConsoleApp.RunAsync(rest, CafPilotCommands.Version).ConfigureAwait(false);
        return Environment.ExitCode;
    }

    internal static async Task<int> RunStorageAsync(string[] rest)
    {
        await ConsoleApp.RunAsync(rest, CafPilotCommands.Storage).ConfigureAwait(false);
        return Environment.ExitCode;
    }
}

/// <summary>
///     CAF command handlers for the LB2 pilot. The XML doc comments become
///     the <c>--help</c> text (CAF reads documentation, not attributes).
/// </summary>
internal static class CafPilotCommands
{
    /// <summary>Print the Harbor version and the .NET runtime version.</summary>
    public static int Version() => HelpVerbs.PrintVersion();

    /// <summary>Print the available session storage backends.</summary>
    public static int Storage() => HelpVerbs.PrintStorageOptions();
}
