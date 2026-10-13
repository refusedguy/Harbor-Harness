using System.Globalization;
using ConsoleAppFramework;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     CAF-E slice 2 (#1290): <c>ask</c> + <c>events</c> + <c>headless</c> +
///     <c>ide</c> + <c>plugin</c> + <c>skill</c> + <c>help</c> verbs on
///     ConsoleAppFramework. Handler methods only — the ONE <c>RunAsync</c>
///     call site stays in <see cref="CafPilotVerbs" /> (single RunAsync per
///     assembly: the generator keys its emitted file without a per-site
///     disambiguator, so two builder sites collide with CS8785 — see the
///     pilot remarks). Handlers delegate to the existing runners, so output
///     stays byte-identical; <c>-h|--help</c> comes from the framework.
/// </summary>
/// <remarks>
///     <para>
///         Flag shape follows what each verb IS: <c>events</c> types
///         <c>--watch</c>/<c>--count</c> (a raw tail would fail the primary
///         <c>--watch</c> path at the parser — same reason <c>logs</c> types
///         its flags in CAF-C); <c>plugin</c>/<c>skill</c> type
///         <c>--project</c>/<c>--force</c> and re-append them after the
///         positional tail (the manual parsers are order-insensitive
///         <c>Contains</c> checks, and a lone flag with no subcommand rides
///         through as the bad subcommand, so the error path stays identical
///         too); <c>ask</c> takes the global <c>--script</c> value
///         <c>Program</c> strips before dispatch as a typed option and
///         forwards it (scripting is unsupported in the main build, so the
///         only observable is the startup-script warning);
///         <c>headless</c>/<c>ide</c>/<c>help</c> keep the raw tail.
///     </para>
///     <para>
///         Known deferral (same family as CAF-A/B/D): tokens the framework
///         cannot bind fail closed at the parser (exit 1) instead of reaching
///         the runner — an unknown <c>--flag</c> on any of these verbs, or a
///         dash-led <c>ask</c> prompt, no longer prints the manual error text
///         (exit 2). Bare + error-usage + unknown-positional paths are
///         byte-identical (see <c>CafMiscTests</c>). <c>headless</c>,
///         <c>ide</c>, a valid <c>ask</c> prompt and <c>events --watch</c>
///         build the host (block on the agent host or the daemon), so they
///         have no host-free parity path — only the framework <c>--help</c>
///         check (same as <c>serve</c> in CAF-D). The old dispatcher is gone:
///         <c>Program</c> keeps only the no-args/unknown → interactive
///         fallback, the <c>--demo</c>/<c>--headless</c>/<c>--providers</c>
///         aliases and the daemon build guard (see #1290).
///     </para>
/// </remarks>
internal static class CafAskCommands
{
    /// <summary>Ask the agent a one-shot question.</summary>
    /// <param name="script">Startup script path (re-attached by Program from the global --script).</param>
    /// <param name="args">Prompt words (joined with spaces, as the manual verb does).</param>
    public static async Task<int> Ask(
        string? script = null,
        [Argument]params string[] args) =>
        await AskVerb.RunAsync(NullLogger.Instance, args, script).ConfigureAwait(false);
}

/// <summary>
///     CAF-E slice 2 (#1290): <c>events</c> verb handler (daemon event
///     stream). See the remarks on <see cref="CafAskCommands" /> for the
///     single-builder rule and the <c>--flag</c> deferral.
/// </summary>
internal static class CafEventsCommands
{
    /// <summary>Inspect the daemon's event stream.</summary>
    /// <param name="watch">-w, Connect and print events as JSON lines.</param>
    /// <param name="count">-n, Stop after N events (default: endless).</param>
    /// <param name="args">Positional tail (unknown subcommands).</param>
    public static async Task<int> Events(
        bool watch = false,
        int? count = null,
        [Argument]params string[] args)
    {
        string[] tail = watch ? ["--watch", ..args] : args;
        tail = count is not null ? ["--count", count.Value.ToString(CultureInfo.InvariantCulture), ..tail] : tail;
        return await EventsVerb.RunAsync(tail).ConfigureAwait(false);
    }
}

/// <summary>
///     CAF-E slice 2 (#1290): <c>headless</c> verb handler (headless daemon
///     mode). See the remarks on <see cref="CafAskCommands" /> for the
///     single-builder rule and the <c>--flag</c> deferral.
/// </summary>
internal static class CafHeadlessCommands
{
    /// <summary>Run the Harbor daemon headless (no UI, IPC server, block until SIGINT/SIGTERM).</summary>
    public static async Task<int> Headless([Argument]params string[] args) =>
        await HeadlessVerb.RunAsync(NullLogger.Instance, args).ConfigureAwait(false);
}

#if HARBOR_WITH_DAEMON
/// <summary>
///     CAF-E slice 2 (#1290): <c>ide</c> verb handler (editor stdio bridge).
///     See the remarks on <see cref="CafAskCommands" /> for the single-builder
///     rule and the <c>--flag</c> deferral. Daemon-gated like the manual
///     registration in <c>Program</c> (Slice A, #1144): the runner types leave
///     the compile when the daemon is off, so this handler does too.
/// </summary>
internal static class CafIdeCommands
{
    /// <summary>Run the NDJSON JSON-RPC stdio bridge for external editors.</summary>
    public static async Task<int> Ide([Argument]params string[] args) =>
        await IdeVerb.RunAsync(NullLogger.Instance, args).ConfigureAwait(false);
}
#endif

/// <summary>
///     CAF-E slice 2 (#1290): <c>plugin</c> verb handler (CS-source plugin
///     management). See the remarks on <see cref="CafAskCommands" /> for the
///     single-builder rule and the flag re-append.
/// </summary>
internal static class CafPluginCommands
{
    /// <summary>Manage CS-source plugins (list, install, uninstall).</summary>
    /// <param name="project">Target the project-local scope instead of the global one.</param>
    /// <param name="force">Overwrite an existing plugin on install.</param>
    /// <param name="args">Positional tail (subcommand and file/name).</param>
    public static async Task<int> Plugin(
        bool project = false,
        bool force = false,
        [Argument]params string[] args)
    {
        string[] flagTail = project
            ? ["--project", ..(force ? ["--force"] : [])]
            : force ? ["--force"] : [];
        return await new PluginsCommand(Console.Out, Console.Error).ExecuteAsync([..args, ..flagTail]).ConfigureAwait(false);
    }
}

/// <summary>
///     CAF-E slice 2 (#1290): <c>skill</c> verb handler (SKILL.md skill
///     management). See the remarks on <see cref="CafAskCommands" /> for the
///     single-builder rule and the flag re-append.
/// </summary>
internal static class CafSkillCommands
{
    /// <summary>Manage SKILL.md skills (list, install, uninstall).</summary>
    /// <param name="project">Target the project-local scope instead of the global one.</param>
    /// <param name="force">Overwrite an existing skill on install.</param>
    /// <param name="args">Positional tail (subcommand and file/name).</param>
    public static async Task<int> Skill(
        bool project = false,
        bool force = false,
        [Argument]params string[] args)
    {
        string[] flagTail = project
            ? ["--project", ..(force ? ["--force"] : [])]
            : force ? ["--force"] : [];
        return await new SkillsCommand(Console.Out, Console.Error).ExecuteAsync([..args, ..flagTail]).ConfigureAwait(false);
    }
}

/// <summary>
///     CAF-E slice 2 (#1290): <c>help</c> verb handler. See the remarks on
///     <see cref="CafAskCommands" /> for the single-builder rule.
/// </summary>
internal static class CafHelpCommands
{
    /// <summary>Print Harbor help.</summary>
    public static int Help([Argument]params string[] args) => HelpVerbs.PrintHelp();
}
