// WordDiffHighlight.cs — K13: word-level inline highlighting for the compact
// diff shown in a tool call card.
//
// HdsDiffCompact used to paint a whole changed line in one colour. This class
// splits a paired delete/add line into word runs so that shared words stay dim
// and only the words that actually changed keep the line's colour. The split
// itself is not reimplemented here: WordDiff.Segment in the headless rendering
// layer is the one intraline engine, and this file only projects its segments
// into display runs. Pure and AOT-safe: no XAML, no dispatcher, no reflection.

using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>One run of text inside a compact-diff line (K13).</summary>
/// <param name="Text">
/// The run's text. Every run but the last in a line carries one trailing space
/// so the words of adjacent runs do not run together when rendered.
/// </param>
/// <param name="Emphasized">
/// True when the run holds a word that changed. False for a shared word, the
/// row prefix, or a line that was not paired at all.
/// </param>
public readonly record struct WordDiffRun(string Text, bool Emphasized);

/// <summary>A compact-diff line split into the runs that make it up.</summary>
/// <param name="RawText">The original line, including its row prefix.</param>
/// <param name="Runs">Ordered runs to render for this line.</param>
public sealed record WordDiffLine(string RawText, IReadOnlyList<WordDiffRun> Runs);

/// <summary>
/// Pure word-level projection for the compact diff (K13).
/// </summary>
/// <remarks>
/// <para>
///     A unified diff writes a rewrite as a delete row followed by an add row.
///     When those two rows sit next to each other this class pairs them and
///     asks <see cref="WordDiff.Segment" /> which words the two rows share. The
///     result is one set of runs per row: shared words are ordinary runs and
///     the words unique to that row are emphasized. A row with no partner, a
///     hunk header or a plain context line keeps a single ordinary run, so its
///     appearance is exactly what it was before this projection existed.
/// </para>
/// <para>
///     The truncation row is produced here too, at the same limit the view used
///     before, so the view stays a thin renderer of what this method returns.
/// </para>
/// </remarks>
public static class WordDiffHighlight
{
    /// <summary>Text of the row appended when the diff is longer than the view allows.</summary>
    public const string TruncationText = "… diff truncated";

    /// <summary>
    /// Projects <paramref name="diffText" /> into at most <paramref name="maxLines" />
    /// display rows, pairing adjacent delete/add rows for word-level highlighting.
    /// </summary>
    /// <param name="diffText">Raw unified-diff text. Null or empty yields no rows.</param>
    /// <param name="maxLines">Maximum number of real rows before the truncation row.</param>
    public static IReadOnlyList<WordDiffLine> Build(string? diffText, int maxLines)
    {
        var result = new List<WordDiffLine>();
        if (string.IsNullOrEmpty(diffText) || maxLines <= 0)
        {
            return result;
        }

        string[] lines = diffText.Split('\n');
        int count = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            if (count >= maxLines)
            {
                result.Add(new WordDiffLine(TruncationText, [new WordDiffRun(TruncationText, false)]));
                break;
            }

            string raw = StripCr(lines[i]);
            char prefix = raw.Length > 0 ? raw[0] : ' ';

            if ((prefix == '-' || prefix == '+') && i + 1 < lines.Length
                && count + 2 <= maxLines)
            {
                string next = StripCr(lines[i + 1]);
                char nextPrefix = next.Length > 0 ? next[0] : ' ';
                bool paired = prefix == '-' && nextPrefix == '+';

                if (paired)
                {
                    string oldText = raw.Length > 0 ? raw[1..] : raw;
                    string newText = next.Length > 0 ? next[1..] : next;
                    WordDiffSides sides = WordDiff.Segment(oldText, newText);

                    result.Add(new WordDiffLine(raw, RunsFor(prefix, sides.Removed)));
                    result.Add(new WordDiffLine(next, RunsFor(nextPrefix, sides.Inserted)));
                    i++;
                    count += 2;
                    continue;
                }
            }

            result.Add(new WordDiffLine(raw, [new WordDiffRun(raw, false)]));
            count++;
        }

        return result;
    }

    private static string StripCr(string line) =>
        line.EndsWith("\r", StringComparison.Ordinal) ? line[..^1] : line;

    private static IReadOnlyList<WordDiffRun> RunsFor(char prefix, IReadOnlyList<WordSeg> segments)
    {
        var runs = new List<WordDiffRun>(segments.Count + 1)
        {
            // The row prefix is punctuation, not a changed word. Keep it dim.
            new($"{prefix} ", false),
        };

        for (int i = 0; i < segments.Count; i++)
        {
            WordSeg segment = segments[i];
            string text = i == segments.Count - 1 ? segment.Text : segment.Text + " ";
            runs.Add(new WordDiffRun(text, segment.Kind != WordSegKind.Equal));
        }

        return runs;
    }
}
