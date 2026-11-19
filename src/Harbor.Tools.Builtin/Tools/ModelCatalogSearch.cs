// ModelCatalogSearch.cs: #2680 F178. The pure search-and-pagination core of a
// model_search tool. It filters a supplied catalog by model id/name and by an
// optional provider, then returns one bounded page. The helper is pure over its
// inputs: it holds no state, starts no process, reads no environment and
// resolves no provider registry. AOT-safe and golden-neutral.
namespace Harbor.Tools.Builtin;

/// <summary>
///     One model as seen by a model_search tool. Self-contained so the search
///     leaf stays decoupled from any provider registry type.
/// </summary>
/// <param name="Id">Stable model identifier (for example <c>provider/model</c>).</param>
/// <param name="Provider">Owning provider id, or <see langword="null" />.</param>
/// <param name="DisplayName">Human-readable name, or <see langword="null" />.</param>
public readonly record struct ModelSearchEntry(string Id, string? Provider, string? DisplayName);

/// <summary>
///     A validated model_search request. Text and provider are already trimmed
///     and normalised to <see langword="null" /> when absent; limit and offset
///     are already bounded by <see cref="ModelCatalogSearch" />.
/// </summary>
public readonly record struct ModelSearchQuery(string? Text, string? Provider, int Limit, int Offset);

/// <summary>
///     One page of model_search results.
/// </summary>
/// <param name="Items">The models in this page, in catalog order.</param>
/// <param name="TotalMatched">Total number of models matching the query.</param>
/// <param name="Offset">The offset that was applied.</param>
/// <param name="HasMore">Whether models remain after this page.</param>
public sealed record ModelSearchPage(
    IReadOnlyList<ModelSearchEntry> Items,
    int TotalMatched,
    int Offset,
    bool HasMore);

/// <summary>
///     Pure model search by name/ID with bounded limit/offset pagination
///     (#2680 F178). Stateless and thread-safe.
/// </summary>
/// <remarks>
///     <para>
///         The page size is hard-capped at <see cref="MaxLimit" /> (20), matching
///         the smallest-interval/hard-cap behaviour of the source feature. A
///         larger request is clamped rather than rejected; a non-positive limit
///         or a negative offset is rejected by <see cref="TryCreate" />.
///     </para>
///     <para>
///         Matching is case-insensitive over the model id and display name, and
///         an optional provider filter uses case-insensitive equality. Catalog
///         order is preserved so results are stable and golden-friendly.
///     </para>
/// </remarks>
public static class ModelCatalogSearch
{
    /// <summary>Default page size when no limit is supplied.</summary>
    public const int DefaultLimit = 20;

    /// <summary>Hard cap on the number of models returned in one page.</summary>
    public const int MaxLimit = 20;

    /// <summary>Default offset when none is supplied.</summary>
    public const int DefaultOffset = 0;

    /// <summary>
    ///     Validates and normalises the arguments a model_search tool receives.
    /// </summary>
    /// <param name="text">Free-text query over id and display name, or <see langword="null" />.</param>
    /// <param name="provider">Optional provider id filter, or <see langword="null" />.</param>
    /// <param name="limit">Requested page size, or <see langword="null" /> for the default.</param>
    /// <param name="offset">Requested offset, or <see langword="null" /> for zero.</param>
    /// <param name="query">The normalised query on success.</param>
    /// <returns><see langword="true" /> when the arguments are valid.</returns>
    public static bool TryCreate(
        string? text,
        string? provider,
        int? limit,
        int? offset,
        out ModelSearchQuery query)
    {
        query = default;

        int effectiveLimit = limit ?? DefaultLimit;
        int effectiveOffset = offset ?? DefaultOffset;

        if (effectiveLimit < 1 || effectiveOffset < 0) return false;

        if (effectiveLimit > MaxLimit) effectiveLimit = MaxLimit;

        query = new ModelSearchQuery(Normalize(text), Normalize(provider), effectiveLimit, effectiveOffset);
        return true;
    }

    /// <summary>
    ///     Searches the supplied catalog and returns a single bounded page.
    /// </summary>
    /// <param name="catalog">Models to search. May be empty.</param>
    /// <param name="query">A query produced by <see cref="TryCreate" />.</param>
    /// <returns>The matching page.</returns>
    public static ModelSearchPage Search(IReadOnlyList<ModelSearchEntry> catalog, ModelSearchQuery query)
    {
        var matched = new List<ModelSearchEntry>();

        for (int i = 0; i < catalog.Count; i++)
        {
            ModelSearchEntry entry = catalog[i];
            if (!MatchesProvider(entry, query.Provider)) continue;
            if (!MatchesText(entry, query.Text)) continue;
            matched.Add(entry);
        }

        int total = matched.Count;
        int start = Math.Min(query.Offset, total);
        int count = Math.Min(query.Limit, total - start);

        var items = new ModelSearchEntry[count];
        for (int i = 0; i < count; i++) items[i] = matched[start + i];

        return new ModelSearchPage(items, total, query.Offset, start + count < total);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool MatchesText(ModelSearchEntry entry, string? text)
    {
        if (text is null) return true;
        if (entry.Id.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        return entry.DisplayName is not null
               && entry.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesProvider(ModelSearchEntry entry, string? provider) =>
        provider is null
        || (entry.Provider is not null
            && string.Equals(entry.Provider, provider, StringComparison.OrdinalIgnoreCase));
}
