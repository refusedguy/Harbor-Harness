using System.Text;

namespace Harbor.Ui.Framework.Rendering.Widgets;

/// <summary>How one row of a line-level diff relates the two texts.</summary>
public enum LineDiffRowKind : byte
{
    /// <summary>The same line at the same place in both texts.</summary>
    Unchanged,

    /// <summary>Present in the new text only.</summary>
    Added,

    /// <summary>Present in the old text only.</summary>
    Removed,
}

/// <summary>
///     One row of a line-level diff: what happened to the line, where it lived
///     on each side, and its text. The number that does not apply to a row is
///     0 — an addition has no old-file line, a removal has no new-file one.
/// </summary>
/// <param name="Kind">What happened to this line.</param>
/// <param name="OldLineNumber">1-based line number in the old text, or 0.</param>
/// <param name="NewLineNumber">1-based line number in the new text, or 0.</param>
/// <param name="Text">The line itself, without any prefix.</param>
public readonly record struct LineDiffRow(
    LineDiffRowKind Kind,
    int OldLineNumber,
    int NewLineNumber,
    string Text);

/// <summary>
///     How one row of an aligned (side-by-side) diff reads across the two texts.
/// </summary>
public enum SideBySideRowKind : byte
{
    /// <summary>One line, identical on both sides.</summary>
    Unchanged,

    /// <summary>One line rewritten: both sides present and different.</summary>
    Modified,

    /// <summary>One line on the right only; the left side is null.</summary>
    Added,

    /// <summary>One line on the left only; the right side is null.</summary>
    Removed,
}

/// <summary>
///     One row of an aligned (side-by-side) diff, shaped for a two-column view.
///     A rewrite is ONE row carrying both sides rather than a removal followed
///     by an addition, which is what a side-by-side pane has to show for the
///     columns to line up.
/// </summary>
/// <param name="Kind">What this row reads as across the two columns.</param>
/// <param name="LineNumber">
///     The new-text line number, or the old-text one for a
///     <see cref="SideBySideRowKind.Removed" /> row (which has no new side).
/// </param>
/// <param name="OldText">Left-hand text; null exactly when <paramref name="Kind" /> is Added.</param>
/// <param name="NewText">Right-hand text; null exactly when <paramref name="Kind" /> is Removed.</param>
public readonly record struct SideBySideDiffRow(
    SideBySideRowKind Kind,
    int LineNumber,
    string? OldText,
    string? NewText);

/// <summary>
///     Line-level diff of two texts — the ONE place in Harbor that answers
///     "what changed between these two strings".
/// </summary>
/// <remarks>
/// <para>
///     Two view-models used to answer that question themselves, by walking
///     <c>left[i]</c> against <c>right[i]</c> and calling the mismatch a
///     modification. That is only right when nothing moves. Insert one line in
///     the middle of a file and every line after it shifts by one, so an index
///     diff reports the whole tail of the file as rewritten — a removal and
///     addition storm in place of the single insertion that actually happened.
///     The user is then shown a false description of their own edit. So the
///     algorithm lives here, in the headless layer, and the view-models only
///     project what it returns.
/// </para>
/// <para>
///     This is the LINE-level counterpart of <see cref="WordDiff" />, which
///     segments a single delete/add pair into words. The two are deliberately
///     independent: a line that is genuinely added or removed has no pair to
///     segment, and a word-level pass over a 2000-line rewrite tells the reader
///     nothing about the shape of the change.
/// </para>
/// <para>
///     <b>Why Myers and not a full LCS table.</b> The obvious LCS table is
///     sized by the product of both line counts, and a 5000-line file needs 100
///     MB of it — on a path fed by untrusted tool output. Myers' O(ND) search
///     costs time proportional to the SIZE OF THE CHANGE instead, and memory
///     linear in the file: the single-line insertion this type exists to report
///     correctly is found in one pass over the two texts, and even a whole-file
///     rewrite is a bounded constant rather than a quadratic allocation. The
///     trace it backtracks through is capped by <see cref="MaxEditDistance" />;
///     past that the changed region has no anchors and is reported as a
///     removal run followed by an addition run — the coarse picture, in linear
///     time. Common prefix and common suffix are matched before the search
///     starts, so what remains is the region that actually differs.
/// </para>
/// <para>
///     Pure, deterministic and allocation-bounded: the only allocations are the
///     line arrays, the bounded trace and the result rows. Comparison is
///     <see cref="StringComparison.Ordinal" /> throughout, so the result is
///     culture-independent and identical on every run.
/// </para>
/// </remarks>
public static class LineDiff
{
    /// <summary>
    ///     Largest edit distance searched exactly, in changed lines.
    /// </summary>
    /// <remarks>
    ///     Myers' backtrack needs the per-step furthest-reaching row of every
    ///     step, so the trace is bounded by this constant: at most
    ///     (<see cref="MaxEditDistance" /> + 1) rows of 2 ×
    ///     <see cref="MaxEditDistance" /> + 3 ints — about 2 MB at 512, and
    ///     released with the trace. 512 changed lines is far beyond any single
    ///     hunk, so an ordinary edit is exact; a whole-file rewrite is the case
    ///     that degrades, and degrading it to "this region was replaced" is
    ///     both cheaper and more honest than a quadratic table.
    /// </remarks>
    public const int MaxEditDistance = 512;

    /// <summary>
    ///     The line-level diff of <paramref name="before" /> against
    ///     <paramref name="after" />, ordered, with every input line accounted
    ///     for exactly once. Insertions in the middle stay insertions: the lines
    ///     around them are matched, not shifted.
    /// </summary>
    /// <param name="before">Old text. Null is an empty text.</param>
    /// <param name="after">New text. Null is an empty text.</param>
    public static IReadOnlyList<LineDiffRow> Compute(string? before, string? after)
    {
        string[] oldLines = SplitLines(before);
        string[] newLines = SplitLines(after);

        // The common prefix and suffix are their own minimal edit script and
        // cost nothing to match, so the search below is sized by the region
        // that actually differs rather than by the size of the file. This is
        // what keeps an ordinary one-line edit in a 5000-line file exact.
        int prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length
            && Same(oldLines[prefix], newLines[prefix]))
        {
            prefix++;
        }

        int oldLast = oldLines.Length - 1;
        int newLast = newLines.Length - 1;
        int suffix = 0;
        while (oldLast - suffix >= prefix && newLast - suffix >= prefix
            && Same(oldLines[oldLast - suffix], newLines[newLast - suffix]))
        {
            suffix++;
        }

        int oldCoreEnd = oldLast - suffix;
        int newCoreEnd = newLast - suffix;

        var rows = new List<LineDiffRow>(oldLines.Length + newLines.Length);
        int oldNo = 1;
        int newNo = 1;

        for (int i = 0; i < prefix; i++)
        {
            rows.Add(new LineDiffRow(LineDiffRowKind.Unchanged, oldNo++, newNo++, oldLines[i]));
        }

        var matches = MatchedLines(oldLines, prefix, oldCoreEnd, newLines, prefix, newCoreEnd);
        int oldCursor = prefix;
        int newCursor = prefix;

        for (int m = 0; m < matches.Count; m++)
        {
            (int oldIndex, int newIndex) = matches[m];

            // The alignment comes out of the search and is ascending by
            // construction, but a row is emitted per matched line and this runs
            // on untrusted tool output. A pair that is not strictly ahead of
            // the cursors is dropped, which leaves those lines to be reported as
            // removals and additions below — a coarser answer, not a crash.
            if (oldIndex < oldCursor || oldIndex > oldCoreEnd
                || newIndex < newCursor || newIndex > newCoreEnd)
            {
                continue;
            }

            while (oldCursor < oldIndex)
            {
                rows.Add(new LineDiffRow(LineDiffRowKind.Removed, oldNo++, 0, oldLines[oldCursor++]));
            }

            while (newCursor < newIndex)
            {
                rows.Add(new LineDiffRow(LineDiffRowKind.Added, 0, newNo++, newLines[newCursor++]));
            }

            rows.Add(new LineDiffRow(LineDiffRowKind.Unchanged, oldNo++, newNo++, oldLines[oldCursor]));
            oldCursor++;
            newCursor++;
        }

        while (oldCursor <= oldCoreEnd)
        {
            rows.Add(new LineDiffRow(LineDiffRowKind.Removed, oldNo++, 0, oldLines[oldCursor++]));
        }

        while (newCursor <= newCoreEnd)
        {
            rows.Add(new LineDiffRow(LineDiffRowKind.Added, 0, newNo++, newLines[newCursor++]));
        }

        for (int i = 0; i < suffix; i++)
        {
            rows.Add(new LineDiffRow(LineDiffRowKind.Unchanged, oldNo++, newNo++, oldLines[oldCoreEnd + 1 + i]));
        }

        return rows;
    }

    /// <summary>
    ///     The same diff, aligned for a two-column view: a run of removals
    ///     immediately followed by a run of additions is a rewrite of that
    ///     region, and is paired off in order so it reads as
    ///     <see cref="SideBySideRowKind.Modified" /> rows carrying both sides.
    ///     Leftovers stay pure removals or additions.
    /// </summary>
    /// <remarks>
    ///     A pure insertion has no removal run at all, so it survives as a single
    ///     <see cref="SideBySideRowKind.Added" /> row with a null left side —
    ///     which is the whole point of the pairing, and the reason this cannot
    ///     be done by index.
    /// </remarks>
    public static IReadOnlyList<SideBySideDiffRow> ComputeSideBySide(string? before, string? after)
    {
        var rows = Compute(before, after);
        var aligned = new List<SideBySideDiffRow>(rows.Count);

        int i = 0;
        while (i < rows.Count)
        {
            if (rows[i].Kind != LineDiffRowKind.Removed)
            {
                LineDiffRow row = rows[i++];
                aligned.Add(row.Kind == LineDiffRowKind.Added
                    ? new SideBySideDiffRow(SideBySideRowKind.Added, row.NewLineNumber, null, row.Text)
                    : new SideBySideDiffRow(SideBySideRowKind.Unchanged, row.NewLineNumber, row.Text, row.Text));
                continue;
            }

            int deleteEnd = i;
            while (deleteEnd < rows.Count && rows[deleteEnd].Kind == LineDiffRowKind.Removed)
            {
                deleteEnd++;
            }

            int addEnd = deleteEnd;
            while (addEnd < rows.Count && rows[addEnd].Kind == LineDiffRowKind.Added)
            {
                addEnd++;
            }

            int pairs = Math.Min(deleteEnd - i, addEnd - deleteEnd);
            for (int k = 0; k < pairs; k++)
            {
                LineDiffRow removed = rows[i + k];
                LineDiffRow added = rows[deleteEnd + k];
                aligned.Add(new SideBySideDiffRow(
                    SideBySideRowKind.Modified, added.NewLineNumber, removed.Text, added.Text));
            }

            for (int k = pairs; k < deleteEnd - i; k++)
            {
                LineDiffRow removed = rows[i + k];
                aligned.Add(new SideBySideDiffRow(
                    SideBySideRowKind.Removed, removed.OldLineNumber, removed.Text, null));
            }

            for (int k = pairs; k < addEnd - deleteEnd; k++)
            {
                LineDiffRow added = rows[deleteEnd + k];
                aligned.Add(new SideBySideDiffRow(
                    SideBySideRowKind.Added, added.NewLineNumber, null, added.Text));
            }

            i = addEnd;
        }

        return aligned;
    }

    /// <summary>
    ///     Renders rows in the context-diff block the core already speaks:
    ///     unchanged lines prefixed with two spaces, removals with
    ///     <c>"- "</c>, additions with <c>"+ "</c>. The same shape
    ///     <c>EditTool.GenerateContextDiff</c> emits, so what a view-model
    ///     computes is recognisable by <see cref="TryParseContextBlock" /> and
    ///     by the diff editors that read the format.
    /// </summary>
    public static string ToUnifiedText(IReadOnlyList<LineDiffRow> rows)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            sb.Append(rows[i].Kind switch
            {
                LineDiffRowKind.Unchanged => "  ",
                LineDiffRowKind.Removed => "- ",
                _ => "+ ",
            });
            sb.AppendLine(rows[i].Text);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Structurally locates the diff block inside a text and parses it —
    ///     the reader counterpart of <see cref="ToUnifiedText" />.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     Recognition is by the format's own row prefixes, never by a substring
    ///     search. That distinction is the whole fix: a tool result that merely
    ///     CONTAINS <c>"diff"</c>, <c>"---"</c>, <c>"+++"</c> or <c>"@@"</c>
    ///     somewhere in its prose is not a diff, and matching those characters
    ///     anywhere in a line is how a passing remark about a separator came to
    ///     be rendered as a diff. Here a row is <c>"  "</c>, <c>"- "</c> or
    ///     <c>"+ "</c> at the START of a line and a block is a maximal run of
    ///     such lines, so <c>"--- section ---"</c> and <c>"+++ and that is
    ///     fine"</c> are prose and stay prose.
    /// </para>
    /// <para>
    ///     A run is only a block if it carries at least one removal or addition;
    ///     a run of pure context rows is an indented paragraph. The longest such
    ///     run wins, earliest on a tie, so the result does not depend on scan
    ///     order.
    /// </para>
    /// <para>
    ///     The block format carries no line numbers, so the parsed rows are
    ///     numbered within the block. The residual ambiguity is the format's
    ///     own: a two-space-indented bullet list is spelled exactly like a run
    ///     of removals, and no reader can tell them apart. It is still strictly
    ///     narrower than the substring match it replaces.
    /// </para>
    /// </remarks>
    /// <param name="text">Text to scan, typically a whole tool result.</param>
    /// <param name="rows">The parsed block, or an empty list when there is none.</param>
    /// <returns>True when a diff block was found.</returns>
    public static bool TryParseContextBlock(string? text, out IReadOnlyList<LineDiffRow> rows)
    {
        rows = [];
        string[] lines = SplitLines(text);

        int bestStart = -1;
        int bestEnd = -1;
        int bestChanges = 0;
        int runStart = -1;
        int runChanges = 0;

        // The sentinel iteration past the last line closes a run that ends at
        // end-of-text, so a block on the final line is not missed.
        for (int i = 0; i <= lines.Length; i++)
        {
            char sign = i < lines.Length ? RowSign(lines[i]) : '\0';
            if (sign != '\0')
            {
                if (runStart < 0)
                {
                    runStart = i;
                    runChanges = 0;
                }

                if (sign != ' ')
                {
                    runChanges++;
                }

                continue;
            }

            if (runStart >= 0 && runChanges > bestChanges)
            {
                bestStart = runStart;
                bestEnd = i;
                bestChanges = runChanges;
            }

            runStart = -1;
            runChanges = 0;
        }

        if (bestChanges == 0)
        {
            return false;
        }

        var parsed = new List<LineDiffRow>(bestEnd - bestStart);
        int oldNo = 1;
        int newNo = 1;

        for (int i = bestStart; i < bestEnd; i++)
        {
            string body = lines[i][2..];
            switch (RowSign(lines[i]))
            {
                case ' ':
                    parsed.Add(new LineDiffRow(LineDiffRowKind.Unchanged, oldNo++, newNo++, body));
                    break;
                case '-':
                    parsed.Add(new LineDiffRow(LineDiffRowKind.Removed, oldNo++, 0, body));
                    break;
                default:
                    parsed.Add(new LineDiffRow(LineDiffRowKind.Added, 0, newNo++, body));
                    break;
            }
        }

        rows = parsed;
        return true;
    }

    /// <summary>
    ///     Matched (old, new) index pairs of a minimal edit script over
    ///     <c>oldLines[oldLo..oldHi]</c> and <c>newLines[newLo..newHi]</c>,
    ///     ascending, and absolute. Empty when the region is empty or its edit
    ///     distance exceeds <see cref="MaxEditDistance" />, which reads as "this
    ///     region has no anchors" and yields a removal run plus an addition run.
    /// </summary>
    private static List<(int Old, int New)> MatchedLines(
        string[] oldLines, int oldLo, int oldHi,
        string[] newLines, int newLo, int newHi)
    {
        int n = oldHi - oldLo + 1;
        int m = newHi - newLo + 1;
        if (n <= 0 || m <= 0)
        {
            return [];
        }

        int limit = Math.Min(n + m, MaxEditDistance);
        int offset = limit + 1;
        int width = 2 * limit + 3;

        // furthest[k] is how far along the old text the search reached on
        // diagonal k, so furthest[k] - k is the matching new-text position.
        var furthest = new int[width];
        var trace = new List<int[]>(limit + 1);

        for (int d = 0; d <= limit; d++)
        {
            trace.Add((int[])furthest.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                // Step down the diagonal, or one to the right; ties go to the
                // insertion, which keeps the path stable.
                int x = k == -d || (k != d && furthest[k - 1 + offset] < furthest[k + 1 + offset])
                    ? furthest[k + 1 + offset]
                    : furthest[k - 1 + offset] + 1;
                int y = x - k;

                while (x < n && y < m && Same(oldLines[oldLo + x], newLines[newLo + y]))
                {
                    x++;
                    y++;
                }

                furthest[k + offset] = x;
                if (x >= n && y >= m)
                {
                    return Backtrack(trace, oldLo, newLo, d, offset, x, y);
                }
            }
        }

        return [];
    }

    /// <summary>
    ///     Walks the recorded search back from the endpoint it reached at step
    ///     <paramref name="d" /> to (0, 0), emitting one pair per diagonal step
    ///     — every step that matched, which is the whole of the script's
    ///     alignment.
    /// </summary>
    /// <param name="trace">The recorded forward search, one row per edit step.</param>
    /// <param name="oldLo">Old-text start of the region being matched.</param>
    /// <param name="newLo">New-text start of the region being matched.</param>
    /// <param name="d">The edit distance the forward search reached.</param>
    /// <param name="offset">Value subtracted from a trace row to recover its absolute index.</param>
    /// <param name="x">Old-text position the forward search finished on.</param>
    /// <param name="y">New-text position the forward search finished on.</param>
    private static List<(int Old, int New)> Backtrack(
        List<int[]> trace,
        int oldLo,
        int newLo,
        int d,
        int offset,
        int x,
        int y)
    {
        var path = new List<(int Old, int New)>();

        for (int step = d; step > 0; step--)
        {
            // Same tie-break as the forward pass, so the path taken back is the
            // one that was taken out.
            int k = x - y;
            int previousK =
                k == -step || (k != step && trace[step][k - 1 + offset] < trace[step][k + 1 + offset])
                    ? k + 1
                    : k - 1;
            int previousX = trace[step][previousK + offset];
            int previousY = previousX - previousK;

            // Everything between the two furthest-reaching points was a snake of
            // matched lines, so it emits one pair per step taken.
            while (x > previousX && y > previousY)
            {
                path.Add((oldLo + x - 1, newLo + y - 1));
                x--;
                y--;
            }

            x = previousX;
            y = previousY;
        }

        // Whatever is left was matched before the first edit.
        while (x > 0 && y > 0)
        {
            path.Add((oldLo + x - 1, newLo + y - 1));
            x--;
            y--;
        }

        path.Reverse();
        return path;
    }

    /// <summary>
    ///     The row prefix of a context-block line, or <c>'\0'</c> when the line
    ///     is not one. Only the FIRST TWO characters are read, so a line is a
    ///     row because of how it starts, never because of what it contains.
    /// </summary>
    private static char RowSign(string line) =>
        line.StartsWith("  ", StringComparison.Ordinal) ? ' '
            : line.StartsWith("- ", StringComparison.Ordinal) ? '-'
            : line.StartsWith("+ ", StringComparison.Ordinal) ? '+'
            : '\0';

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static string[] SplitLines(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
