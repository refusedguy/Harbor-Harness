// SourceCommentBlanker.cs — the comment stripper the architecture rules read
// the tree through, in a form whose cost is proportional to the text it reads.
//
// WHY THIS FILE EXISTS
// --------------------
// `SourceScan.StripComments` used to blank a block comment with
//
//     var blank = new char[match.Length];
//     for (int i = 0; i < match.Length; i++)
//         blank[i] = match.Value[i] == '\n' ? '\n' : ' ';
//     return new string(blank);
//
// and `Match.Value` is a PROPERTY that returns `Text.Substring(index, length)`
// — a NEW string on every read. So the loop above allocated one string of the
// match's length PER CHARACTER of the match: a block comment of L characters
// cost L² characters of garbage.
//
// Measured over the repository's own tree, that is 12.82 GB of garbage for ONE
// pass over `tests/` — and 11.40 GB of it comes from ONE block comment: the
// 75 497-character XML doc comment at the top of
// `tests/Harbor.Architecture.Tests/ProcessEnvIsolationRule.cs`. The quadratic
// term is Σ(2·L²) over the block comments of the tree, and one 75 KB comment
// is worth more than the other 113 together.
//
// THE CONTRACT, WHICH DID NOT CHANGE
// ----------------------------------
// The bytes this produces are the bytes the old implementation produced, and
// the rules read line numbers out of them. So the contract is stated in terms
// of the OLD implementation, not in terms of what a comment stripper "should"
// do:
//
//   1. A block comment `/* … */` becomes spaces, one per character, with every
//      NEWLINE inside it kept. The length is unchanged, so every line number
//      downstream still points at the real source line.
//   2. A line comment `// …` becomes ONE space — the whole rest of the line,
//      not one space per character. This is the one place the output SHRINKS,
//      and the old implementation shrank it the same way.
//   3. Neither phase knows about string literals. `"http://x"` loses its tail
//      to the line-comment phase, and a `/*` inside a literal opens a block
//      comment. That is how the old implementation behaved, and the rules were
//      written against that behaviour; changing it would move verdicts.
//   4. The first `/*` wins, and it is closed by the FIRST `*/` after it. An
//      unclosed `/*` does not swallow the file: the scan moves on and looks
//      for the next `/*`.
//
// The equivalence proof lives in `CommentStripperEquivalenceTests.cs`: the
// whole live tree, byte for byte, against an oracle that is the old regular
// expressions with the quadratic read replaced by a linear one.

namespace Harbor.Architecture.Tests;

/// <summary>
///     Blanks out C-style comments in a source text: block comments become
///     spaces, line comments become one space, and every newline survives so
///     line numbers keep pointing at the real source line.
/// </summary>
internal static class SourceCommentBlanker
{
    /// <summary>
    ///     Blanks the comments of <paramref name="source" />.
    /// </summary>
    /// <param name="source">The text to blank.</param>
    /// <returns>The blanked text.</returns>
    internal static string Blank(string source)
    {
        int length = source.Length;
        if (length == 0)
        {
            return string.Empty;
        }

        // One buffer, used for both phases. The block phase writes IN PLACE
        // (a blank has the same length as the text it replaces), and the line
        // phase COMPACTS IN PLACE (the write cursor never passes the read
        // cursor, because a line comment only ever removes characters). So the
        // whole strip costs one array and one string — 4 bytes per input
        // character — instead of the old implementation's quadratic term.
        char[] buffer = source.ToCharArray();

        BlankBlockComments(buffer);
        int written = BlankLineComments(buffer);

        return new string(buffer, 0, written);
    }

    /// <summary>
    ///     Blanks every block comment in <paramref name="buffer" />, in place.
    /// </summary>
    /// <param name="buffer">The text to blank; modified in place.</param>
    private static void BlankBlockComments(char[] buffer)
    {
        int length = buffer.Length;
        int i = 0;

        while (i < length - 1)
        {
            if (buffer[i] != '/' || buffer[i + 1] != '*')
            {
                i++;
                continue;
            }

            int close = IndexOfBlockClose(buffer, i + 2);
            if (close < 0)
            {
                // An unclosed `/*` does not swallow the rest of the file: the
                // scan moves one character on and looks for the next `/*`.
                // This is what the old regular expression did, and a stripper
                // that DID swallow the file would blank every rule's subject.
                i++;
                continue;
            }

            // The blank covers the whole match — the `/*`, the body and the
            // `*/` — and keeps every newline inside it.
            for (int k = i; k < close + 2; k++)
            {
                buffer[k] = buffer[k] == '\n' ? '\n' : ' ';
            }

            i = close + 2;
        }
    }

    /// <summary>
    ///     The index of the `*` of the first `*/` at or after
    ///     <paramref name="from" />, or -1 when there is none.
    /// </summary>
    /// <param name="buffer">The text to search.</param>
    /// <param name="from">Where the search starts.</param>
    private static int IndexOfBlockClose(char[] buffer, int from)
    {
        ReadOnlySpan<char> tail = buffer.AsSpan(from);
        int at = tail.IndexOf("*/".AsSpan());
        return at < 0 ? -1 : from + at;
    }

    /// <summary>
    ///     Replaces every line comment with one space, compacting the buffer.
    /// </summary>
    /// <param name="buffer">The text to compact; modified in place.</param>
    /// <returns>How many characters the result has.</returns>
    private static int BlankLineComments(char[] buffer)
    {
        int length = buffer.Length;
        int read = 0;
        int write = 0;

        while (read < length)
        {
            if (buffer[read] == '/' && read + 1 < length && buffer[read + 1] == '/')
            {
                // The comment runs to the end of the line, and the whole of it
                // becomes ONE space. The newline itself is not part of the
                // comment and is copied by the next iteration.
                int end = read;
                while (end < length && buffer[end] != '\n')
                {
                    end++;
                }

                buffer[write++] = ' ';
                read = end;
                continue;
            }

            buffer[write++] = buffer[read++];
        }

        return write;
    }
}
