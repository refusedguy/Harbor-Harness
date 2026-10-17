using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

// #436: spans flow into Rendering painters (AssistantMarkdownBlock), so the
// palette speaks the Rendering style vocabulary through UIR even though this
// file shares the engine's namespace (whose verbatim CellStyle port would
// otherwise capture the bare names).
using UIR = Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Injectable syntax-highlight palette (issue #198). Replaces the four static
/// <c>CodeTokenizer.*Style</c> globals that read <see cref="ChatPalette"/>
/// directly and made styles untestable/unthemable in isolation:
/// <see cref="CodeSyntaxTokenizer"/> takes this struct via constructor.
/// </summary>
public readonly record struct CodeHighlightPalette(UIR.CellStyle Keyword, UIR.CellStyle String, UIR.CellStyle Comment, UIR.CellStyle Number)
{
    /// <summary>
    /// Live projection of <see cref="ChatPalette"/> — keyword = accent + bold,
    /// string = success, comment = muted, number = warning. Read per access
    /// (not cached) so theme switches propagate, exactly like the old static
    /// style properties.
    /// </summary>
    public static CodeHighlightPalette Default => new(
        new UIR.CellStyle(ChatPalette.Accent, attrs: UIR.StyleAttr.Bold),
        new UIR.CellStyle(ChatPalette.Success),
        new UIR.CellStyle(ChatPalette.Muted),
        new UIR.CellStyle(ChatPalette.Warning));
}
