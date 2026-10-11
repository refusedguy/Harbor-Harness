using Harbor.Ui.Framework.Navigation;

namespace Harbor.Ui.Framework.Overlays;

// Steal/textual T4 (#1186): system commands are just another provider
// (textual's system_commands.py). This type owns the 10 system rows — the
// single source CellForge's CommandPaletteCatalog projects from — so the
// builtin vocabulary cannot drift between the palette and the provider.

/// <summary>One system-command row (title/subtitle/icon for renderers, id for hosts).</summary>
/// <param name="Id">Commit id (overlay id or action slug).</param>
/// <param name="Title">Display title (without any icon prefix).</param>
/// <param name="Subtitle">One-line description.</param>
/// <param name="IconKey">Opaque renderer hint (e.g. <c>FolderIcon</c>); renderers map it to glyphs.</param>
/// <param name="Group">Palette section heading.</param>
public sealed record SystemCommandDef(string Id, string Title, string Subtitle, string IconKey, string Group);

/// <summary>
/// System-command provider (<c>ProviderId "system"</c>, <see cref="Order" /> 1).
/// In-memory projection of <see cref="Defaults" />; <see cref="StartupAsync" />
/// is a no-op.
/// </summary>
public sealed class SystemCommandProvider : ICommandProvider
{
    /// <summary>Palette section for every system row.</summary>
    public const string GroupCommands = "Commands";

    /// <summary>
    /// The 10 system rows. Ids reuse <see cref="OverlayIds" /> constants where
    /// the row opens an overlay, matching the pinned CellForge catalog strings.
    /// </summary>
    public static readonly IReadOnlyList<SystemCommandDef> Defaults =
    [
        new(OverlayIds.SessionsFlyout, "Open Session", "Open an existing chat session", "FolderIcon", GroupCommands),
        new("new-session", "New Session", "Start a fresh chat session", "PlusIcon", GroupCommands),
        new("branch-session", "Branch Session", "Branch the current session at the selected message", "BranchIcon", GroupCommands),
        new("toggle-theme", "Toggle Theme", "Switch between dark and light", "ThemeIcon", GroupCommands),
        new("open-code-editor", "Open Code Editor", "Open the built-in code editor", "CodeIcon", GroupCommands),
        new(OverlayIds.Diff, "Open Diff View", "Open the diff viewer", "DiffIcon", GroupCommands),
        new(OverlayIds.TokenUsage, "Open Token Usage", "Show per-session token usage and cost", "ChartIcon", GroupCommands),
        new(OverlayIds.Settings, "Open Settings", "Configure providers, theme, fonts", "SettingsIcon", GroupCommands),
        new(OverlayIds.ProviderBrowser, "Open Provider Browser", "Browse and configure LLM providers", "ProviderIcon", GroupCommands),
        new("quit", "Quit", "Exit Harbor", "QuitIcon", GroupCommands),
    ];

    /// <inheritdoc />
    public string ProviderId => "system";

    /// <inheritdoc />
    public int Order { get; }

    /// <summary>Create the system provider (second in discovery order by default).</summary>
    /// <param name="order">Empty-query discovery sequence (lower first).</param>
    public SystemCommandProvider(int order = 1)
    {
        Order = order;
    }

    /// <inheritdoc />
    public Task StartupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<CommandHit> SearchAsync(string? query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query))
        {
            var discovery = new List<CommandHit>(Defaults.Count);
            for (int i = 0; i < Defaults.Count; i++)
            {
                var def = Defaults[i];
                discovery.Add(CommandHit.Discovery(ProviderId, def.Id, def.Title, def.Subtitle, def.Group));
            }

            return CommandHits.From(discovery);
        }

        string q = query.Trim();
        var scored = new List<CommandHit>(Defaults.Count);
        for (int i = 0; i < Defaults.Count; i++)
        {
            var def = Defaults[i];
            int score = CommandMatcher.Score($"{def.Title} {def.Subtitle}", q);
            if (score >= 0)
            {
                scored.Add(CommandHit.Scored(ProviderId, def.Id, def.Title, def.Subtitle, def.Group, score));
            }
        }

        scored.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : string.Compare(a.Title, b.Title, StringComparison.Ordinal);
        });
        return CommandHits.From(scored);
    }
}
