using System.Text;

namespace Harbor.Tui.ConsoleEx.Widgets;

/// <summary>
/// Subsequence fuzzy scorer for the command palette (fzf-style, simplified).
/// Match = query characters appear in candidate in order. Score rewards
/// word-boundary and consecutive hits, penalizes sparse matches and length.
/// Pure static functions, zero allocations on the hot path.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>No match sentinel — <see cref="Score" /> returns null.</summary>
    public const int MinScore = int.MinValue;

    /// <summary>
    /// Scores <paramref name="candidate" /> against <paramref name="query" />.
    /// null = not a subsequence match; higher = better.
    /// </summary>
    public static int? Score(string query, string candidate)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 0; // empty query — everything matches neutrally (prefix order preserved)
        }

        if (string.IsNullOrEmpty(candidate) || query.Length > candidate.Length)
        {
            return null;
        }

        int score = 0;
        int qi = 0;
        int prevMatch = -2;
        int matched = 0;
        bool firstSegment = true;

        for (int ci = 0; ci < candidate.Length && qi < query.Length; ci++)
        {
            char cc = candidate[ci];
            char qc = query[qi];
            if (char.ToLowerInvariant(cc) != char.ToLowerInvariant(qc))
            {
                continue;
            }

            // Word-boundary bonus: string start, separator, or camelCase hump.
            bool boundary = prevMatch < 0 && firstSegment
                || ci == 0
                || IsSeparator(candidate[ci - 1])
                || (char.IsUpper(cc) && !char.IsUpper(candidate[ci - 1]));
            if (boundary)
            {
                score += 16;
            }

            // Consecutive-hit bonus (stronger the longer the streak).
            if (prevMatch == ci - 1)
            {
                score += 12;
            }

            if (prevMatch < 0)
            {
                firstSegment = false;
            }

            // Early hits read as more relevant.
            score -= ci;
            prevMatch = ci;
            qi++;
            matched++;
        }

        if (matched < query.Length)
        {
            return null; // not a full subsequence
        }

        score -= (candidate.Length - query.Length) / 4; // mild length penalty
        return score;
    }

    /// <summary>
    /// Ranks <paramref name="candidates" /> by <paramref name="query" />:
    /// matching items first (best score first), non-matching dropped.
    /// Stable for equal scores — suggested order survives.
    /// </summary>
    public static List<T> Filter<T>(string query, IReadOnlyList<T> candidates, Func<T, string> textOf)
    {
        ArgumentNullException.ThrowIfNull(textOf);

        var result = new List<T>(candidates.Count);
        if (query.Length == 0)
        {
            result.AddRange(candidates);
            return result;
        }

        var scored = new List<(T Item, int Score)>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            int? score = Score(query, textOf(candidates[i]));
            if (score.HasValue)
            {
                scored.Add((candidates[i], score.Value - i)); // earlier entries win ties
            }
        }

        scored.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        foreach (var (item, _) in scored)
        {
            result.Add(item);
        }

        return result;
    }

    private static bool IsSeparator(char c) => c is ' ' or '-' or '_' or '/' or '.' or ':' or '>';
}
