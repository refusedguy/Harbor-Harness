using Harbor.Ui.Framework.Rendering;

namespace Harbor.Ui.Framework.Rendering.Widgets;

/// <summary>Segment kind inside an intraline (word-level) diff.</summary>
public enum WordSegKind : byte
{
    Equal,
    Deleted,
    Added,
}

/// <summary>One contiguous run of words sharing a diff kind.</summary>
public readonly record struct WordSeg(WordSegKind Kind, string Text);

/// <summary>
/// One delete→add row pair found inside an N:M rewrite run, addressed by its
/// row index in the caller's line list. Both rows share <see cref="Sides"/>;
/// the caller paints the side matching each row's kind.
/// </summary>
public readonly record struct WordPair(int DeleteIndex, int AddIndex, WordDiffSides Sides);

/// <summary>
/// Per-row projections of an intraline (word-level) diff. Shared tokens keep
/// their SOURCE ORDER in each projection, so both rows read naturally while
/// anchoring on exactly the same matched words.
/// </summary>
/// <param name="Removed">Old-line view: Equal runs plus Deleted runs.</param>
/// <param name="Inserted">New-line view: Equal runs plus Added runs.</param>
public sealed record WordDiffSides(
    IReadOnlyList<WordSeg> Removed,
    IReadOnlyList<WordSeg> Inserted);

/// <summary>
/// Whitespace-token intraline diff between a removed and an added diff row
/// (git --word-diff equivalent). An LCS finds matched word pairs; each row is
/// then projected independently around those matches. Pure functions; no
/// allocation beyond result records.
/// </summary>
public static class WordDiff
{
    /// <summary>
    /// Pair-or-fall-back threshold for N:M runs, as a Sørensen–Dice token
    /// overlap percentage (0..100) — see <see cref="SimilarityPercent"/> for
    /// the metric, which for equal-length rows is just "share of shared
    /// tokens". 34 % is therefore "a third of the words match": enough to
    /// anchor on, low enough that a re-indent, a rename of one identifier or
    /// a wrapped expression still pairs. A candidate below it is an unrelated
    /// rewrite and stays unpaired, so it paints as a plain line-level row —
    /// pairing rows with nothing in common would leave the context run empty
    /// and rainbow the whole line for no signal.
    /// </summary>
    public const int MinRunPairSimilarityPercent = 34;

    /// <summary>
    /// Hard cap (in characters) on a single row taking part in intraline
    /// pairing. <see cref="Segment"/> allocates an O(tokens²) LCS matrix, so a
    /// minified line (a bundled asset, a generated lockfile row) would make
    /// parse time quadratic in megabytes. Rows above the cap fall back to
    /// plain line-level rendering instead of stalling the first paint.
    /// </summary>
    public const int MaxPairableLineChars = 4096;

    /// <summary>
    /// Half-width, in rows, of the candidate window <see cref="PairRun"/> scans
    /// for a partner. Rewrites stay roughly order-preserving, so only the adds
    /// near a delete's proportional position are scored. Bounds the search at
    /// O(deletes × 2 × radius) similarity calls, keeping parse time linear for
    /// whole-file reformat hunks; smaller runs are searched in full.
    /// </summary>
    private const int PairSearchRadius = 4;

    /// <summary>Either side may be empty (pure add or pure delete line).</summary>
    public static WordDiffSides Segment(string oldLine, string newLine)
    {
        var oldTok = Tokenize(oldLine ?? string.Empty);
        var newTok = Tokenize(newLine ?? string.Empty);

        // Matched pair indices collected from the classic backtrack.
        var (matchOld, matchNew) = Matches(oldTok, newTok);
        return new WordDiffSides(
            Project(oldTok, matchOld, WordSegKind.Deleted),
            Project(newTok, matchNew, WordSegKind.Added));
    }

    private static string[] Tokenize(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static (int[] OldMatch, int[] NewMatch) Matches(string[] a, string[] b)
    {
        int[,] lcs = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                lcs[i, j] = StringComparer.Ordinal.Equals(a[i - 1], b[j - 1])
                    ? lcs[i - 1, j - 1] + 1
                    : Math.Max(lcs[i - 1, j], lcs[i, j - 1]);
            }
        }

        var oldMatch = new int[a.Length];
        Array.Fill(oldMatch, -1);
        var newMatch = new int[b.Length];
        Array.Fill(newMatch, -1);

        int x = a.Length;
        int y = b.Length;
        while (x > 0 && y > 0)
        {
            if (StringComparer.Ordinal.Equals(a[x - 1], b[y - 1]))
            {
                oldMatch[x - 1] = y - 1;
                newMatch[y - 1] = x - 1;
                x--;
                y--;
                continue;
            }

            if (lcs[x - 1, y] >= lcs[x, y - 1])
            {
                x--;
            }
            else
            {
                y--;
            }
        }

        return (oldMatch, newMatch);
    }

    /// <summary>Projects one token array around its matches into ordered runs.</summary>
    private static List<WordSeg> Project(string[] tokens, int[] match, WordSegKind gapKind)
    {
        var runs = new List<WordSeg>(tokens.Length);
        var buffer = new System.Text.StringBuilder();
        WordSegKind current = WordSegKind.Equal;

        void Flush()
        {
            if (buffer.Length > 0)
            {
                runs.Add(new WordSeg(current, buffer.ToString()));
                buffer.Clear();
            }
        }

        for (int t = 0; t < tokens.Length; t++)
        {
            WordSegKind kind = match[t] >= 0 ? WordSegKind.Equal : gapKind;
            if (kind != current && buffer.Length > 0)
            {
                Flush();
            }

            current = kind;
            if (buffer.Length > 0)
            {
                buffer.Append(' ');
            }

            buffer.Append(tokens[t]);
        }

        Flush();
        return runs;
    }

    /// <summary>
    /// Pair a Delete row with its following Add row (widgets §3.10): both
    /// sides share the same matched anchors; callers pick the side matching
    /// their row kind.
    /// </summary>
    public static WordDiffSides? TryPair(DiffLine delete, DiffLine add) =>
        delete.Kind == DiffLineKind.Delete && add.Kind == DiffLineKind.Add
            ? Segment(delete.Text, add.Text)
            : null;

    /// <summary>
    /// Line similarity as a Sørensen–Dice coefficient over whitespace-token
    /// multisets, in percent: <c>200 × |A ∩ B| / (|A| + |B|)</c>. Repetition
    /// counts, not just set membership, so a line that repeats a token is not
    /// flattered by it. Multiset intersection is order-insensitive, which is
    /// what we want — a reorder is still the same line. Two empty lines score
    /// 0 rather than 100: "no tokens" must never read as "identical rewrite".
    /// Pure and ordinal — tokens are compared with <see cref="StringComparer.Ordinal"/>
    /// and no case folding or <c>CompareTo</c> is involved, so the result is
    /// culture-independent and identical on every run.
    /// </summary>
    public static int SimilarityPercent(string left, string right)
    {
        var a = Tokenize(left ?? string.Empty);
        var b = Tokenize(right ?? string.Empty);
        int total = a.Length + b.Length;
        return total == 0 ? 0 : (int)(200L * MultisetOverlap(a, b) / total);
    }

    /// <summary>Multiset intersection size of two token arrays (ordinal).</summary>
    private static int MultisetOverlap(string[] left, string[] right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        // Count the shorter side, then stream the longer one against it.
        bool leftIsShorter = left.Length <= right.Length;
        string[] counted = leftIsShorter ? left : right;
        string[] streamed = leftIsShorter ? right : left;

        var counts = new Dictionary<string, int>(counted.Length, StringComparer.Ordinal);
        for (int i = 0; i < counted.Length; i++)
        {
            counts.TryGetValue(counted[i], out int seen);
            counts[counted[i]] = seen + 1;
        }

        int common = 0;
        for (int i = 0; i < streamed.Length; i++)
        {
            if (counts.TryGetValue(streamed[i], out int remaining) && remaining > 0)
            {
                counts[streamed[i]] = remaining - 1;
                common++;
            }
        }

        return common;
    }

    /// <summary>
    /// Pair the run of consecutive Delete rows starting at <paramref name="deleteRunStart"/>
    /// with the run of Add rows that immediately follows it — the N:M case (3
    /// lines rewritten into 5) that a 1:1 scan cannot see.
    ///
    /// Greedy best-first: each round scores the unconsumed rows within their
    /// positional window, takes the closest pair, and consumes both, so a row
    /// takes part in at most one pair and the closest pairs win. Leftovers stay
    /// unpaired and the caller paints them as plain line-level rows.
    ///
    /// A pair is emitted only when it is at least
    /// <see cref="MinRunPairSimilarityPercent"/> similar. The one exception is
    /// an exact 1:1 run, which is always paired — that is the shipped
    /// single-replace behaviour and a pair with no shared anchor degrades to
    /// the same picture anyway (its context run is empty, so the whole row
    /// takes the accent). Every other shape, 2:2 included, is a guess about
    /// which rows correspond and has to clear the bar.
    ///
    /// Pure and deterministic: candidates are scanned in row order, equal
    /// scores keep the earlier Delete row, and no culture-sensitive
    /// comparison is on the path.
    /// </summary>
    public static IReadOnlyList<WordPair> PairRun(IReadOnlyList<DiffLine> lines, int deleteRunStart)
    {
        if (lines is null || deleteRunStart < 0 || deleteRunStart >= lines.Count
            || lines[deleteRunStart].Kind != DiffLineKind.Delete)
        {
            return [];
        }

        // Collect the two runs: Delete rows, then the Add rows right after them.
        int addRunStart = deleteRunStart;
        while (addRunStart < lines.Count && lines[addRunStart].Kind == DiffLineKind.Delete)
        {
            addRunStart++;
        }

        int addRunEnd = addRunStart;
        while (addRunEnd < lines.Count && lines[addRunEnd].Kind == DiffLineKind.Add)
        {
            addRunEnd++;
        }

        int deletes = addRunStart - deleteRunStart;
        int adds = addRunEnd - addRunStart;
        if (deletes == 0 || adds == 0)
        {
            return [];
        }

        // Guardrail: a run touching an oversized row skips N:M pairing wholesale
        // rather than paying the O(tokens²) matrix for a megabyte minified line.
        for (int i = deleteRunStart; i < addRunEnd; i++)
        {
            if (lines[i].Text.Length > MaxPairableLineChars)
            {
                return [];
            }
        }

        // Only an exact 1:1 run skips the bar: it is the shipped
        // single-replace behaviour, and a pair with no shared anchor
        // degrades to the same picture anyway (its context run is empty, so
        // the whole row takes the accent). Every N:M shape — 2:2 included —
        // is a guess, so it has to earn the threshold.
        bool requireSimilarity = deletes != 1 || adds != 1;
        int maxPairs = Math.Min(deletes, adds);
        var deleteUsed = new bool[deletes];
        var addUsed = new bool[adds];
        var pairs = new List<WordPair>(maxPairs);

        for (int round = 0; round < maxPairs; round++)
        {
            int bestScore = -1;
            int bestDelete = -1;
            int bestAdd = -1;

            for (int d = 0; d < deletes; d++)
            {
                if (deleteUsed[d])
                {
                    continue;
                }

                int center = (int)((long)d * adds / deletes);
                int lo = Math.Max(0, center - PairSearchRadius);
                int hi = Math.Min(adds - 1, center + PairSearchRadius);
                for (int a = lo; a <= hi; a++)
                {
                    if (addUsed[a])
                    {
                        continue;
                    }

                    int score = SimilarityPercent(lines[deleteRunStart + d].Text, lines[addRunStart + a].Text);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestDelete = d;
                        bestAdd = a;
                    }
                }
            }

            if (bestDelete < 0)
            {
                break;
            }

            deleteUsed[bestDelete] = true;
            addUsed[bestAdd] = true;

            if (requireSimilarity && bestScore < MinRunPairSimilarityPercent)
            {
                // Below the bar: consume both (a closer partner is already
                // gone) and leave the rows to line-level painting.
                continue;
            }

            pairs.Add(new WordPair(
                deleteRunStart + bestDelete,
                addRunStart + bestAdd,
                Segment(lines[deleteRunStart + bestDelete].Text, lines[addRunStart + bestAdd].Text)));
        }

        return pairs;
    }
}
