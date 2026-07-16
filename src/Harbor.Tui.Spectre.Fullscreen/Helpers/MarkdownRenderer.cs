using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Harbor.Tui.Spectre.Fullscreen.Helpers;

/// <summary>
/// Markdown rendering helpers — converts markdown text to Spectre Console markup lines.
/// Single responsibility: text → markup transformation.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly Regex BoldRegex = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex ItalicRegex = new(@"\*(.+?)\*", RegexOptions.Compiled);
    private static readonly Regex CodeRegex = new(@"`(.+?)`", RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new(@"\[(.+?)\]\(.+?\)", RegexOptions.Compiled);
    private static readonly Regex NumberedListRegex = new(@"^(\d+)\. (.*)", RegexOptions.Compiled);

    /// <summary>
    /// Format assistant content with basic markdown: code blocks, bold, italic, headers, lists.
    /// </summary>
    public static List<string> FormatToList(string content, int maxWidth)
    {
        var lines = content.Replace("\r", "").Split('\n');
        var result = new List<string>(lines.Length * 2);
        bool inCodeBlock = false;
        string codeLang = string.Empty;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```"))
            {
                if (!inCodeBlock)
                {
                    inCodeBlock = true;
                    codeLang = trimmed[3..].Trim();
                    result.Add($"[bold yellow]┌─── {codeLang} ───[/]");
                }
                else
                {
                    inCodeBlock = false;
                    result.Add("[bold yellow]└──────────────[/]");
                }
                continue;
            }

            if (inCodeBlock)
            {
                foreach (var wl in WordWrap(line, maxWidth - 4))
                    result.Add($"[yellow]│[/] [silver]{Markup.Escape(wl)}[/]");
                continue;
            }

            if (trimmed.StartsWith("### "))
            {
                result.Add($"[bold white]  {Markup.Escape(trimmed[4..])}[/]");
                continue;
            }
            if (trimmed.StartsWith("## "))
            {
                result.Add($"[bold white] {Markup.Escape(trimmed[3..])}[/]");
                continue;
            }
            if (trimmed.StartsWith("# "))
            {
                result.Add($"[bold white]{Markup.Escape(trimmed[2..])}[/]");
                continue;
            }

            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                var formatted = FormatInline(trimmed[2..]);
                result.Add($"[grey]  •[/] {formatted}");
                continue;
            }

            var match = NumberedListRegex.Match(trimmed);
            if (match.Success)
            {
                var formatted = FormatInline(match.Groups[2].Value);
                result.Add($"[grey]  {match.Groups[1].Value}.[/] {formatted}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                result.Add(string.Empty);
                continue;
            }

            foreach (var wl in WordWrap(FormatInline(trimmed), maxWidth))
                result.Add(wl);
        }

        return result;
    }

    /// <summary>Format inline markdown: **bold**, *italic*, `code`, [links](url).</summary>
    public static string FormatInline(string text)
    {
        var escaped = Markup.Escape(text);
        escaped = BoldRegex.Replace(escaped, "[bold white]$1[/]");
        escaped = ItalicRegex.Replace(escaped, "[italic]$1[/]");
        escaped = CodeRegex.Replace(escaped, "[yellow]$1[/]");
        escaped = LinkRegex.Replace(escaped, "$1");
        return escaped;
    }

    /// <summary>Word-aware text wrapping — breaks at word boundaries.</summary>
    public static List<string> WordWrap(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return new List<string> { string.Empty };

        var result = new List<string>();
        var rawLines = text.Replace("\r", "").Split('\n');

        foreach (var rawLine in rawLines)
        {
            if (rawLine.Length == 0) { result.Add(string.Empty); continue; }

            if (!rawLine.Contains(' '))
            {
                for (int i = 0; i < rawLine.Length; i += maxWidth)
                    result.Add(rawLine.Substring(i, Math.Min(maxWidth, rawLine.Length - i)));
                continue;
            }

            var words = rawLine.Split(' ');
            var current = new StringBuilder();

            foreach (var word in words)
            {
                if (current.Length == 0)
                    current.Append(word);
                else if (current.Length + 1 + word.Length <= maxWidth)
                    current.Append(' ').Append(word);
                else
                {
                    result.Add(current.ToString());
                    current.Clear();
                    current.Append(word);
                }
            }

            if (current.Length > 0)
                result.Add(current.ToString());
        }

        return result;
    }
}
