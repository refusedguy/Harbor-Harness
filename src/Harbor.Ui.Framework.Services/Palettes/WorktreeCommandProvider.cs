namespace Harbor.Ui.Framework.Overlays;

// Steal/textual T4 (#1186): the worktree-jump source as a provider. Seed rows
// come from WorktreeJumpSeeder (sessions merged with git worktrees); this
// type only projects them to hits, mirroring WorktreeJumpPaletteModel's
// best-of-title/path/branch scoring.

/// <summary>
/// Worktree-jump provider (<c>ProviderId "worktree"</c>, <see cref="Order" /> 3).
/// Host pushes <see cref="WorktreeJumpEntry" /> rows via
/// <see cref="SetEntries" />; empty query keeps seed order, otherwise rows
/// rank by best title/path/branch score.
/// </summary>
/// <remarks>
/// The hit <c>Id</c> is the session id. Rows with an empty id have no session
/// to switch to (see <see cref="WorktreeJumpSeeder" />) — Enter on those only
/// closes the palette.
/// </remarks>
public sealed class WorktreeCommandProvider : ICommandProvider
{
    /// <summary>Palette section for worktree rows.</summary>
    public const string GroupWorktrees = "Worktrees";

    private IReadOnlyList<WorktreeJumpEntry> _entries = [];

    /// <inheritdoc />
    public string ProviderId => "worktree";

    /// <inheritdoc />
    public int Order { get; }

    /// <summary>Create the worktree provider over an optional seed.</summary>
    /// <param name="order">Empty-query discovery sequence (lower first).</param>
    /// <param name="entries">Initial rows.</param>
    public WorktreeCommandProvider(int order = 3, IReadOnlyList<WorktreeJumpEntry>? entries = null)
    {
        Order = order;
        if (entries is not null)
        {
            SetEntries(entries);
        }
    }

    /// <summary>Replace the seed rows (kept in incoming order).</summary>
    public void SetEntries(IReadOnlyList<WorktreeJumpEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries;
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
            var discovery = new List<CommandHit>(_entries.Count);
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                discovery.Add(CommandHit.Discovery(ProviderId, entry.SessionId, entry.Title, DetailOf(entry), GroupWorktrees));
            }

            return CommandHits.From(discovery);
        }

        string q = query.Trim();
        var scored = new List<CommandHit>(_entries.Count);
        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            int score = Math.Max(
                CommandMatcher.Score(entry.Title, q),
                Math.Max(
                    CommandMatcher.Score(entry.Path, q),
                    CommandMatcher.Score(entry.Branch, q)));
            if (score >= 0)
            {
                scored.Add(CommandHit.Scored(ProviderId, entry.SessionId, entry.Title, DetailOf(entry), GroupWorktrees, score));
            }
        }

        scored.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : string.Compare(a.Title, b.Title, StringComparison.Ordinal);
        });
        return CommandHits.From(scored);
    }

    private static string DetailOf(WorktreeJumpEntry entry)
        => $"{entry.BranchText}  {entry.Status}  {entry.Path}";
}
