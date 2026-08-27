using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Widgets;

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
/// Whitespace-token intraline diff between a removed and an added diff row
/// (git --word-diff equivalent). LCS over tokens keeps runs stable instead of
/// producing noisy single-character churn; pure functions, zero allocations
/// beyond result lists.
/// </summary>
public static class WordDiff
{
    /// <summary>
    /// Segment the old/new line into Equal/Deleted/Added runs in wire order
    /// (all deletions precede their matched additions within one changed run).
    /// Either side may be empty (pure add or pure delete line).
    /// </summary>
    public static IReadOnlyList<WordSeg> Segment(string oldLine, string newLine)
    {
        var oldTok = Tokenize(oldLine ?? string.Empty);
        var newTok = Tokenize(newLine ?? string.Empty);
        int[,] lcs = LcsLengths(oldTok, newTok);

        var reversed = new List<WordSeg>(Math.Max(oldTok.Length, newTok.Length));
        WalkBackwards(oldTok, newTok, lcs, reversed);

        // Reversed backtrack walked right→left; folding while iterating that
        // list from its tail rebuilds wire order runs directly.
        var merged = new List<WordSeg>(reversed.Count);
        foreach (var seg in FoldRunsReversed(reversed))
        {
            merged.Add(seg);
        }

        return merged;
    }

    /// <summary>Splits on whitespace, keeping separators invisible.</summary>
    private static string[] Tokenize(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static int[,] LcsLengths(string[] a, string[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                table[i, j] = StringComparer.Ordinal.Equals(a[i - 1], b[j - 1])
                    ? table[i - 1, j - 1] + 1
                    : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        return table;
    }

    private static void WalkBackwards(string[] a, string[] b, int[,] lcs, List<WordSeg> outReversed)
    {
        int i = a.Length;
        int j = b.Length;
        while (i > 0 || j > 0)
        {
            if (i > 0 && j > 0 && StringComparer.Ordinal.Equals(a[i - 1], b[j - 1]))
            {
                outReversed.Add(new WordSeg(WordSegKind.Equal, a[i - 1]));
                i--;
                j--;
                continue;
            }

            // Deletions first so edits read «old → new» once re-reversed.
            if (j == 0 || (i > 0 && lcs[i - 1, j] >= lcs[i, j - 1]))
            {
                outReversed.Add(new WordSeg(WordSegKind.Deleted, a[i - 1]));
                i--;
            }
            else
            {
                outReversed.Add(new WordSeg(WordSegKind.Added, b[j - 1]));
                j--;
            }
        }
    }

    /// <summary>
    /// Folds same-kind neighbours into space-joined runs. <paramref name="reversedTokens"/>
    /// is in right→left discovery order; iterating from its tail emits wire order.
    /// </summary>
    private static IEnumerable<WordSeg> FoldRunsReversed(List<WordSeg> reversedTokens)
    {
        WordSegKind runKind = WordSegKind.Equal;
        var buffer = new System.Text.StringBuilder();

        for (int idx = reversedTokens.Count - 1; idx >= 0; idx--)
        {
            var tok = reversedTokens[idx];
            if (tok.Kind != runKind && buffer.Length > 0)
            {
                yield return new WordSeg(runKind, buffer.ToString());
                buffer.Clear();
            }

            runKind = tok.Kind;
            if (buffer.Length > 0)
            {
                buffer.Append(' ');
            }

            buffer.Append(tok.Text);
        }

        if (buffer.Length > 0)
        {
            yield return new WordSeg(runKind, buffer.ToString());
        }
    }

    /// <summary>
    /// Pair a Delete row with its following Add row (widget §3.10 integration):
    /// returns the segmented view painted as one logical changed line.
    /// </summary>
    public static IReadOnlyList<WordSeg>? TryPair(DiffLine delete, DiffLine add) =>
        delete.Kind == DiffLineKind.Delete && add.Kind == DiffLineKind.Add
            ? Segment(delete.Text, add.Text)
            : null;
}
