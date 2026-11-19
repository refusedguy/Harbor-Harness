// #3087 (S6): declarative descriptions for the BCL-only widget catalog.
// One immutable, record-based description per ready widget. No Harbor imports,
// no reflection: the catalog dispatches on the concrete record type at paint
// time, so the whole surface stays AOT-clean and the description is data a
// caller can build, store and compare.

using System.Text;

namespace Harbor.Tui.CellForge.Catalog;

using Harbor.Tui.CellForge.Rendering;

/// <summary>
/// The five ready widgets of the engine catalog. The enum is the stable id a
/// declarative surface (a screen spec, a DSL or a CLI) can name without
/// binding to a painter.
/// </summary>
public enum WidgetKind : byte
{
    /// <summary>Wrapped by construction: text lines clipped to the area.</summary>
    Paragraph = 0,

    /// <summary>One row of a repeated glyph, clipped to the area.</summary>
    Rule = 1,

    /// <summary>Label plus a proportional filled/empty bar.</summary>
    Gauge = 2,

    /// <summary>One selectable row per item, with a leading marker.</summary>
    List = 3,

    /// <summary>Caption on the left, value right-aligned in the same row.</summary>
    Status = 4,
}

/// <summary>
/// Base of every declarative widget description. A spec is a value: keeping
/// selection or ratio outside the painter is what makes a widget reusable
/// across frames without an element-state store.
/// </summary>
public abstract record WidgetSpec
{
    /// <summary>The catalog entry this description paints.</summary>
    public abstract WidgetKind Kind { get; }
}

/// <summary>Text lines separated by <c>\n</c>, clipped to the painted area.</summary>
public sealed record ParagraphSpec(string Text, CellStyle Style = default) : WidgetSpec
{
    /// <inheritdoc />
    public override WidgetKind Kind => WidgetKind.Paragraph;
}

/// <summary>One horizontal row of <paramref name="Glyph"/>.</summary>
public sealed record RuleSpec(Rune Glyph, CellStyle Style = default) : WidgetSpec
{
    /// <inheritdoc />
    public override WidgetKind Kind => WidgetKind.Rule;

    /// <summary>The catalog's plain dashed rule.</summary>
    public static RuleSpec Dashed() => new(new Rune('-'));
}

/// <summary>A label followed by a bar whose filled run is <paramref name="Ratio"/> of the bar width.</summary>
public sealed record GaugeSpec(string Label, double Ratio, Rune FilledGlyph, Rune EmptyGlyph, CellStyle Style = default) : WidgetSpec
{
    /// <inheritdoc />
    public override WidgetKind Kind => WidgetKind.Gauge;

    /// <summary>Builds the catalog's <c>#</c>/<c>-</c> gauge.</summary>
    public static GaugeSpec Create(string label, double ratio) =>
        new(label, ratio, new Rune('#'), new Rune('-'));
}

/// <summary>One row per item; the selected row leads with <paramref name="Marker"/>.</summary>
public sealed record ListSpec(IReadOnlyList<string> Items, int Selected, Rune Marker, CellStyle Style = default) : WidgetSpec
{
    /// <inheritdoc />
    public override WidgetKind Kind => WidgetKind.List;

    /// <summary>Builds the catalog's <c>&gt;</c>-marked list.</summary>
    public static ListSpec Create(IReadOnlyList<string> items, int selected = 0) =>
        new(items, selected, new Rune('>'));
}

/// <summary>A caption and a right-aligned value in one row.</summary>
public sealed record StatusSpec(string Caption, string Value, CellStyle Style = default) : WidgetSpec
{
    /// <inheritdoc />
    public override WidgetKind Kind => WidgetKind.Status;
}
