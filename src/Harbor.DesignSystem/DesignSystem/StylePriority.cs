namespace Harbor.Ui.Framework.Rendering;

/// <summary>
/// Semantic visual states a widget can hold. States are flags because a
/// control can be both focused and selected at once. The declaration order is
/// the fixed priority order used by <see cref="StylePriority.Resolve"/>: a
/// later state overrides an earlier one. Disabled is deliberately NOT a state
/// here — it is a separate final layer that always wins, so a disabled control
/// reads as disabled regardless of the states it also carries.
///
/// Modelled after gpui-kit <c>crates/base/src/state_style.rs</c>
/// (<c>resolve_style</c>, commit 249a963): value states are layered in a fixed
/// order, then disabled is resolved last. The order here mirrors gpui-kit's
/// documented value-state order (pressed/checked → selected → focused).
/// </summary>
[Flags]
public enum VisualState : byte
{
    /// <summary>No semantic state — the ordinary (instance) style applies.</summary>
    None = 0,

    /// <summary>Pointer is over the control (lowest-priority semantic state).</summary>
    Hovered = 1 << 0,

    /// <summary>Control is pressed / checked / otherwise engaged.</summary>
    Active = 1 << 1,

    /// <summary>Control is selected / picked.</summary>
    Selected = 1 << 2,

    /// <summary>Control owns keyboard focus (highest-priority semantic state).</summary>
    Focused = 1 << 3,
}

/// <summary>
/// The styles a widget resolves against. <see cref="Ordinary"/> is the
/// always-present baseline (the gpui-kit instance style). Every semantic slot
/// is optional: <see langword="null"/> means "this state does not override the
/// style", the equivalent of omitting a state from gpui-kit's active-state
/// iterator. <see cref="Disabled"/> is applied last when the caller marks the
/// control disabled; when it is null a disabled control keeps its semantic
/// style (no separate disabled appearance).
/// </summary>
/// <param name="Ordinary">Baseline style when no state is active.</param>
/// <param name="Hovered">Override while <see cref="VisualState.Hovered"/>.</param>
/// <param name="Active">Override while <see cref="VisualState.Active"/>.</param>
/// <param name="Selected">Override while <see cref="VisualState.Selected"/>.</param>
/// <param name="Focused">Override while <see cref="VisualState.Focused"/>.</param>
/// <param name="Disabled">Final override while disabled.</param>
public readonly record struct StyleCatalog(
    CellStyle Ordinary,
    CellStyle? Hovered = null,
    CellStyle? Active = null,
    CellStyle? Selected = null,
    CellStyle? Focused = null,
    CellStyle? Disabled = null);

/// <summary>
/// The single priority resolver for CellForge / DesignSystem widget styles:
/// ordinary → semantic states → disabled. Every state-driven widget routes its
/// style choice through this method so the ordering cannot drift apart between
/// widgets (the defect this resolver exists to remove).
///
/// Pure and allocation-free — all arguments and the result are structs, safe
/// on the render hot path and under NativeAOT.
/// </summary>
public static class StylePriority
{
    /// <summary>
    /// Resolves the final style for a widget from its catalog, the semantic
    /// states currently active, and a disabled flag.
    /// </summary>
    /// <param name="catalog">The widget's style catalog.</param>
    /// <param name="state">Active semantic states; combine with <c>|</c>.</param>
    /// <param name="disabled">When true, <see cref="StyleCatalog.Disabled"/> is applied last and wins.</param>
    /// <returns>The resolved style.</returns>
    public static CellStyle Resolve(in StyleCatalog catalog, VisualState state, bool disabled)
    {
        CellStyle resolved = catalog.Ordinary;

        // Fixed order: a later state overrides an earlier one. A null slot is
        // simply not applied, so states only override what the catalog supplies.
        if ((state & VisualState.Hovered) != 0 && catalog.Hovered is { } hovered)
        {
            resolved = hovered;
        }

        if ((state & VisualState.Active) != 0 && catalog.Active is { } active)
        {
            resolved = active;
        }

        if ((state & VisualState.Selected) != 0 && catalog.Selected is { } selected)
        {
            resolved = selected;
        }

        if ((state & VisualState.Focused) != 0 && catalog.Focused is { } focused)
        {
            resolved = focused;
        }

        // Disabled is always resolved last, so it wins over every semantic
        // state. This is the whole point of the priority contract.
        if (disabled && catalog.Disabled is { } disabledStyle)
        {
            resolved = disabledStyle;
        }

        return resolved;
    }
}
