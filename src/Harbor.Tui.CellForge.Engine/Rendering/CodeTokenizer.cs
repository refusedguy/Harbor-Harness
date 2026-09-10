namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// One syntax-highlighted run inside a fenced code line: raw text plus its
/// resolved <see cref="CellStyle"/>.
/// </summary>
public readonly record struct CodeSpan(string Text, CellStyle Style);

/// <summary>
/// Lightweight Span-based code tokenizer for fenced code blocks — a
/// cell-grid port of
/// <c>apps/Harbor.App.Avalonia/Views/Controls/CodeBlock.axaml.cs</c>
/// (<c>Tokenize()</c> + <c>KeywordsFor</c>).
///
/// Rules (kept 1:1 with the Avalonia source): <c>//</c> and <c>#</c> line
/// comments, <c>/* … */</c> block comments, <c>"…"</c> / <c>'…'</c> /
/// <c>`…`</c> strings with backslash escapes, digit-led number literals,
/// identifier/keyword scan over 6 language groups (csharp, js-ts, python,
/// go, rust, sql).
///
/// Color mapping: keyword = <see cref="ChatPalette.Accent"/> + Bold,
/// string = <see cref="ChatPalette.Success"/>, comment =
/// <see cref="ChatPalette.Muted"/> (terminal tertiary), number =
/// <see cref="ChatPalette.Warning"/>; everything else is
/// <see cref="CellStyle.Plain"/>.
///
/// BCL-only, AOT-clean (no regex, no reflection, no static mutable state).
/// The scan itself is <see cref="ReadOnlySpan{T}"/>-based; strings are
/// materialized only at span boundaries (one per emitted span).
/// </summary>
public static class CodeTokenizer
{
    /// <summary>Keyword style: accent primary + bold.</summary>
    public static CellStyle KeywordStyle => new(ChatPalette.Accent, attrs: StyleAttr.Bold);

    /// <summary>String-literal style: success green.</summary>
    public static CellStyle StringStyle => new(ChatPalette.Success);

    /// <summary>Comment style: muted (terminal tertiary).</summary>
    public static CellStyle CommentStyle => new(ChatPalette.Muted);

    /// <summary>Number-literal style: warning amber.</summary>
    public static CellStyle NumberStyle => new(ChatPalette.Warning);

    private enum Lang : byte
    {
        None,
        CSharp,
        Js,
        Python,
        Go,
        Rust,
        Sql,
    }

    /// <summary>
    /// Tokenizes a whole code region in one shot (multi-line aware:
    /// an unterminated <c>/*</c> runs to the end of the input, matching
    /// the Avalonia source). Returns an empty list for empty input.
    /// </summary>
    public static List<CodeSpan> Tokenize(string code, string? language)
    {
        var spans = new List<CodeSpan>(8);
        if (string.IsNullOrEmpty(code))
        {
            return spans;
        }

        bool inBlockComment = false;
        TokenizeCore(code.AsSpan(), ClassifyLanguage(language ?? string.Empty), ref inBlockComment, spans);
        return spans;
    }

    /// <summary>
    /// Tokenizes one display line, threading multi-line <c>/* … */</c>
    /// state through <paramref name="inBlockComment"/> so fence bodies
    /// can be highlighted line-by-line (the markdown pipeline wraps long
    /// code lines, so the tokenizer sees display lines, not source lines).
    /// </summary>
    public static List<CodeSpan> TokenizeLine(ReadOnlySpan<char> line, string? language, ref bool inBlockComment)
    {
        var spans = new List<CodeSpan>(8);
        if (line.IsEmpty)
        {
            return spans;
        }

        TokenizeCore(line, ClassifyLanguage(language ?? string.Empty), ref inBlockComment, spans);
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
    public static Dictionary<int, List<CodeSpan>>? HighlightFenceBodies(IReadOnlyList<MdLine> lines)
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

    private static void TokenizeCore(ReadOnlySpan<char> code, Lang lang, ref bool inBlockComment, List<CodeSpan> outSpans)
    {
        var keywordStyle = KeywordStyle;
        var stringStyle = StringStyle;
        var commentStyle = CommentStyle;
        var numberStyle = NumberStyle;

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

                if (IsKeyword(code.Slice(i, j - i), lang))
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

    private static Lang ClassifyLanguage(ReadOnlySpan<char> language)
    {
        var lang = language.Trim();
        if (lang.IsEmpty)
        {
            return Lang.None;
        }

        if (lang.Equals("cs", StringComparison.OrdinalIgnoreCase)
            || lang.Equals("csharp", StringComparison.OrdinalIgnoreCase)
            || lang.Equals("c#", StringComparison.OrdinalIgnoreCase))
        {
            return Lang.CSharp;
        }

        if (lang.Equals("js", StringComparison.OrdinalIgnoreCase)
            || lang.Equals("javascript", StringComparison.OrdinalIgnoreCase)
            || lang.Equals("ts", StringComparison.OrdinalIgnoreCase)
            || lang.Equals("typescript", StringComparison.OrdinalIgnoreCase))
        {
            return Lang.Js;
        }

        if (lang.Equals("py", StringComparison.OrdinalIgnoreCase)
            || lang.Equals("python", StringComparison.OrdinalIgnoreCase))
        {
            return Lang.Python;
        }

        if (lang.Equals("go", StringComparison.OrdinalIgnoreCase)
            || lang.Equals("golang", StringComparison.OrdinalIgnoreCase))
        {
            return Lang.Go;
        }

        if (lang.Equals("rust", StringComparison.OrdinalIgnoreCase)
            || lang.Equals("rs", StringComparison.OrdinalIgnoreCase))
        {
            return Lang.Rust;
        }

        if (lang.Equals("sql", StringComparison.OrdinalIgnoreCase))
        {
            return Lang.Sql;
        }

        return Lang.None;
    }

    private static bool IsKeyword(ReadOnlySpan<char> word, Lang lang)
    {
        switch (lang)
        {
            case Lang.CSharp:
                switch (word)
                {
                    case "abstract": case "as": case "base": case "bool": case "break":
                    case "byte": case "case": case "catch": case "char": case "checked":
                    case "class": case "const": case "continue": case "decimal": case "default":
                    case "delegate": case "do": case "double": case "else": case "enum":
                    case "event": case "explicit": case "extern": case "false": case "finally":
                    case "fixed": case "float": case "for": case "foreach": case "goto":
                    case "if": case "implicit": case "in": case "int": case "interface":
                    case "internal": case "is": case "lock": case "long": case "namespace":
                    case "new": case "null": case "object": case "operator": case "out":
                    case "override": case "params": case "private": case "protected": case "public":
                    case "readonly": case "ref": case "return": case "sbyte": case "sealed":
                    case "short": case "sizeof": case "stackalloc": case "static": case "string":
                    case "struct": case "switch": case "this": case "throw": case "true":
                    case "try": case "typeof": case "uint": case "ulong": case "unchecked":
                    case "unsafe": case "ushort": case "using": case "var": case "virtual":
                    case "void": case "volatile": case "while": case "async": case "await":
                    case "yield": case "record": case "partial":
                        return true;
                }

                return false;

            case Lang.Js:
                switch (word)
                {
                    case "var": case "let": case "const": case "function": case "return":
                    case "if": case "else": case "for": case "while": case "do":
                    case "switch": case "case": case "break": case "continue": case "new":
                    case "this": case "typeof": case "instanceof": case "in": case "of":
                    case "class": case "extends": case "super": case "import": case "export":
                    case "from": case "default": case "try": case "catch": case "finally":
                    case "throw": case "async": case "await": case "yield": case "delete":
                    case "void": case "null": case "undefined": case "true": case "false":
                    case "interface": case "type": case "enum": case "public": case "private":
                    case "protected": case "readonly": case "static": case "get": case "set":
                    case "implements": case "namespace": case "as": case "is": case "satisfies":
                        return true;
                }

                return false;

            case Lang.Python:
                switch (word)
                {
                    case "def": case "return": case "if": case "elif": case "else":
                    case "for": case "while": case "break": case "continue": case "in":
                    case "not": case "and": case "or": case "is": case "None":
                    case "True": case "False": case "class": case "import": case "from":
                    case "as": case "try": case "except": case "finally": case "raise":
                    case "with": case "lambda": case "yield": case "global": case "nonlocal":
                    case "pass": case "assert": case "del": case "print": case "self":
                    case "cls": case "async": case "await":
                        return true;
                }

                return false;

            case Lang.Go:
                switch (word)
                {
                    case "func": case "return": case "if": case "else": case "for":
                    case "range": case "switch": case "case": case "default": case "break":
                    case "continue": case "package": case "import": case "type": case "struct":
                    case "interface": case "var": case "const": case "go": case "defer":
                    case "select": case "chan": case "map": case "nil": case "true":
                    case "false": case "make": case "new": case "len": case "cap":
                    case "append":
                        return true;
                }

                return false;

            case Lang.Rust:
                switch (word)
                {
                    case "fn": case "let": case "mut": case "const": case "static":
                    case "if": case "else": case "for": case "while": case "loop":
                    case "match": case "return": case "break": case "continue": case "struct":
                    case "enum": case "trait": case "impl": case "pub": case "use":
                    case "mod": case "as": case "in": case "ref": case "move":
                    case "async": case "await": case "self": case "Self": case "super":
                    case "crate": case "where": case "dyn": case "unsafe": case "extern":
                    case "type": case "true": case "false":
                        return true;
                }

                return false;

            case Lang.Sql:
                // The Avalonia source lists both cases with an Ordinal
                // comparer (so "Select" is NOT a keyword there either).
                switch (word)
                {
                    case "SELECT": case "select": case "FROM": case "from": case "WHERE": case "where":
                    case "INSERT": case "insert": case "UPDATE": case "update": case "DELETE": case "delete":
                    case "CREATE": case "create": case "TABLE": case "table": case "INDEX": case "index":
                    case "DROP": case "drop": case "ALTER": case "alter": case "INTO": case "into":
                    case "VALUES": case "values": case "SET": case "set": case "JOIN": case "join":
                    case "INNER": case "inner": case "LEFT": case "left": case "RIGHT": case "right":
                    case "OUTER": case "outer": case "ON": case "on": case "GROUP": case "group":
                    case "BY": case "by": case "ORDER": case "order": case "HAVING": case "having":
                    case "LIMIT": case "limit": case "OFFSET": case "offset": case "AS": case "as":
                    case "AND": case "and": case "OR": case "or": case "NOT": case "not":
                    case "NULL": case "null": case "PRIMARY": case "primary": case "KEY": case "key":
                    case "FOREIGN": case "foreign": case "REFERENCES": case "references": case "UNIQUE": case "unique":
                    case "DEFAULT": case "default": case "CASCADE": case "cascade":
                        return true;
                }

                return false;

            default:
                return false;
        }
    }

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
