namespace Harbor.Ui.Framework.Overlays;

/// <summary>
///     Shared fuzzy scorer for the slash-panel models (plugins, providers,
///     session tree). Same semantics as the jump palette
///     (<see cref="WorktreeJumpPaletteModel" />): an exact substring wins and
///     shorter texts rank first; otherwise a case-insensitive subsequence
///     match scores the tightness, else <c>-1</c> (no match).
/// </summary>
/// <remarks>
///     Higher is better; <c>-1</c> filters the row out. Substring hits live in
///     the <c>10000 - length</c> band so they always outrank subsequence hits.
///     Pure BCL, zero Harbor dependencies.
/// </remarks>
public static class PanelFuzzy
{
    /// <summary>Score <paramref name="text" /> against <paramref name="query" />.</summary>
    /// <param name="text">Row text to match (may be null or empty — never matches).</param>
    /// <param name="query">Filter text; null/empty matches everything with score 0.</param>
    /// <returns>Match score (higher is better), or <c>-1</c> on no match.</returns>
    public static int Score(string? text, string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 0;
        }

        if (string.IsNullOrEmpty(text))
        {
            return -1;
        }

        if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 10000 - text.Length;
        }

        int ti = 0;
        int budget = 9000;
        int lastHit = -1;
        for (int qi = 0; qi < query.Length; qi++)
        {
            char q = char.ToLowerInvariant(query[qi]);
            bool hit = false;
            while (ti < text.Length)
            {
                if (char.ToLowerInvariant(text[ti]) == q)
                {
                    budget -= ti - lastHit - 1;
                    lastHit = ti;
                    ti++;
                    hit = true;
                    break;
                }

                ti++;
            }

            if (!hit)
            {
                return -1;
            }
        }

        // Clamp: 0 still passes the >= 0 filter, -1 stays the no-match sentinel.
        return Math.Max(0, budget - text.Length);
    }
}
