namespace Harbor.App.Cli.Commands;

/// <summary>
///     The single source of truth for the CLI's top-level verbs (#2669).
///     Both the <c>harbor help</c> usage line and the generated shell
///     completion scripts are projected from <see cref="All"/>, so the two
///     lists cannot drift apart. Plain data and string shaping only: no
///     reflection, no NuGet packages, no AOT-hostile constructs.
/// </summary>
/// <remarks>
///     This is a catalog of NAMES, not of handlers. Registration of a handler
///     still lives in <c>CafPilotVerbs</c> and dispatch in <c>Program</c>;
///     adding a verb there means adding it here too, and the
///     <c>CompletionVerbsTests</c> suite makes an omitted catalog entry
///     visible by comparing the help output with the generated scripts.
/// </remarks>
internal static class CliVerbCatalog
{
    /// <summary>Top-level verbs, in the order <c>harbor help</c> advertises them.</summary>
    internal static readonly string[] All =
    [
        "ask",
        "run",
        "demo",
        "setup",
        "auth",
        "config",
        "doctor",
        "providers",
        "models",
        "sessions",
        "mcp",
        "serve",
        "tui",
        "events",
        "storage",
        "logs",
        "help",
        "version",
        "completions",
    ];

    /// <summary>
    ///     The bracketed verb enumeration used by the <c>harbor help</c>
    ///     usage line. The two verbs with arguments keep their documented
    ///     syntax; everything else is the bare name.
    /// </summary>
    internal static string FormatUsageVerbs() => string.Join("|", All.Select(static verb => verb switch
    {
        "ask" => "ask <prompt>",
        "run" => "run (task agent=<name> <prompt>|change agent=<name> \"<task>\" [--dry-run]|list)",
        _ => verb,
    }));

    /// <summary>Space-separated verb list embedded in every completion script.</summary>
    internal static string CompletionWordList() => string.Join(" ", All);
}
