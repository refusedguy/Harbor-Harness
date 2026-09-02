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
}
