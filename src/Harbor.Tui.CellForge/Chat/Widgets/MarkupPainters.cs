using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// The two renderers for one <see cref="MarkupKind" /> — the cell wireframe of
/// the overlay and the baked PNG — plus the foreground used when unselected.
/// Held by the frozen <see cref="MarkupPainters" /> registry so the two media
/// cannot drift apart (issue #1496). Assembly-internal strategy, not a plugin
/// extension axis (#555).
/// </summary>
internal sealed class MarkupPainter(
    CellStyle unselectedStyle,
    Action<ScreenBuffer, Rect, NormalizedPoint, NormalizedPoint, string, CellStyle> paintOverlay,
    Action<MarkupPngBaker.RgbaImage, MarkupAnnotation> paintPng)
{
    public CellStyle UnselectedStyle { get; } = unselectedStyle;

    /// <summary>
    /// Paints the cell wireframe. Committed annotations and drafts share this
    /// path: a draft passes an empty <c>text</c>, which the text primitive
    /// treats as a no-op.
    /// </summary>
    public void PaintOverlay(ScreenBuffer buffer, Rect image, NormalizedPoint from, NormalizedPoint to, string text, CellStyle style) =>
        paintOverlay(buffer, image, from, to, text, style);

    /// <summary>Paints the annotation onto decoded PNG pixels.</summary>
    public void PaintPng(MarkupPngBaker.RgbaImage image, MarkupAnnotation item) => paintPng(image, item);
}

/// <summary>
/// Frozen registry keyed by <see cref="MarkupKind" />. Single dispatch point
/// for the three former parallel switches (<c>PaintAnnotation</c>,
/// <c>PaintDraft</c>, <c>MarkupPngBaker.Draw</c>). Adding a kind is one entry
/// here pointing at its two existing primitives; the overlay and the baker
/// need no further edit.
/// </summary>
internal static class MarkupPainters
{
    private static readonly IReadOnlyDictionary<MarkupKind, MarkupPainter> Registry =
        new Dictionary<MarkupKind, MarkupPainter>
        {
            [MarkupKind.Arrow] = new(
                new(ChatPalette.Text),
                (b, img, a, z, _, s) => MarkupOverlay.PaintArrow(b, img, a, z, s),
                (img, item) => MarkupPngBaker.DrawArrow(img, item)),
            [MarkupKind.Rectangle] = new(
                new(ChatPalette.Text),
                (b, img, a, z, _, s) => MarkupOverlay.PaintRect(b, img, a, z, s),
                (img, item) => MarkupPngBaker.DrawRect(img, item)),
            [MarkupKind.Text] = new(
                ChatPalette.ToolArgs,
                (b, img, a, _, text, s) => MarkupOverlay.PaintText(b, img, a, text, s),
                (img, item) => MarkupPngBaker.DrawLabel(img, item)),
        };

    /// <summary>Painter for <paramref name="kind" />; text is the fallback for an unknown value.</summary>
    public static MarkupPainter For(MarkupKind kind) =>
        Registry.TryGetValue(kind, out var painter) ? painter : Registry[MarkupKind.Text];
}
