using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Injectable syntax-highlight palette (issue #198). Replaces the four static
/// <c>CodeTokenizer.*Style</c> globals that read <see cref="ChatPalette"/>
/// directly and made styles untestable/unthemable in isolation:
/// <see cref="CodeSyntaxTokenizer"/> takes this struct via constructor.
/// </summary>
public readonly record struct CodeHighlightPalette(CellStyle Keyword, CellStyle String, CellStyle Comment, CellStyle Number)
{
    /// <summary>
    /// Live projection of <see cref="ChatPalette"/> — keyword = accent + bold,
    /// string = success, comment = muted, number = warning. Read per access
    /// (not cached) so theme switches propagate, exactly like the old static
    /// style properties.
    /// </summary>
    public static CodeHighlightPalette Default => new(
        new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold),
        new CellStyle(ChatPalette.Success),
        new CellStyle(ChatPalette.Muted),
        new CellStyle(ChatPalette.Warning));
}
