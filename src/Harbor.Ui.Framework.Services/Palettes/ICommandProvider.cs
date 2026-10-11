namespace Harbor.Ui.Framework.Overlays;

// Steal/textual T4 (#1186): Provider ABC from textual's command.py —
// search(query) streams Hits, startup() warms the index, matcher() scores.
// System commands are just another provider, and N providers are aggregated
// in parallel by CommandProviderRegistry.

/// <summary>
/// One palette source. The registry fans <see cref="SearchAsync" /> out to
/// every provider in parallel and merges the streams; the host warms indexes
/// once via <see cref="StartupAsync" /> (fired in the background at app start).
/// </summary>
public interface ICommandProvider
{
    /// <summary>Stable provider id, carried on every <see cref="CommandHit" />.</summary>
    string ProviderId { get; }

    /// <summary>
    /// Empty-query discovery sequence (lower first). Inside one provider the
    /// seed order is kept; across providers this breaks ties.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Build the search index (file list, session snapshot, …). In-memory
    /// providers return a completed task. The host fires this in the
    /// background so the first palette open never blocks on I/O.
    /// </summary>
    Task StartupAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stream matching hits. A null/empty <paramref name="query" /> yields
    /// discovery hits (score 0, seed order); otherwise scored hits
    /// (best first), already filtered to matches only.
    /// </summary>
    IAsyncEnumerable<CommandHit> SearchAsync(string? query, CancellationToken cancellationToken);
}

/// <summary>
/// The shared matcher every provider scores with (textual's
/// <c>Provider.matcher()</c> seam). Today it delegates to
/// <see cref="PanelFuzzy" /> — substring wins, shorter rows first, otherwise
/// tightest subsequence, <c>-1</c> for no match — so all providers rank
/// identically; a future matcher lands here without touching providers.
/// </summary>
public static class CommandMatcher
{
    /// <summary>Score <paramref name="text" /> against <paramref name="query" /> (higher is better, -1 filters out).</summary>
    public static int Score(string? text, string? query) => PanelFuzzy.Score(text, query);
}
