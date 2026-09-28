using System.Collections.Frozen;

namespace Harbor.Ui.Framework.Commands;

/// <summary>
///     One entry of the canonical slash-command registry (issue #462).
/// </summary>
/// <param name="Name">Canonical name without the leading <c>/</c> (e.g. <c>help</c>).</param>
/// <param name="Description">One-line description shown in help screens and palettes.</param>
/// <param name="Aliases">Alternative names, without the leading <c>/</c>.</param>
/// <param name="ArgSuggestions">
///     Second-step argument suggestions for a palette picker (e.g. <c>refresh</c> for
///     <c>/skills</c>). <see langword="null" /> when the command takes no arguments.
/// </param>
/// <param name="Group">Palette group heading (GoF Command grouping).</param>
/// <param name="QuitsLoop">
///     <see langword="true" /> when the command ends the interactive loop instead of
///     running a handler. <c>/exit</c> and <c>/quit</c> are handled by the REPL runner
///     before any handler lookup, so they carry no execute delegate.
/// </param>
public sealed record SlashCommandDefinition(
    string Name,
    string Description,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string>? ArgSuggestions = null,
    string Group = SlashCommandCatalog.GroupOther,
    bool QuitsLoop = false)
{
    /// <summary>The command as the user types it, including the leading <c>/</c>.</summary>
    public string Invocation => "/" + Name;
}

/// <summary>
///     The single source of truth for the slash-command vocabulary (issue #462).
/// </summary>
/// <remarks>
///     <para>
///         Before this type every surface owned a private copy of the command list —
///         <c>ChatCommands.Slash</c> (reducer autocomplete), <c>CommandPaletteCatalog</c>
///         (CellForge palette), <c>SlashCommands.All</c> (desktop), the Avalonia
///         <c>CommandPaletteViewModel</c> and the Fullscreen renderer's hint line. The
///         copies drifted: the palette advertised <c>/tokens</c>, <c>/diff</c>,
///         <c>/editor</c>, <c>/theme</c> and <c>/branch</c>, none of which the CLI
///         dispatcher can execute, while omitting <c>/new</c>, <c>/permissions</c>,
///         <c>/plugins</c>, <c>/skills</c>, <c>/tree</c>, <c>/fork</c> and
///         <c>/renderer</c>, which it can.
///     </para>
///     <para>
///         The registry lives here — the lowest UI-framework contract assembly, reachable
///         by every consumer and by the CLI composition root — so the lists cannot drift
///         again. <c>SlashCommandDispatcher</c> binds handlers to these entries and fails
///         fast at construction when the two disagree, so a new command must be declared
///         here exactly once.
///     </para>
/// </remarks>
public static class SlashCommandCatalog
{
    /// <summary>Palette group for shell-level commands (help, exit).</summary>
    public const string GroupGeneral = "General";

    /// <summary>Palette group for configuration commands (setup, auth, config, permissions).</summary>
    public const string GroupConfig = "Config";

    /// <summary>Palette group for session commands (new, sessions, tree, fork).</summary>
    public const string GroupSessions = "Sessions";

    /// <summary>Palette group for runtime commands (model, agent, tui, renderer, …).</summary>
    public const string GroupRuntime = "Runtime";

    /// <summary>Fallback palette group.</summary>
    public const string GroupOther = "Other";

    private static readonly IReadOnlyList<SlashCommandDefinition> Definitions =
    [
        new("help", "Show this help screen", ["h"], Group: GroupGeneral),
        new("exit", "Exit Harbor", ["quit"], Group: GroupGeneral, QuitsLoop: true),

        new("new", "Start a new session", ["new-session"], Group: GroupSessions),
        new("sessions", "List recent sessions", [], Group: GroupSessions),
        new("tree", "Show the session fork tree", [], Group: GroupSessions),
        new("fork", "Fork a session at a message", [], Group: GroupSessions),

        new("setup", "Run the setup wizard", [], Group: GroupConfig),
        new("auth", "Manage provider API keys", ["key", "api-key"], Group: GroupConfig),
        new("config", "Show the active configuration", [], Group: GroupConfig),
        new("permissions", "Inspect or edit permission rules", [], Group: GroupConfig),

        new("model", "Switch the active LLM model", ["m"], Group: GroupRuntime),
        new("agent", "Switch the active agent", ["mode", "a"], Group: GroupRuntime),
        new("providers", "List configured providers", [], Group: GroupRuntime),
        new("plugins", "Reload plugins from disk", [], Group: GroupRuntime),
        new("tui", "List available TUI backends", ["ansi", "plain", "spectre", "consoleex", "notifications"], Group: GroupRuntime),
        new("storage", "Show the session storage backend", ["jsonl", "memory", "sqlite"], Group: GroupRuntime),
        new("renderer", "Show or switch the renderer backend", [], Group: GroupRuntime),
        new("skills", "Check skill freshness", ["skill"], ["refresh", "update"], Group: GroupRuntime),
    ];

    private static readonly FrozenDictionary<string, SlashCommandDefinition> ByName = BuildLookup();

    /// <summary>Every registered command, in registration order.</summary>
    public static IReadOnlyList<SlashCommandDefinition> All => Definitions;

    /// <summary>
    ///     Every command as the user types it (<c>/help</c>, <c>/exit</c>, …). This is the
    ///     autocomplete/help/palette vocabulary — never a hand-maintained list.
    /// </summary>
    public static IReadOnlyList<string> Invocations { get; } = BuildInvocations();

    /// <summary>Look up a command by name, alias, or user-typed text.</summary>
    /// <param name="command">
    ///     Text with or without a leading <c>/</c> (<c>/help</c>, <c>help</c>, <c>cls</c>).
    /// </param>
    /// <returns>The matching definition, or <see langword="null" />.</returns>
    public static SlashCommandDefinition? Find(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        return ByName.GetValueOrDefault(command.Trim().TrimStart('/'));
    }

    private static FrozenDictionary<string, SlashCommandDefinition> BuildLookup()
    {
        var map = new Dictionary<string, SlashCommandDefinition>(Definitions.Count * 2, StringComparer.OrdinalIgnoreCase);
        foreach (SlashCommandDefinition def in Definitions)
        {
            Add(map, def.Name, def);
            foreach (string alias in def.Aliases)
            {
                Add(map, alias, def);
            }
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Adds one name/alias, rejecting a collision. Two commands sharing an alias
    ///     would silently shadow each other in the dispatcher's lookup, so it is a
    ///     programming error rather than something to resolve at runtime.
    /// </summary>
    private static void Add(Dictionary<string, SlashCommandDefinition> map, string key, SlashCommandDefinition def)
    {
        if (map.TryGetValue(key, out SlashCommandDefinition? existing))
        {
            throw new InvalidOperationException(
                $"Slash command '{key}' is declared by both /{existing.Name} and /{def.Name}.");
        }

        map[key] = def;
    }

    private static IReadOnlyList<string> BuildInvocations()
    {
        var list = new string[Definitions.Count];
        for (int i = 0; i < Definitions.Count; i++)
        {
            list[i] = Definitions[i].Invocation;
        }

        return list;
    }
}
