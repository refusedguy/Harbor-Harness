using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Ui.Framework.Overlays;

// Steal/textual T4 (#1186): the N-provider aggregation from textual's
// command.py. Providers are fanned out in parallel; the empty query keeps
// provider order (DiscoveryHit UX), a non-empty query ranks by score.

/// <summary>
/// Merges <see cref="ICommandProvider" /> streams into one ranked hit list.
/// Owns the provider set, the background index warmup, and the Hit vs
/// DiscoveryHit ordering split. Pure BCL, zero rendering — any host
/// (CellForge <c>CommandPaletteView</c>, desktop flyout) drives it.
/// </summary>
public sealed class CommandProviderRegistry
{
    private readonly List<ICommandProvider> _providers = new();

    /// <summary>Create a registry over optional initial providers (kept in order).</summary>
    public CommandProviderRegistry(IEnumerable<ICommandProvider>? providers = null)
    {
        if (providers is not null)
        {
            _providers.AddRange(providers);
        }
    }

    /// <summary>Default set: slash, system, files, worktree (discovery order).</summary>
    public static CommandProviderRegistry CreateDefault()
        => new([new SlashCommandProvider(), new SystemCommandProvider(), new FileCommandProvider(), new WorktreeCommandProvider()]);

    /// <summary>Registered providers in insertion order.</summary>
    public IReadOnlyList<ICommandProvider> Providers => _providers;

    /// <summary>Add a provider (insertion order is the merge order; <see cref="ICommandProvider.Order" /> decides discovery rank).</summary>
    public void Add(ICommandProvider provider)
    {
        Ensure.NotNull(provider);
        _providers.Add(provider);
    }

    /// <summary>
    /// Warm every provider index (file list, session snapshot, …). The host
    /// fires this once in the background at startup — <c>_ = Task.Run(() =>
    /// registry.WarmupAsync(ct))</c> — so the first palette open never blocks.
    /// One slow provider delays the task, never the UI thread.
    /// </summary>
    public Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ICommandProvider[] snapshot = [.. _providers];
        return Task.WhenAll(snapshot.Select(p => p.StartupAsync(cancellationToken)));
    }

    /// <summary>
    /// Search every provider in parallel and merge. Empty query: discovery
    /// hits in provider <see cref="ICommandProvider.Order" /> then seed order.
    /// Non-empty: scored hits best-first (score, provider order, title).
    /// A failing provider contributes nothing; cancellation still propagates.
    /// </summary>
    public async Task<IReadOnlyList<CommandHit>> SearchAsync(string? query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ICommandProvider[] snapshot = [.. _providers];
        string q = query ?? string.Empty;

        List<CommandHit>[] perProvider = await Task.WhenAll(
            snapshot.Select(p => CollectAsync(p, q, cancellationToken))).ConfigureAwait(false);

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < snapshot.Length; i++)
        {
            rank[snapshot[i].ProviderId] = snapshot[i].Order;
        }

        static int RankOf(Dictionary<string, int> rank, CommandHit hit)
            => rank.TryGetValue(hit.ProviderId, out int r) ? r : 0;

        var merged = new List<CommandHit>();
        for (int i = 0; i < perProvider.Length; i++)
        {
            merged.AddRange(perProvider[i]);
        }

        // OrderBy is stable: equal keys keep merge order, which already
        // follows snapshot order and, inside one provider, seed order.
        if (q.Trim().Length == 0)
        {
            return merged.OrderBy(h => RankOf(rank, h)).ToList();
        }

        return merged
            .OrderByDescending(h => h.Score)
            .ThenBy(h => RankOf(rank, h))
            .ThenBy(h => h.Title, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<List<CommandHit>> CollectAsync(ICommandProvider provider, string query, CancellationToken cancellationToken)
    {
        try
        {
            var hits = new List<CommandHit>();
            await foreach (CommandHit hit in provider.SearchAsync(query, cancellationToken).ConfigureAwait(false))
            {
                hits.Add(hit);
            }

            return hits;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }
}
