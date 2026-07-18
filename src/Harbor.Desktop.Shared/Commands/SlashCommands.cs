namespace Harbor.Desktop.Shared.Commands;

/// <summary>
///     Catalog of slash commands (e.g. <c>/help</c>, <c>/clear</c>,
/// <c>/quit</c>) shared by every desktop app. The platform app's chat
/// view-model dispatches the user-typed <c>/</c>-prefixed text via
/// <c>TuiEffectHost.RunSlash</c> — this catalog just documents the
/// canonical command names and descriptions for the help screen.
/// </summary>
public static class SlashCommands
{
    /// <summary>One slash-command entry — name, description, optional aliases.</summary>
    /// <param name="Name">Command name including the leading slash (e.g. <c>/help</c>).</param>
    /// <param name="Description">One-line description shown in the help screen.</param>
    /// <param name="Aliases">Optional aliases (without leading slash).</param>
    public sealed record Entry(string Name, string Description, IReadOnlyList<string> Aliases);

    /// <summary>Canonical list of slash commands.</summary>
    public static readonly IReadOnlyList<Entry> All =
    [
        new Entry("/help", "Show this help screen", Array.Empty<string>()),
        new Entry("/clear", "Clear the current chat transcript", new[] { "cls" }),
        new Entry("/quit", "Exit Harbor", new[] { "exit" }),
        new Entry("/sessions", "List recent sessions", Array.Empty<string>()),
        new Entry("/branch", "Branch the current session at the last assistant message", Array.Empty<string>()),
        new Entry("/providers", "List configured providers", Array.Empty<string>()),
        new Entry("/tokens", "Show token usage for the current session", Array.Empty<string>()),
        new Entry("/theme", "Toggle between dark and light theme", Array.Empty<string>()),
        new Entry("/editor", "Open the code editor", Array.Empty<string>()),
        new Entry("/diff", "Open the diff viewer", Array.Empty<string>()),
    ];

    /// <summary>Look up an entry by name (with or without leading slash) or alias.</summary>
    /// <param name="command">User-typed command (e.g. <c>/help</c> or <c>help</c> or <c>cls</c>).</param>
    /// <returns>The matching <see cref="Entry"/>, or null if not found.</returns>
    public static Entry? Find(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var trimmed = command.TrimStart('/');
        return All.FirstOrDefault(e => MatchesEntry(e, trimmed));
    }

    private static bool MatchesEntry(Entry entry, string trimmed)
    {
        if (entry.Name.TrimStart('/').Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            return true;
        return entry.Aliases.Any(alias => alias.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
