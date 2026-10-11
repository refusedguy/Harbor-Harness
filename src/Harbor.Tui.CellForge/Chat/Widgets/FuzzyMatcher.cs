namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Subsequence fuzzy scorer for the command palette (fzf-style, simplified).
/// Match = query characters appear in candidate in order. Score rewards
/// word-boundary and consecutive hits, penalizes sparse matches and length.
/// Steals three items from textual's <c>fuzzy.py</c> (<c>FuzzySearch</c>):
/// (query, candidate) LRU cache (4096), substring fast-path (×1.5, ×2.0 when
/// equal — skips the per-character scan), and first-letter bonus + group-count
/// scoring. Boundary/consecutive base was already here.
/// Cache lookups are locked but allocation-light (struct key, no boxing);
/// scoring itself allocates nothing.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>No match sentinel — <see cref="Score" /> returns null.</summary>
    public const int MinScore = int.MinValue;

    /// <summary>Cache capacity — textual's <c>FuzzySearch</c> default (1024 * 4).</summary>
    private const int CacheCapacity = 4096;

    /// <summary>Substring fast-path boost — textual yields score * 1.5.</summary>
    private const double SubstringBoost = 1.5;

    /// <summary>Exact-match boost — textual yields score * 2.0.</summary>
    private const double ExactBoost = 2.0;

    /// <summary>
    /// Per matched word-start bonus — textual's <c>offset_count +
    /// |first_letters ∩ positions|</c>, scaled to our int range (boundary = 16).
    /// </summary>
    private const int FirstLetterBonus = 8;

    /// <summary>
    /// Contiguity bonus scale — textual multiplies by <c>1 + norm²</c> where
    /// <c>norm = (n - (groups-1)) / n</c>. Our base is a signed int (early-hit
    /// and length penalties can drive it negative), where a multiplier would
    /// invert ordering, so the same norm prices additively:
    /// <c>n * norm² * scale</c>. Contiguous (1 group) scores the full
    /// <c>n * scale</c>; fully scattered lands near zero.
    /// </summary>
    private const int GroupBonusScale = 4;

    private readonly record struct CacheKey(string Query, string Candidate);

    private static readonly object _sync = new();
    private static readonly Dictionary<CacheKey, LinkedListNode<(CacheKey Key, int? Value)>> _entries = new();
    private static readonly LinkedList<(CacheKey Key, int? Value)> _lru = new();

    /// <summary>Number of cached (query, candidate) entries (≤ 4096).</summary>
    public static int CacheCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Clears the score cache — tests and benchmarks only.</summary>
    public static void ClearCache()
    {
        lock (_sync)
        {
            _entries.Clear();
            _lru.Clear();
        }
    }

    /// <summary>
    /// Scores <paramref name="candidate" /> against <paramref name="query" />.
    /// null = not a subsequence match; higher = better.
    /// Results (including misses) are cached per (query, candidate).
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

        var key = new CacheKey(query, candidate);
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddLast(node);
                return node.Value.Value;
            }
        }

        int? result = ScoreCore(query, candidate);

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _lru.AddLast(existing);
                return existing.Value.Value;
            }

            LinkedListNode<(CacheKey Key, int? Value)> node = _lru.AddLast((key, result));
            _entries[key] = node;
            while (_entries.Count > CacheCapacity && _lru.First is not null)
            {
                _entries.Remove(_lru.First.Value.Key);
                _lru.RemoveFirst();
            }
        }

        return result;
    }

    /// <summary>
    /// Ranks <paramref name="candidates" /> by <paramref name="query" />:
    /// matching items first (best score first), non-matching dropped.
    /// Stable for equal scores — suggested order survives.
    /// Repeated filters with the same query hit the <see cref="Score" /> cache.
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

    private static int? ScoreCore(string query, string candidate)
    {
        // Substring fast-path (textual: `if query in candidate`): contiguous
        // offsets, no per-character scan, boosted score.
        int sub = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (sub >= 0)
        {
            return ScoreSubstring(query, candidate, sub);
        }

        return ScoreGreedy(query, candidate);
    }

    private static int ScoreSubstring(string query, string candidate, int start)
    {
        int score = 0;
        int firstLetters = 0;
        for (int k = 0; k < query.Length; k++)
        {
            int ci = start + k;
            // k == 0 mirrors the greedy loop's first-match boundary clause below.
            if (k == 0 || IsWordStart(candidate, ci))
            {
                score += 16;
            }

            if (IsWordStart(candidate, ci))
            {
                firstLetters++;
            }

            if (k > 0)
            {
                score += 12; // contiguous by construction — maximal streak
            }

            score -= ci; // early hits read as more relevant
        }

        score = Finish(score, query.Length, firstLetters, groups: 1, candidate.Length - query.Length);

        // Lengths equal + substring found ⇒ equal ignoring case (textual's ×2.0).
        double boost = query.Length == candidate.Length ? ExactBoost : SubstringBoost;
        return (int)Math.Round(score * boost, MidpointRounding.AwayFromZero);
    }

    private static int? ScoreGreedy(string query, string candidate)
    {
        int score = 0;
        int qi = 0;
        int prevMatch = -2;
        int matched = 0;
        int firstLetters = 0;
        int groups = 0;
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
            // The first matched char always counts (preserves original ranking).
            bool boundary = prevMatch < 0 && firstSegment
                || IsWordStart(candidate, ci);
            if (boundary)
            {
                score += 16;
            }

            if (IsWordStart(candidate, ci))
            {
                firstLetters++;
            }

            // Consecutive-hit bonus (stronger the longer the streak).
            if (prevMatch == ci - 1)
            {
                score += 12;
            }

            // Group count (textual): a new run starts on every non-adjacent hit.
            if (prevMatch == ci - 1 && groups > 0)
            {
                // same run — group count unchanged
            }
            else
            {
                groups++;
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

        return Finish(score, matched, firstLetters, groups, candidate.Length - query.Length);
    }

    private static int Finish(int score, int matched, int firstLetters, int groups, int lengthDelta)
    {
        score -= lengthDelta / 4; // mild length penalty
        score += firstLetters * FirstLetterBonus;
        double normalized = (matched - (groups - 1)) / (double)matched;
        score += (int)Math.Round(matched * normalized * normalized * GroupBonusScale, MidpointRounding.AwayFromZero);
        return score;
    }

    private static bool IsWordStart(string candidate, int index)
    {
        if (index == 0)
        {
            return true;
        }

        char prev = candidate[index - 1];
        if (IsSeparator(prev))
        {
            return true;
        }

        char cur = candidate[index];
        return char.IsUpper(cur) && !char.IsUpper(prev);
    }

    private static bool IsSeparator(char c) => c is ' ' or '-' or '_' or '/' or '.' or ':' or '>';
}
