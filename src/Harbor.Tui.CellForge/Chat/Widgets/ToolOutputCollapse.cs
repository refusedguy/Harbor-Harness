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
/// BCL-only, AOT-clean, allocation-light (one split per call, rune slices for
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
        string[] lines = output.Split('\n');
        if (lines.Length <= maxLines && CountRunes(output) <= maxChars)
        {
            return new CollapseResult(output, Overflow: false);
        }

        int take = Math.Max(0, Math.Min(maxLines, lines.Length));
        string[] visible = new string[take];
        Array.Copy(lines, visible, take);
        if (lines.Length > maxLines && visible.Length > 0)
        {
            visible[visible.Length - 1] += "…";
        }

        string preview = string.Join("\n", visible);
        if (CountRunes(preview) > maxChars)
        {
            return new CollapseResult(TruncateRunes(preview, Math.Max(0, maxChars - 1)) + "…", Overflow: true);
        }

        return new CollapseResult(preview, Overflow: true);
    }

    /// <summary>Tail collapse for shell output: label + last lines.</summary>
    public static string CollapseTail(string output, int maxLines, int maxChars)
    {
        output ??= string.Empty;
        string[] lines = output.Split('\n');
        if (lines.Length <= maxLines && CountRunes(output) <= maxChars)
        {
            return output;
        }

        int count = Math.Max(1, lines.Length - Math.Max(0, maxLines - 1));
        string label = count == 1 ? "(1 earlier line)" : $"({count} earlier lines)";
        if (maxLines <= 1)
        {
            return label;
        }

        int keep = Math.Min(maxLines - 1, lines.Length);
        string tail = string.Join("\n", lines, lines.Length - keep, keep);
        int available = maxChars - CountRunes(label) - 1;
        if (available <= 0)
        {
            return label;
        }

        string tailRunes = tail;
        int tailCount = CountRunes(tail);
        if (tailCount > available)
        {
            tailRunes = TakeLastRunes(tail, available);
        }

        return label + "\n" + tailRunes;
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

    internal static int CountRunes(string text)
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

    private static string TakeLastRunes(string text, int maxRunes)
    {
        if (maxRunes <= 0)
        {
            return string.Empty;
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
