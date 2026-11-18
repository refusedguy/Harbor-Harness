namespace Harbor.Ui.Framework.Commands;

/// <summary>
///     Where a slash command comes from (#2686 Q06). A command can be declared by
///     Harbor itself, contributed by a provider, or surfaced from a skill. The
///     value travels on <see cref="SlashCommandDefinition" /> so every surface that
///     advertises commands (help screens, palettes, autocomplete) can say where a
///     command came from instead of showing one flat, source-less list.
/// </summary>
/// <remarks>
///     Builtins default to <see cref="Builtin" />; a provider or skill that adds a
///     command says so by passing its own value. Keeping provenance on the shared
///     definition — rather than in one surface's private list — is what makes the
///     registry a single source of truth for commands <em>and</em> their origin.
/// </remarks>
public enum CommandSource
{
    /// <summary>The command ships with Harbor.</summary>
    Builtin,

    /// <summary>The command is contributed by the active provider.</summary>
    Provider,

    /// <summary>The command is surfaced from an installed skill.</summary>
    Skill
}

/// <summary>Display helpers for <see cref="CommandSource" />.</summary>
public static class CommandSourceLabels
{
    /// <summary>The lowercase label shown next to a command in help output.</summary>
    public static string Label(this CommandSource source) => source switch
    {
        CommandSource.Provider => "provider",
        CommandSource.Skill => "skill",
        _ => "builtin"
    };
}
