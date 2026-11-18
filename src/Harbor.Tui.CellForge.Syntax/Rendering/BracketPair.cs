// Issue #2494: pure matching-bracket search over plain document lines.
// Screen-buffer free by design: the caller decides how to paint the two cells
// (DocumentView.PaintLine does that). BCL only, small stack, AOT-clean.

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Positions of a cursor bracket and its match, stored as (row, col) pairs.
/// One side stays null when the bracket is unmatched or the cursor is not on
/// a bracket at all.
/// </summary>
internal readonly record struct BracketPairResult(
    (int Row, int Col)? Open,
    (int Row, int Col)? Close)
{
    /// <summary>No bracket under the cursor and no match.</summary>
    public static BracketPairResult None => default;

    /// <summary>True when both ends of the pair were found.</summary>
    public bool IsMatch => Open.HasValue && Close.HasValue;
}

/// <summary>
/// Finds the bracket pair enclosing a document position. The walk uses a
/// stack over ()[]{} and crosses line boundaries, so nested and mixed brackets
/// resolve by type. Strings and comments are not modeled: this function only
/// sees the characters the caller supplies.
/// </summary>
internal static class BracketPair
{
    /// <summary>
    /// Returns the matching pair for the bracket at <paramref name="row"/> /
    /// <paramref name="col"/>. An opening bracket fills <see cref="BracketPairResult.Open"/>
    /// with the cursor and <see cref="BracketPairResult.Close"/> with the match;
    /// a closing bracket fills them the other way round. Out-of-range positions
    /// and non-bracket characters yield <see cref="BracketPairResult.None"/>.
    /// </summary>
    public static BracketPairResult Find(IReadOnlyList<string> lines, int row, int col)
    {
        if (lines is null || row < 0 || row >= lines.Count)
        {
            return BracketPairResult.None;
        }

        string line = lines[row] ?? string.Empty;
        if (col < 0 || col >= line.Length)
        {
            return BracketPairResult.None;
        }

        char at = line[col];
        if (IsOpen(at))
        {
            (int Row, int Col)? match = Scan(lines, row, col, at, forward: true);
            return new BracketPairResult((row, col), match);
        }

        if (IsClose(at))
        {
            (int Row, int Col)? match = Scan(lines, row, col, at, forward: false);
            return new BracketPairResult(match, (row, col));
        }

        return BracketPairResult.None;
    }

    private static (int Row, int Col)? Scan(IReadOnlyList<string> lines, int row, int col, char startBracket, bool forward)
    {
        // Entry 0 is the bracket under the cursor; it is the last one popped.
        var stack = new List<(int Row, int Col, char Bracket)>(8);
        stack.Add((row, col, startBracket));

        for (int r = row; r >= 0 && r < lines.Count; r += forward ? 1 : -1)
        {
            string text = lines[r] ?? string.Empty;
            if (forward)
            {
                int start = r == row ? col + 1 : 0;
                for (int c = start; c < text.Length; c++)
                {
                    var hit = Visit(text[c], r, c, stack, forward);
                    if (hit.HasValue)
                    {
                        return hit;
                    }
                }
            }
            else
            {
                int start = r == row ? col - 1 : text.Length - 1;
                for (int c = start; c >= 0; c--)
                {
                    var hit = Visit(text[c], r, c, stack, forward);
                    if (hit.HasValue)
                    {
                        return hit;
                    }
                }
            }
        }

        return null;
    }

    private static (int Row, int Col)? Visit(
        char ch,
        int row,
        int col,
        List<(int Row, int Col, char Bracket)> stack,
        bool forward)
    {
        if (forward ? IsOpen(ch) : IsClose(ch))
        {
            stack.Add((row, col, ch));
            return null;
        }

        if (forward ? !IsClose(ch) : !IsOpen(ch))
        {
            return null;
        }

        if (stack.Count == 0)
        {
            return null;
        }

        var top = stack[^1];
        bool pairs = forward ? Pairs(top.Bracket, ch) : Pairs(ch, top.Bracket);
        if (!pairs)
        {
            return null;
        }

        stack.RemoveAt(stack.Count - 1);
        return stack.Count == 0 ? (row, col) : null;
    }

    private static bool IsOpen(char c) => c is '(' or '[' or '{';

    private static bool IsClose(char c) => c is ')' or ']' or '}';

    private static bool Pairs(char open, char close) =>
        (open == '(' && close == ')') || (open == '[' && close == ']') || (open == '{' && close == '}');
}
