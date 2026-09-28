using Harbor.Ui.Framework.Commands;

namespace Harbor.Desktop.Shared.Commands;
/// <summary>
///     Catalog of slash commands (<c>/help</c>, <c>/exit</c>, …) shared by every
///     desktop app. The platform app's chat view-model dispatches the
///     user-typed <c>/</c>-prefixed text via <c>TuiEffectHost.RunSlash</c> —
///     this catalog documents the canonical command names, descriptions and
///     aliases for the help screen.
/// </summary>
/// <remarks>
///     The entries are projected from <see cref="SlashCommandCatalog" />, the
///     single registry the CLI slash dispatcher binds handlers to (issue
///     #462). This file used to own a private 10-entry copy that had drifted
///     away from what the dispatcher runs, so desktop apps advertised
///     <c>/tokens</c>, <c>/theme</c> and <c>/editor</c> — commands that do not
///     exist — while missing <c>/permissions</c>, <c>/plugins</c> and
///     <c>/skills</c>. Deriving instead of copying makes that class of bug
///     unrepresentable.
/// </remarks>
public static class SlashCommands
{

    /// <summary>Canonical list of slash commands, projected from the shared catalog.</summary>
    public static readonly IReadOnlyList<Entry> All = BuildAll();

    /// <summary>Look up an entry by name (with or without leading slash) or alias.</summary>
    /// <param name="command">User-typed command (e.g. <c>/help</c> or <c>help</c> or <c>quit</c>).</param>
    /// <returns>The matching <see cref="Entry" />, or null if not found.</returns>
    public static Entry? Find(string command)
    {
        SlashCommandDefinition? def = SlashCommandCatalog.Find(command);
        return def is null ? null : new Entry(def.Invocation, def.Description, def.Aliases);
    }

    private static IReadOnlyList<Entry> BuildAll()
    {
        var list = new List<Entry>(SlashCommandCatalog.All.Count);
        foreach (SlashCommandDefinition def in SlashCommandCatalog.All)
        {
            list.Add(new Entry(def.Invocation, def.Description, def.Aliases));
        }

        return list;
    }

    /// <summary>One slash-command entry — name, description, optional aliases.</summary>
    /// <param name="Name">Command name including the leading slash (e.g. <c>/help</c>).</param>
    /// <param name="Description">One-line description shown in the help screen.</param>
    /// <param name="Aliases">Optional aliases (without leading slash).</param>
    public sealed record Entry(string Name, string Description, IReadOnlyList<string> Aliases);
}
