using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Markdown;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Instantiable syntax highlighter for fenced code blocks (issue #198): a
/// cell-grid port of <c>apps/Harbor.App.Avalonia/Views/Controls/CodeBlock.axaml.cs</c>
/// (<c>Tokenize()</c> + <c>KeywordsFor</c>), split out of the old static
/// <see cref="CodeTokenizer"/> god-object.
///
/// Rules (kept 1:1 with the Avalonia source): <c>//</c> and <c>#</c> line
/// comments, <c>/* … */</c> block comments, <c>"…"</c> / <c>'…'</c> /
/// <c>`…`</c> strings with backslash escapes, digit-led number literals,
/// identifier/keyword scan against the injected
/// <see cref="LanguageSupportRegistry"/>.
///
/// The scan itself is <see cref="ReadOnlySpan{T}"/>-based; strings are
/// materialized only at span boundaries (one per emitted span).
/// BCL-only, AOT-clean (no regex, no reflection, no static mutable state).
/// </summary>
public sealed class CodeSyntaxTokenizer
{
    /// <summary>
    /// Shared default: live <see cref="ChatPalette"/> projection +
    /// <see cref="LanguageSupportRegistry.Default"/>. Used by the
    /// <see cref="CodeTokenizer"/> compat façade.
    /// </summary>
    public static readonly CodeSyntaxTokenizer Default = new();

    private readonly CodeHighlightPalette? _palette;
    private readonly LanguageSupportRegistry _registry;

    /// <summary>
    /// Builds a tokenizer. A <c>null</c> palette means the live
    /// <see cref="CodeHighlightPalette.Default"/> projection (theme-aware);
    /// pass an explicit palette to pin styles (tests, custom themes).
    /// </summary>
    public CodeSyntaxTokenizer(CodeHighlightPalette? palette = null, LanguageSupportRegistry? registry = null)
    {
        _palette = palette;
        _registry = registry ?? LanguageSupportRegistry.Default;
    }

    /// <summary>Active palette: the injected one, or the live default projection.</summary>
    public CodeHighlightPalette Palette => _palette ?? CodeHighlightPalette.Default;

    /// <summary>Registry backing keyword resolution.</summary>
    public LanguageSupportRegistry Registry => _registry;

    /// <summary>Keyword style (palette snapshot at call time).</summary>
    public CellStyle KeywordStyle => Palette.Keyword;

    /// <summary>String-literal style (palette snapshot at call time).</summary>
    public CellStyle StringStyle => Palette.String;

    /// <summary>Comment style (palette snapshot at call time).</summary>
    public CellStyle CommentStyle => Palette.Comment;

    /// <summary>Number-literal style (palette snapshot at call time).</summary>
    public CellStyle NumberStyle => Palette.Number;

    /// <summary>
    /// Tokenizes a whole code region in one shot (multi-line aware:
    /// an unterminated <c>/*</c> runs to the end of the input, matching
    /// the Avalonia source). Returns an empty list for empty input.
    /// </summary>
    public List<CodeSpan> Tokenize(string code, string? language)
    {
        var spans = new List<CodeSpan>(8);
        if (string.IsNullOrEmpty(code))
        {
            return spans;
        }

        bool inBlockComment = false;
        TokenizeCore(code.AsSpan(), _registry.Resolve(language), ref inBlockComment, spans);
        return spans;
    }

    /// <summary>
    /// Tokenizes one display line, threading multi-line <c>/* … */</c>
    /// state through <paramref name="inBlockComment"/> so fence bodies
    /// can be highlighted line-by-line (the markdown pipeline wraps long
    /// code lines, so the tokenizer sees display lines, not source lines).
    /// </summary>
    public List<CodeSpan> TokenizeLine(ReadOnlySpan<char> line, string? language, ref bool inBlockComment)
    {
        var spans = new List<CodeSpan>(8);
        if (line.IsEmpty)
        {
            return spans;
        }

        TokenizeCore(line, _registry.Resolve(language), ref inBlockComment, spans);
        return spans;
    }

    /// <summary>
    /// Builds a display-line overlay with highlighted spans for every
    /// fence-body line in an already-rendered <see cref="MdLine"/> list.
    /// Returns <c>null</c> when there is nothing to highlight (no fences,
    /// unknown language, or bodies with no tokens). Lines whose
    /// tokenization is all-plain are omitted — plain renders identically
    /// through the normal <c>MdStyle.Normal</c> path.
    /// </summary>
    public Dictionary<int, List<CodeSpan>>? HighlightFenceBodies(IReadOnlyList<MdLine> lines)
    {
        Dictionary<int, List<CodeSpan>>? map = null;
        bool inFence = false;
        string? language = null;
        bool inBlockComment = false;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (IsFenceMarker(line))
            {
                if (!inFence)
                {
                    language = ParseFenceLanguage(FenceMarkerText(line));
                    inBlockComment = false;
                    inFence = true;
                }
                else
                {
                    inFence = false;
                    language = null;
                }

                continue;
            }

            if (!inFence)
            {
                continue;
            }

            string text = ConcatSpans(line);
            if (text.Length == 0)
            {
                continue;
            }

            var spans = TokenizeLine(text.AsSpan(), language, ref inBlockComment);
            if (IsAllPlain(spans))
            {
                continue;
            }

            (map ??= new Dictionary<int, List<CodeSpan>>()).Add(i, spans);
        }

        return map;
    }

    private void TokenizeCore(ReadOnlySpan<char> code, ILanguageSupport? language, ref bool inBlockComment, List<CodeSpan> outSpans)
    {
        var palette = Palette;
        var keywordStyle = palette.Keyword;
        var stringStyle = palette.String;
        var commentStyle = palette.Comment;
        var numberStyle = palette.Number;

        int n = code.Length;
        int i = 0;
        int plainStart = 0;

        // Continuation of a /* … */ opened on an earlier display line.
        if (inBlockComment)
        {
            int close = -1;
            for (int k = 0; k + 1 < n; k++)
            {
                if (code[k] == '*' && code[k + 1] == '/')
                {
                    close = k;
                    break;
                }
            }

            if (close < 0)
            {
                outSpans.Add(new CodeSpan(code.ToString(), commentStyle));
                return;
            }

            outSpans.Add(new CodeSpan(code.Slice(0, close + 2).ToString(), commentStyle));
            i = close + 2;
            plainStart = i;
            inBlockComment = false;
        }

        while (i < n)
        {
            char c = code[i];

            // Line comment: // or # (see IsShebang note below).
            if ((c == '/' && i + 1 < n && code[i + 1] == '/') || c == '#')
            {
                if (i > plainStart)
                {
                    outSpans.Add(new CodeSpan(code.Slice(plainStart, i - plainStart).ToString(), CellStyle.Plain));
                }

                int j = i;
                while (j < n && code[j] != '\n' && code[j] != '\r')
                {
                    j++;
                }

                outSpans.Add(new CodeSpan(code.Slice(i, j - i).ToString(), commentStyle));
                i = j;
                plainStart = j;
                continue;
            }

            // Block comment: /* … */ (unterminated runs to end of input and,
            // in line mode, continues onto the next display line).
            if (c == '/' && i + 1 < n && code[i + 1] == '*')
            {
                if (i > plainStart)
                {
                    outSpans.Add(new CodeSpan(code.Slice(plainStart, i - plainStart).ToString(), CellStyle.Plain));
                }

                int j = i + 2;
                while (j + 1 < n && !(code[j] == '*' && code[j + 1] == '/'))
                {
                    j++;
                }

                if (j + 1 < n)
                {
                    j += 2;
                }
                else
                {
                    j = n;
                    inBlockComment = true;
                }

                outSpans.Add(new CodeSpan(code.Slice(i, j - i).ToString(), commentStyle));
                i = j;
                plainStart = j;
                continue;
            }

            // String literal: " … " or ' … ' or ` … ` (backslash escapes).
            if (c == '"' || c == '\'' || c == '`')
            {
                if (i > plainStart)
                {
                    outSpans.Add(new CodeSpan(code.Slice(plainStart, i - plainStart).ToString(), CellStyle.Plain));
                }

                char quote = c;
                int j = i + 1;
                while (j < n && code[j] != quote)
                {
                    if (code[j] == '\\' && j + 1 < n)
                    {
                        j++;
                    }

                    j++;
                }

                if (j < n)
                {
                    j++; // include closing quote
                }

                outSpans.Add(new CodeSpan(code.Slice(i, j - i).ToString(), stringStyle));
                i = j;
                plainStart = j;
                continue;
            }

            // Number literal: digit-led and starting a fresh plain run
            // (i == plainStart mirrors `current.Length == 0` in the
            // Avalonia source, so "abc123" stays plain).
            if (char.IsDigit(c) && i == plainStart)
            {
                int j = i;
                while (j < n && (char.IsDigit(code[j]) || code[j] == '.' || code[j] == 'x' || code[j] == 'X'
                                 || (code[j] >= 'a' && code[j] <= 'f') || (code[j] >= 'A' && code[j] <= 'F')))
                {
                    j++;
                }

                outSpans.Add(new CodeSpan(code.Slice(i, j - i).ToString(), numberStyle));
                i = j;
                plainStart = j;
                continue;
            }

            // Identifier / keyword.
            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < n && (char.IsLetterOrDigit(code[j]) || code[j] == '_'))
                {
                    j++;
                }

                if (language is not null && language.IsKeyword(code.Slice(i, j - i)))
                {
                    if (i > plainStart)
                    {
                        outSpans.Add(new CodeSpan(code.Slice(plainStart, i - plainStart).ToString(), CellStyle.Plain));
                    }

                    outSpans.Add(new CodeSpan(code.Slice(i, j - i).ToString(), keywordStyle));
                    plainStart = j;
                }

                i = j;
                continue;
            }

            // Default accumulation (emitted lazily at the next boundary).
            i++;
        }

        if (n > plainStart)
        {
            outSpans.Add(new CodeSpan(code.Slice(plainStart, n - plainStart).ToString(), CellStyle.Plain));
        }
    }

    // NOTE (faithful-port quirk): the Avalonia source gates '#' comments on
    // !IsShebang(code, i), but its IsShebang is a constant-false stub (every
    // branch returns false), so '#' ALWAYS lexes as a line comment —
    // including a '#!/usr/bin/env …' shebang at offset 0. This port keeps
    // that behavior 1:1 instead of "fixing" it.

    private static bool IsFenceMarker(MdLine line)
    {
        var spans = line.Spans;
        if (spans.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < spans.Count; i++)
        {
            if (spans[i].Style != MdStyle.Fence)
            {
                return false;
            }
        }

        return true;
    }

    private static string FenceMarkerText(MdLine line)
    {
        if (line.Spans.Count == 1)
        {
            return line.Spans[0].Text;
        }

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < line.Spans.Count; i++)
        {
            sb.Append(line.Spans[i].Text);
        }

        return sb.ToString();
    }

    private static string ParseFenceLanguage(string marker)
    {
        var span = marker.AsSpan();
        if (span.StartsWith("```"))
        {
            span = span.Slice(3);
        }

        span = span.Trim();
        int end = span.IndexOfAny([' ', '\t']);
        if (end >= 0)
        {
            span = span.Slice(0, end);
        }

        return span.ToString();
    }

    private static string ConcatSpans(MdLine line)
    {
        var spans = line.Spans;
        if (spans.Count == 1)
        {
            return spans[0].Text;
        }

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < spans.Count; i++)
        {
            sb.Append(spans[i].Text);
        }

        return sb.ToString();
    }

    private static bool IsAllPlain(List<CodeSpan> spans)
    {
        for (int i = 0; i < spans.Count; i++)
        {
            if (!spans[i].Style.IsPlain)
            {
                return false;
            }
        }

        return true;
    }
}
