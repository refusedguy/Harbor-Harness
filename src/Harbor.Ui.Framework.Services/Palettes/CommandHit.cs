namespace Harbor.Ui.Framework.Overlays;

// Steal/textual T4 (#1186): the palette no longer owns per-source catalogs.
// Every source is an ICommandProvider streaming CommandHit rows; the palette
// only merges and paints. Hit vs DiscoveryHit follows textual's command.py:
// a scored Hit for non-empty queries, a score-0 DiscoveryHit in provider
// order for the empty query.

/// <summary>
/// One palette row from an <see cref="ICommandProvider" />.
/// Scored hits (<see cref="IsDiscovery" /> false) carry a
/// <see cref="PanelFuzzy" /> score (higher is better); discovery hits
/// (<see cref="IsDiscovery" /> true) always score 0 and keep provider seed
/// order when the query is empty.
/// </summary>
/// <param name="ProviderId">Owning provider (e.g. <c>"slash"</c>, <c>"system"</c>).</param>
/// <param name="Id">Stable row id passed back on commit.</param>
/// <param name="Title">Primary display text, matched against the query.</param>
/// <param name="Detail">Secondary display text (description, path, status).</param>
/// <param name="Group">Section heading; empty renders without a header.</param>
/// <param name="Score">Match score (higher is better); 0 for discovery hits.</param>
/// <param name="IsDiscovery">True for empty-query provider-ordered rows.</param>
public sealed record CommandHit(
    string ProviderId,
    string Id,
    string Title,
    string Detail = "",
    string Group = "",
    int Score = 0,
    bool IsDiscovery = false)
{
    /// <summary>Score-0 provider-ordered row for the empty query.</summary>
    public static CommandHit Discovery(string providerId, string id, string title, string detail = "", string group = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(title);
        return new CommandHit(providerId, id, title, detail, group, 0, true);
    }

    /// <summary>Scored row for a non-empty query.</summary>
    public static CommandHit Scored(string providerId, string id, string title, string detail, string group, int score)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(title);
        return new CommandHit(providerId, id, title, detail, group, score, false);
    }
}

/// <summary>
/// Adapts an in-memory hit list to the provider stream shape. In-memory
/// providers (slash, system) return this; slow sources (files, worktree)
/// yield incrementally instead of snapshotting.
/// </summary>
public static class CommandHits
{
    /// <summary>Wrap <paramref name="hits" /> as a single-use async stream.</summary>
    public static IAsyncEnumerable<CommandHit> From(IReadOnlyList<CommandHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        return new Snapshot(hits);
    }

    private sealed class Snapshot(IReadOnlyList<CommandHit> hits) : IAsyncEnumerable<CommandHit>
    {
        public IAsyncEnumerator<CommandHit> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new Cursor(hits, cancellationToken);
    }

    private sealed class Cursor(IReadOnlyList<CommandHit> hits, CancellationToken cancellationToken) : IAsyncEnumerator<CommandHit>
    {
        private int _index = -1;

        public CommandHit Current => hits[_index];

        public ValueTask<bool> MoveNextAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            _index++;
            return new ValueTask<bool>(_index < hits.Count);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
