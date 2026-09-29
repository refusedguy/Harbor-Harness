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
/// then projected independently around those matches.
///
/// <para>
///     Every structure here is sized from the row being diffed, and the row is
///     untrusted: it arrives from LLM tool output — an <c>edit</c> or a
///     <c>patch</c> over a minified bundle is one single line, and nothing in
///     the format caps its length. Both the token arrays and the LCS table are
///     therefore bounded (<see cref="MaxPairableLineChars" /> and
///     <see cref="MaxLcsMatrixCells" />); past a bound the pair degrades to the
///     plain line-level picture instead of growing with the input.
/// </para>
/// Pure functions; allocation is limited to the token arrays, the LCS table and
/// the result records, all of them capped as above.
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
    /// pairing, enforced by <see cref="Segment" /> itself — so every entry
    /// point, the public <see cref="TryPair" /> included, tokenizes at most
    /// this much text. 4 KiB of text is at most 2 048 whitespace tokens, i.e. a
    /// 16 KiB token array, whatever the file on disk holds. A minified line (a
    /// bundled asset, a generated lockfile row) falls back to plain line-level
    /// rendering instead of stalling the first paint.
    /// </summary>
    public const int MaxPairableLineChars = 4096;

    /// <summary>
    /// Hard cap on the LCS table, in cells. <see cref="Matches" /> needs the
    /// whole <c>(oldTokens+1) × (newTokens+1)</c> table to backtrack through,
    /// and that table is the largest structure on the diff parse path — sized
    /// entirely by untrusted LLM tool output. At the 10 000 tokens a side the
    /// issue was filed about it is 400 MB; this makes the worst case a
    /// constant.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     128 × 128 = 16 384 cells = 64 KiB, chosen as the largest table that
    ///     still lands below the 85 000-byte LOH threshold: the worst case is a
    ///     gen0/gen1 array the GC reclaims without fragmenting, rather than one
    ///     parked on the large object heap where it is never compacted. 128
    ///     words a side is about four wrapped lines of a 120-column terminal,
    ///     past which per-word highlighting on a single row buys nothing
    ///     readable.
    /// </para>
    /// <para>
    ///     The cap is on the <em>product</em>, not on either side, so a
    ///     2 048-token row against a 5-token one (2 049 × 6 cells) still gets a
    ///     real word diff. What it rejects is the square case.
    /// </para>
    /// <para>
    ///     Over the cap the pair is reported as sharing no words, which
    ///     <see cref="Project" /> already expresses as one Deleted run and one
    ///     Added run — and <see cref="PairRun" /> declines to emit a pair at
    ///     all, so the row keeps painting exactly as it did before pairing.
    /// </para>
    /// </remarks>
    public const int MaxLcsMatrixCells = 128 * 128;

    /// <summary>
    /// Half-width, in rows, of the candidate window <see cref="PairRun"/> scans
    /// for a partner. Rewrites stay roughly order-preserving, so only the adds
    /// near a delete's proportional position are scored. Bounds the search at
    /// O(deletes × 2 × radius) similarity calls, keeping parse time linear for
    /// whole-file reformat hunks; smaller runs are searched in full.
    /// </summary>
    private const int PairSearchRadius = 4;

    /// <summary>Either side may be empty (pure add or pure delete line).</summary>
    public static WordDiffSides Segment(string oldLine, string newLine) =>
        SegmentCore(oldLine, newLine).Sides;

    /// <summary>
    ///     <see cref="Segment" /> plus the budget verdict, so
    ///     <see cref="PairRun" /> can tell "these rows share no words" (a real
    ///     diff, still worth a pair) from "the table did not fit" (not a diff
    ///     at all, and emitting a pair would only redraw the row one character
    ///     short of its right edge).
    /// </summary>
    private static (WordDiffSides Sides, bool WithinBudget) SegmentCore(string oldLine, string newLine)
    {
        oldLine ??= string.Empty;
        newLine ??= string.Empty;

        // Cap BEFORE tokenizing: the token array is sized by the row, so a
        // megabyte line would spend 8 MB on 500k references before the LCS
        // guard ever got a chance to look at it.
        if (oldLine.Length > MaxPairableLineChars || newLine.Length > MaxPairableLineChars)
            return (Unpaired(oldLine, newLine), WithinBudget: true);

        var oldTok = Tokenize(oldLine);
        var newTok = Tokenize(newLine);

        // Matched pair indices collected from the classic backtrack.
        var (matchOld, matchNew, withinBudget) = Matches(oldTok, newTok);
        return (
            new WordDiffSides(
                Project(oldTok, matchOld, WordSegKind.Deleted),
                Project(newTok, matchNew, WordSegKind.Added)),
            withinBudget);
    }

    /// <summary>
    ///     The no-anchor picture: one run per side covering the whole row, so
    ///     the paint is the row in its accent colour — what an unpaired
    ///     line-level row already looks like. Empty sides stay empty, matching
    ///     the projection of a side with no tokens.
    /// </summary>
    private static WordDiffSides Unpaired(string oldLine, string newLine)
    {
        IReadOnlyList<WordSeg> removed = oldLine.Length == 0
            ? Array.Empty<WordSeg>()
            : new[] { new WordSeg(WordSegKind.Deleted, oldLine) };
        IReadOnlyList<WordSeg> inserted = newLine.Length == 0
            ? Array.Empty<WordSeg>()
            : new[] { new WordSeg(WordSegKind.Added, newLine) };
        return new WordDiffSides(removed, inserted);
    }

    private static string[] Tokenize(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static (int[] OldMatch, int[] NewMatch, bool WithinBudget) Matches(string[] a, string[] b)
    {
        var oldMatch = new int[a.Length];
        Array.Fill(oldMatch, -1);
        var newMatch = new int[b.Length];
        Array.Fill(newMatch, -1);

        int cols = b.Length + 1;
        if ((long)(a.Length + 1) * cols > MaxLcsMatrixCells)
            return (oldMatch, newMatch, WithinBudget: false);

        // Flat 1-D table: one allocation, and the backtrack walks it with a
        // stride instead of a two-dimensional indexer. Fill order and the
        // tie-break below are the ones the matrix version used, so every pair
        // inside the budget produces byte-identical segments.
        var lcs = new int[(a.Length + 1) * cols];
        for (int i = 1; i <= a.Length; i++)
        {
            int row = i * cols;
            int prev = row - cols;
            for (int j = 1; j <= b.Length; j++)
            {
                lcs[row + j] = StringComparer.Ordinal.Equals(a[i - 1], b[j - 1])
                    ? lcs[prev + j - 1] + 1
                    : Math.Max(lcs[prev + j], lcs[row + j - 1]);
            }
        }

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

            if (lcs[(x - 1) * cols + y] >= lcs[x * cols + (y - 1)])
            {
                x--;
            }
            else
            {
                y--;
            }
        }

        return (oldMatch, newMatch, WithinBudget: true);
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

            var segmented = SegmentCore(lines[deleteRunStart + bestDelete].Text, lines[addRunStart + bestAdd].Text);
            if (!segmented.WithinBudget)
            {
                // The LCS table did not fit, so there is no anchor to draw.
                // A pair here would paint the row from an empty context run —
                // and one character short of its right edge, since the
                // segmented painter budgets a separator the line painter does
                // not. Unpaired is the identical picture at no cost.
                continue;
            }

            pairs.Add(new WordPair(
                deleteRunStart + bestDelete,
                addRunStart + bestAdd,
                segmented.Sides));
        }

        return pairs;
    }
}
