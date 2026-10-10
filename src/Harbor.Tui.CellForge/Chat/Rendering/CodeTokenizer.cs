using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Markdown;

// #436: spans paint into Rendering grids, so span styles are pinned to the
// Rendering vocabulary through UIR (same capture as CodeHighlightPalette).
using UIR = Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// One syntax-highlighted run inside a fenced code line: raw text plus its
/// resolved <see cref="CellStyle"/> (Rendering vocabulary — spans paint into
/// Rendering grids; see CodeHighlightPalette for why the pin is explicit).
/// </summary>
public readonly record struct CodeSpan(string Text, UIR.CellStyle Style);

/// <summary>
/// Back-compat static façade over <see cref="CodeSyntaxTokenizer.Default"/>
/// (issue #198): keeps the old <c>CodeTokenizer.Tokenize / TokenizeLine /
/// HighlightFenceBodies / *Style</c> surface byte-identical so existing
/// callers and golden tests are untouched. New code should instantiate
/// <see cref="CodeSyntaxTokenizer"/> with an explicit
/// <see cref="CodeHighlightPalette"/> and/or
/// <see cref="LanguageSupportRegistry"/> instead.
/// </summary>
public static class CodeTokenizer
{
    /// <summary>Keyword style: accent primary + bold.</summary>
    public static UIR.CellStyle KeywordStyle => CodeSyntaxTokenizer.Default.KeywordStyle;

    /// <summary>String-literal style: success green.</summary>
    public static UIR.CellStyle StringStyle => CodeSyntaxTokenizer.Default.StringStyle;

    /// <summary>Comment style: muted (terminal tertiary).</summary>
    public static UIR.CellStyle CommentStyle => CodeSyntaxTokenizer.Default.CommentStyle;

    /// <summary>Number-literal style: warning amber.</summary>
    public static UIR.CellStyle NumberStyle => CodeSyntaxTokenizer.Default.NumberStyle;

    /// <summary>
    /// Tokenizes a whole code region in one shot (multi-line aware).
    /// See <see cref="CodeSyntaxTokenizer.Tokenize"/>.
    /// </summary>
    public static List<CodeSpan> Tokenize(string code, string? language)
        => CodeSyntaxTokenizer.Default.Tokenize(code, language);

    /// <summary>
    /// Tokenizes one display line, threading <c>/* … */</c> state through
    /// <paramref name="inBlockComment"/>. See
    /// <see cref="CodeSyntaxTokenizer.TokenizeLine"/>.
    /// </summary>
    public static List<CodeSpan> TokenizeLine(ReadOnlySpan<char> line, string? language, ref bool inBlockComment)
        => CodeSyntaxTokenizer.Default.TokenizeLine(line, language, ref inBlockComment);

    /// <summary>
    /// Builds a display-line overlay with highlighted spans for every
    /// fence-body line. See
    /// <see cref="CodeSyntaxTokenizer.HighlightFenceBodies"/>.
    /// </summary>
    public static Dictionary<int, List<CodeSpan>>? HighlightFenceBodies(IReadOnlyList<MdLine> lines)
        => CodeSyntaxTokenizer.Default.HighlightFenceBodies(lines);
}
