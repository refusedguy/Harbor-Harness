namespace Harbor.Desktop.Shared.Services;
/// <summary>
///     Lightweight fuzzy-search service for the command palette. Uses a
///     simplified subsequence-match score (à la Sublime Text / VS Code
///     Command Palette) — no external dependencies, no allocations beyond
///     the score buffer.
/// </summary>
public sealed class FuzzySearchService
{
    /// <summary>
    ///     Score <paramref name="candidate" /> against <paramref name="query" />.
    ///     Higher score = better match; <c>0</c> = no match.
    /// </summary>
    /// <param name="candidate">Haystack text (e.g. command title).</param>
    /// <param name="query">Needle text (user-typed query).</param>
    /// <returns>Score, or 0 if the candidate does not contain query as a subsequence.</returns>
    public int Score(string candidate, string query)
    {
        if (string.IsNullOrEmpty(query)) return 1;
        if (string.IsNullOrEmpty(candidate)) return 0;
        if (query.Length > candidate.Length) return 0;

        int score = 0;
        int ci = 0; // candidate index
        int qi = 0; // query index
        int consecutive = 0;

        // Case-insensitive comparison via culture-invariant char compare.
        while (ci < candidate.Length && qi < query.Length)
        {
            char c = char.ToLowerInvariant(candidate[ci]);
            char q = char.ToLowerInvariant(query[qi]);
            if (c == q)
            {
                consecutive++;
                // Reward consecutive matches, word-boundary starts, and CamelCase humps.
                if (ci == 0 || !char.IsLetterOrDigit(candidate[ci - 1]))
                    score += 20;
                else if (char.IsUpper(candidate[ci]) && char.IsLower(candidate[ci - 1]))
                    score += 15;
                else
                    score += 1 + consecutive;

                qi++;
            }
            else
            {
                consecutive = 0;
            }
            ci++;
        }

        return qi == query.Length ? score : 0;
    }

    /// <summary>
    ///     Rank <paramref name="items" /> by fuzzy-match score against
    ///     <paramref name="query" />. Returns only items with score > 0,
    ///     sorted descending.
    /// </summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="items">Items to rank.</param>
    /// <param name="query">User-typed query.</param>
    /// <param name="textSelector">Maps an item to the text to match against.</param>
    /// <returns>Items with score > 0, sorted by score descending.</returns>
    public IReadOnlyList<(T Item, int Score)> Rank<T>(
        IEnumerable<T> items,
        string query,
        Func<T, string> textSelector)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (textSelector is null) throw new ArgumentNullException(nameof(textSelector));

        var results = new List<(T Item, int Score)>();
        foreach (var item in items)
        {
            string text = textSelector(item);
            int s = Score(text, query);
            if (s > 0) results.Add((item, s));
        }
        results.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        return results;
    }
}
