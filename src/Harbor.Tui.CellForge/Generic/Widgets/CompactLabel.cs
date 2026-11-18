using System.Globalization;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// The four compact labels of the badge/tag/kbd/marker family (#2371, from the
/// gpui-kit components research). A variant is a closed choice, not a plugin
/// axis: the family has exactly these members and no registration point.
/// </summary>
public enum CompactLabelVariant
{
    /// <summary>A short count pill, e.g. <c>[3]</c>.</summary>
    Badge,

    /// <summary>A keyword chip that may carry a dismiss affordance, e.g. <c>&lt;work ×&gt;</c>.</summary>
    Tag,

    /// <summary>A keyboard key cap, e.g. <c>[Ctrl+K]</c>.</summary>
    Kbd,

    /// <summary>A short status, notification or loading string, e.g. <c>● loading</c>.</summary>
    Marker,
}

/// <summary>
/// The one style contract of the compact-label family: the leading glyph, the
/// trailing glyph (a nul means "no tail") and the foreground style of the body.
/// Every variant resolves through <see cref="For"/>, so a theme switch changes
/// all four labels through <see cref="ChatPalette"/> and no widget keeps a
/// second palette.
/// </summary>
public readonly record struct CompactLabelStyle(char Lead, char Tail, CellStyle Body)
{
    /// <summary>The dismiss affordance of a tag, painted dim against its body.</summary>
    public const char DismissGlyph = '×';

    /// <summary>Resolves the glyph pair and body style of one variant.</summary>
    public static CompactLabelStyle For(CompactLabelVariant variant) => variant switch
    {
        CompactLabelVariant.Badge => new('[', ']', new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold)),
        CompactLabelVariant.Tag => new('<', '>', new CellStyle(ChatPalette.Tool)),
        CompactLabelVariant.Kbd => new('[', ']', new CellStyle(ChatPalette.Warning)),
        CompactLabelVariant.Marker => new('●', '\0', new CellStyle(ChatPalette.Success)),
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown compact-label variant."),
    };
}

/// <summary>
/// External content of a <see cref="CompactLabel"/>. The host owns the text,
/// the count and the dismiss flag; the widget keeps no hidden copy. Fields that
/// do not apply to the chosen variant are normalized away in the constructor,
/// so a badge never carries a dismiss flag and a tag never carries a count.
/// </summary>
public readonly record struct CompactLabelState
{
    /// <summary>Which member of the family to paint.</summary>
    public CompactLabelVariant Variant { get; }

    /// <summary>Body text. Null is normalized to the empty string.</summary>
    public string Text { get; }

    /// <summary>Count of a badge; null for every other variant.</summary>
    public int? Count { get; }

    /// <summary>Whether a tag shows its dismiss affordance; false for every other variant.</summary>
    public bool Dismissible { get; }

    /// <summary>Creates a state, normalizing fields that the variant does not use.</summary>
    public CompactLabelState(CompactLabelVariant variant, string? text, int? count = null, bool dismissible = false)
    {
        Variant = variant;
        Text = text ?? string.Empty;
        Count = variant == CompactLabelVariant.Badge ? count : null;
        Dismissible = dismissible && variant == CompactLabelVariant.Tag;
    }

    /// <summary>Copy with new body text; the remaining fields stay put.</summary>
    public CompactLabelState WithText(string? text) => new(Variant, text, Count, Dismissible);

    /// <summary>Copy with a new variant; inapplicable fields are normalized again.</summary>
    public CompactLabelState WithVariant(CompactLabelVariant variant) => new(variant, Text, Count, Dismissible);
}

/// <summary>
/// Compact, domain-free label primitive. It knows no session, task, agent or
/// chat block: it paints the content the host supplies and adds no extension
/// point. Badge shows a count, tag can be dismissed, kbd caps a hotkey, and
/// marker leads a short status string.
/// </summary>
public sealed class CompactLabel
{
    /// <summary>Current externally supplied content.</summary>
    public CompactLabelState State { get; private set; }

    /// <summary>Creates a label over a host-owned state snapshot.</summary>
    public CompactLabel(CompactLabelState state) => State = state;

    /// <summary>Creates a label with one variant and its body text.</summary>
    public CompactLabel(CompactLabelVariant variant, string? text = null, int? count = null, bool dismissible = false)
        : this(new CompactLabelState(variant, text, count, dismissible))
    {
    }

    /// <summary>Replaces the external content. Normalizes through <see cref="CompactLabelState"/>.</summary>
    public void SetState(CompactLabelState state) => State = state;

    /// <summary>Resolves the style contract of the current variant.</summary>
    public CompactLabelStyle Style => CompactLabelStyle.For(State.Variant);

    /// <summary>
    /// Paints one compact row inside <paramref name="rect"/>. The label is
    /// clipped to the row end, so a narrow rect shows only its leading cells.
    /// A dismissible tag repaints its <see cref="CompactLabelStyle.DismissGlyph"/>
    /// cell dim; the rest of the label uses the variant's body style.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        CompactLabelStyle style = Style;
        string label = Compose(style);
        if (label.Length == 0)
        {
            return;
        }

        buffer.SetText(rect.X, rect.Y, label.AsSpan(), style.Body);

        if (State.Variant == CompactLabelVariant.Tag && State.Dismissible)
        {
            // Lead glyph, body text, one space, then the dismiss glyph.
            int glyphX = rect.X + 1 + State.Text.Length + 1;
            if (glyphX < rect.X + Math.Min(label.Length, rect.Width))
            {
                buffer.SetText(glyphX, rect.Y, CompactLabelStyle.DismissGlyph.ToString().AsSpan(), ChatPalette.Dim);
            }
        }
    }

    private string Compose(CompactLabelStyle style)
    {
        switch (State.Variant)
        {
            case CompactLabelVariant.Badge:
                string count = State.Count?.ToString(CultureInfo.InvariantCulture) ?? State.Text;
                return style.Lead + count + style.Tail;
            case CompactLabelVariant.Tag:
                return style.Lead + State.Text + (State.Dismissible ? " " + CompactLabelStyle.DismissGlyph : string.Empty) + style.Tail;
            case CompactLabelVariant.Kbd:
                return style.Lead + State.Text + style.Tail;
            case CompactLabelVariant.Marker:
                return style.Lead + " " + State.Text;
            default:
                return State.Text;
        }
    }
}
