using System.Text;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Two-budget tool-output collapse, ported from opencode
/// <c>packages/tui/src/util/collapse-tool-output.ts</c> (O11, epic #1155,
/// issue #1180): every preview is bounded by <b>both</b> a line budget and a
/// char (code-point) budget. Shell (<c>bash</c>) cards keep the command to 2
/// lines and show the output <b>tail</b> (last lines behind a
/// <c>"(N earlier lines)"</c> label) instead of the head, so the exit status
/// stays visible. The <c>Overflow</c> flag is the expand affordance — the
/// feed Enter/click gesture reveals the full body.
/// BCL-only, AOT-clean, allocation-light (no split per call: span line
/// walks plus one substring for the visible head/tail, rune slices for
/// truncation). Mirrors the TS exactly: head truncation appends <c>"…"</c>
/// inline to the last visible line, then the char budget cuts to
/// <c>maxChars - 1 + "…"</c>; the tail keeps the last
/// <c>maxLines - 1</c> lines under the label.
/// </summary>
internal static class ToolOutputCollapse
{
    public readonly record struct CollapseResult(string Preview, bool Overflow);

    public readonly record struct ShellCollapseResult(string Command, string Output, bool Overflow);

    private readonly record struct CommandCollapse(string Output, bool Overflow, int Lines);

    /// <summary>Head collapse: first <paramref name="maxLines"/> lines, then char budget.</summary>
    public static CollapseResult Collapse(string output, int maxLines, int maxChars)
    {
        output ??= string.Empty;
        int totalLines = CountLines(output);
        if (!IsOverflow(output, maxLines, maxChars))
        {
            return new CollapseResult(output, Overflow: false);
        }

        int take = Math.Max(0, Math.Min(maxLines, totalLines));
        // First `take` lines re-joined by '\n' == the prefix ending at the
        // take-th newline (Split+Join round-trips exactly, no normalization).
        string head = take <= 0
            ? string.Empty
            : take >= totalLines
                ? output
                : output[..TakeEnd(output, take)];
        if (totalLines > maxLines && take > 0)
        {
            head += "…";
        }

        if (CountRunes(head) > maxChars)
        {
            return new CollapseResult(TruncateRunes(head, Math.Max(0, maxChars - 1)) + "…", Overflow: true);
        }

        return new CollapseResult(head, Overflow: true);
    }

    /// <summary>
    /// The overflow predicate of <see cref="Collapse" />, on a span: true when
    /// the output busts the line budget OR the char budget.
    /// </summary>
    /// <remarks>
    /// #1139: the paint/measure path asked <see cref="Collapse" /> for its
    /// <c>Overflow</c> flag and threw the preview away — but reaching the flag
    /// meant handing the method a <see cref="string" />, and the callers were
    /// trimming the tool output into a fresh copy of the WHOLE body first (the
    /// output of a <c>bash</c> card is routinely tens of kilobytes, and the
    /// card is re-measured and re-painted on every frame). This predicate
    /// answers the same question over a span: nothing is copied, nothing is
    /// materialized, and the flag is exactly the one <see cref="Collapse" />
    /// reports — <c>Collapse</c> itself defers to this method, so the two can
    /// never disagree.
    /// </remarks>
    internal static bool IsOverflow(ReadOnlySpan<char> output, int maxLines, int maxChars)
    {
        int totalLines = CountLines(output);
        return totalLines > maxLines || CountRunes(output) > maxChars;
    }

    /// <summary>Tail collapse for shell output: label + last lines.</summary>
    public static string CollapseTail(string output, int maxLines, int maxChars)
    {
        output ??= string.Empty;
        return CollapseTailCore(output.AsSpan(), maxLines, maxChars, output);
    }

    /// <summary>
    /// Tail collapse over a span: the tail is materialized, the rest of the
    /// output never is (#1139).
    /// </summary>
    /// <remarks>
    /// The string overload hands the whole output in and gets the whole output
    /// back when it fits — no copy. The span overload cannot do that, so it
    /// materializes only what it returns: the label, the visible tail lines and
    /// the rune-capped tail. Everything past the tail is never touched, which
    /// is the whole point of the paint path calling it.
    /// </remarks>
    internal static string CollapseTail(ReadOnlySpan<char> output, int maxLines, int maxChars)
        => CollapseTailCore(output, maxLines, maxChars, whole: null);

    private static string CollapseTailCore(ReadOnlySpan<char> output, int maxLines, int maxChars, string? whole)
    {
        int totalLines = CountLines(output);
        if (totalLines <= maxLines && CountRunes(output) <= maxChars)
        {
            return whole ?? output.ToString();
        }

        int count = Math.Max(1, totalLines - Math.Max(0, maxLines - 1));
        string label = count == 1 ? "(1 earlier line)" : $"({count} earlier lines)";
        if (maxLines <= 1)
        {
            return label;
        }

        int keep = Math.Min(maxLines - 1, totalLines);
        // Last `keep` lines re-joined by '\n' == the suffix starting at that line.
        ReadOnlySpan<char> tail = keep >= totalLines ? output : output[LineStart(output, totalLines - keep)..];
        int available = maxChars - CountRunes(label) - 1;
        if (available <= 0)
        {
            return label;
        }

        ReadOnlySpan<char> tailRunes = tail;
        if (CountRunes(tail) > available)
        {
            tailRunes = TakeLastRunes(tail, available);
        }

        return string.Concat(label, "\n", tailRunes.ToString());
    }

    /// <summary><c>Split('\n').Length</c> without the array: every '\n'
    /// starts a new line, so even <c>""</c> is one line.</summary>
    private static int CountLines(ReadOnlySpan<char> text)
    {
        int lines = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    /// <summary>Index of the take-th <c>'\n'</c> (end of the <c>take</c>-line
    /// head prefix). Call only when <c>take</c> is strictly below the line
    /// count, so the terminator exists.</summary>
    private static int TakeEnd(ReadOnlySpan<char> text, int take)
    {
        int seen = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                seen++;
                if (seen == take)
                {
                    return i;
                }
            }
        }

        return text.Length;
    }

    /// <summary>Start offset of 0-based line <paramref name="index"/>
    /// (just past the <c>index</c>-th <c>'\n'</c>). Call only with
    /// <c>0 &lt; index &lt; lines</c>.</summary>
    private static int LineStart(ReadOnlySpan<char> text, int index)
    {
        int seen = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                seen++;
                if (seen == index)
                {
                    return i + 1;
                }
            }
        }

        return 0;
    }

    /// <summary>Shell collapse: command capped to 2 lines, output shown as tail.</summary>
    public static ShellCollapseResult CollapseShell(string command, string output, int maxLines, int maxChars)
    {
        command ??= string.Empty;
        output ??= string.Empty;
        if (command.Length == 0)
        {
            var collapsed = Collapse(output, maxLines, maxChars);
            return new ShellCollapseResult(
                command,
                collapsed.Overflow ? CollapseTail(output, maxLines, maxChars) : output,
                collapsed.Overflow);
        }

        int commandLines = Math.Min(2, maxLines);
        int lineChars = Math.Max(1, maxChars / Math.Max(1, maxLines));
        var cmd = CollapseShellCommand(command, commandLines, lineChars);
        if (output.Length == 0)
        {
            return new ShellCollapseResult(cmd.Output, output, cmd.Overflow);
        }

        int lines = Math.Max(1, maxLines - cmd.Lines - 1);
        int chars = Math.Max(1, maxChars - CountRunes(cmd.Output) - 2);
        var outCollapsed = Collapse(output, lines, chars);
        return new ShellCollapseResult(
            cmd.Output,
            outCollapsed.Overflow ? CollapseTail(output, lines, chars) : output,
            cmd.Overflow || outCollapsed.Overflow);
    }

    private static CommandCollapse CollapseShellCommand(string input, int maxLines, int lineWidth)
    {
        var visible = new StringBuilder(input.Length + 1);
        int lines = 1;
        int width = 0;
        bool overflow = false;
        int i = 0;
        while (i < input.Length)
        {
            if (input[i] == '\n')
            {
                if (lines >= maxLines)
                {
                    overflow = true;
                    break;
                }

                visible.Append('\n');
                lines++;
                width = 0;
                i++;
                continue;
            }

            int runeLen = 1;
            if (char.IsHighSurrogate(input[i]) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
            {
                runeLen = 2;
            }

            int w = 1;
            if (width + w > lineWidth)
            {
                if (lines >= maxLines)
                {
                    overflow = true;
                    break;
                }

                lines++;
                width = 0;
            }

            visible.Append(input, i, runeLen);
            width += w;
            i += runeLen;
        }

        if (!overflow)
        {
            return new CommandCollapse(input, Overflow: false, lines);
        }

        string text = visible.ToString();
        if (width >= lineWidth && text.Length > 0 && !text.EndsWith('\n'))
        {
            // Drop the last rune so the "…" fits the line width (mirrors the TS pop).
            int end = text.Length;
            int start = end - 1;
            if (start > 0 && char.IsLowSurrogate(text[end - 1]) && char.IsHighSurrogate(text[start - 1]))
            {
                start--;
            }

            text = text[..start];
        }

        return new CommandCollapse(text + "…", Overflow: true, lines);
    }

    internal static int CountRunes(ReadOnlySpan<char> text)
    {
        int count = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }

            i++;
            count++;
        }

        return count;
    }

    private static string TruncateRunes(string text, int maxRunes)
    {
        if (maxRunes <= 0)
        {
            return string.Empty;
        }

        int count = 0;
        int i = 0;
        while (i < text.Length)
        {
            int len = 1;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                len = 2;
            }

            if (count >= maxRunes)
            {
                break;
            }

            count++;
            i += len;
        }

        return text[..i];
    }

    private static ReadOnlySpan<char> TakeLastRunes(ReadOnlySpan<char> text, int maxRunes)
    {
        if (maxRunes <= 0)
        {
            return ReadOnlySpan<char>.Empty;
        }

        int count = 0;
        int i = text.Length;
        while (i > 0)
        {
            int len = 1;
            if (i >= 2 && char.IsLowSurrogate(text[i - 1]) && char.IsHighSurrogate(text[i - 2]))
            {
                len = 2;
            }

            if (count >= maxRunes)
            {
                break;
            }

            count++;
            i -= len;
        }

        return text[i..];
    }
}
