using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Ui.Framework.Overlays;

// Steal/textual T4 (#1186): the files source as a provider with a background-
// warmed index. The provider never touches the filesystem itself (Presentation
// must not fork or walk — #666, #669): the host supplies paths (push via
// SetFiles) or an enumerator (pulled once by StartupAsync), and StartupAsync
// only sorts the snapshot into the search index.

/// <summary>
/// File provider (<c>ProviderId "files"</c>, <see cref="Order" /> 2).
/// Host-owned paths, provider-owned sorted index; the host fires
/// <see cref="StartupAsync" /> in the background so the first palette open
/// never blocks on enumeration.
/// </summary>
public sealed class FileCommandProvider : ICommandProvider
{
    /// <summary>Palette section for file rows.</summary>
    public const string GroupFiles = "Files";

    /// <summary>Empty-query discovery cap (a full repo never fits the list).</summary>
    public const int MaxDiscovery = 50;

    /// <summary>Scored-query result cap (top hits after ranking).</summary>
    public const int MaxResults = 50;

    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>>? _enumerate;
    private List<string> _index = new();

    /// <inheritdoc />
    public string ProviderId => "files";

    /// <inheritdoc />
    public int Order { get; }

    /// <summary>Create the file provider over an optional seed list.</summary>
    /// <param name="order">Empty-query discovery sequence (lower first).</param>
    /// <param name="enumerate">Host-supplied enumeration, pulled once by <see cref="StartupAsync" />.</param>
    /// <param name="seed">Initial paths (kept sorted); replaced by <see cref="StartupAsync" /> when <paramref name="enumerate" /> is set.</param>
    public FileCommandProvider(
        int order = 2,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? enumerate = null,
        IEnumerable<string>? seed = null)
    {
        Order = order;
        _enumerate = enumerate;
        if (seed is not null)
        {
            SetFiles(seed);
        }
    }

    /// <summary>Replace the index (kept ordinal-sorted).</summary>
    public void SetFiles(IEnumerable<string> paths)
    {
        Ensure.NotNull(paths);
        var list = new List<string>();
        foreach (string path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                list.Add(path);
            }
        }

        list.Sort(StringComparer.Ordinal);
        _index = list;
    }

    /// <summary>Current index size (0 before warmup with no seed).</summary>
    public int IndexedCount => _index.Count;

    /// <inheritdoc />
    public async Task StartupAsync(CancellationToken cancellationToken)
    {
        if (_enumerate is null)
        {
            return;
        }

        IReadOnlyList<string> paths = await _enumerate(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        SetFiles(paths);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<CommandHit> SearchAsync(string? query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query))
        {
            int take = Math.Min(_index.Count, MaxDiscovery);
            var discovery = new List<CommandHit>(take);
            for (int i = 0; i < take; i++)
            {
                string path = _index[i];
                discovery.Add(CommandHit.Discovery(ProviderId, path, Path.GetFileName(path) ?? path, path, GroupFiles));
            }

            return CommandHits.From(discovery);
        }

        string q = query.Trim();
        var scored = new List<CommandHit>();
        for (int i = 0; i < _index.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = _index[i];
            string name = Path.GetFileName(path) ?? path;
            int score = Math.Max(
                CommandMatcher.Score(name, q),
                CommandMatcher.Score(path, q));
            if (score >= 0)
            {
                scored.Add(CommandHit.Scored(ProviderId, path, name, path, GroupFiles, score));
            }
        }

        scored.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : string.Compare(a.Detail, b.Detail, StringComparison.Ordinal);
        });
        if (scored.Count > MaxResults)
        {
            scored.RemoveRange(MaxResults, scored.Count - MaxResults);
        }

        return CommandHits.From(scored);
    }
}
