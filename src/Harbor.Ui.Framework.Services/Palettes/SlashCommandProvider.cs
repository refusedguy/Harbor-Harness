using Harbor.Ui.Framework.Commands;

namespace Harbor.Ui.Framework.Overlays;

// Steal/textual T4 (#1186): the slash catalog as a provider. The vocabulary
// stays owned by SlashCommandCatalog (#462) — this type only projects it to
// hits, so the palette can never drift from the dispatcher again.

/// <summary>
/// Slash-command provider (<c>ProviderId "slash"</c>, <see cref="Order" /> 0).
/// Pure projection of <see cref="SlashCommandCatalog" />: same registration
/// order for discovery, same alias-aware match text for scoring.
/// </summary>
public sealed class SlashCommandProvider : ICommandProvider
{
    /// <inheritdoc />
    public string ProviderId => "slash";

    /// <inheritdoc />
    public int Order { get; }

    /// <summary>Create the slash provider (first in discovery order by default).</summary>
    /// <param name="order">Empty-query discovery sequence (lower first).</param>
    public SlashCommandProvider(int order = 0)
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
            var discovery = new List<CommandHit>(SlashCommandCatalog.All.Count);
            foreach (SlashCommandDefinition def in SlashCommandCatalog.All)
            {
                discovery.Add(CommandHit.Discovery(ProviderId, def.Name, def.Invocation, Describe(def), def.Group));
            }

            return CommandHits.From(discovery);
        }

        string q = query.Trim();
        var scored = new List<CommandHit>(SlashCommandCatalog.All.Count);
        foreach (SlashCommandDefinition def in SlashCommandCatalog.All)
        {
            int score = CommandMatcher.Score(MatchText(def), q);
            if (score >= 0)
            {
                scored.Add(CommandHit.Scored(ProviderId, def.Name, def.Invocation, Describe(def), def.Group, score));
            }
        }

        scored.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : string.Compare(a.Title, b.Title, StringComparison.Ordinal);
        });
        return CommandHits.From(scored);
    }

    private static string Describe(SlashCommandDefinition def)
        => def.Aliases.Count == 0
            ? def.Description
            : $"{def.Description} (alias: {string.Join(", ", def.Aliases)})";

    private static string MatchText(SlashCommandDefinition def)
        => def.Aliases.Count == 0
            ? $"{def.Invocation} {def.Description}"
            : $"{def.Invocation} {def.Description} {string.Join(" ", def.Aliases)}";
}
