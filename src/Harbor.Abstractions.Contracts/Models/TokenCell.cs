using System.Globalization;

namespace Harbor.Abstractions.Models;

/// <summary>
///     The ONE string shape of the "0.# + k/M" token figure — the compact
///     cell the CellForge status widget and the per-message header paint.
///     Issue #788.
/// </summary>
/// <remarks>
///     <para>
///         Before this type the same expression lived in two assemblies:
///         <c>StatusViewModel.FormatCount</c> (Harbor.Ui.Framework.Rendering)
///         and <c>SideBarView.FormatTokens</c> (Harbor.Tui.CellForge). Both
///         produced byte-identical text for every input, so the duplication
///         was drift, not a difference of reader, precision or placement.
///         Both now call this one method; neither surface's output changed.
///     </para>
///     <para>
///         <b>This type does not choose the notation.</b> A session's token
///         spend still has other conventions in the tree: <c>F1 + K/M</c> in
///         <c>StatusBarText</c> and <c>PanelRows</c>, raw on the plain/ANSI
///         status line, and <c>N0</c> in the Avalonia views. ADR-010 §6 leaves
///         the choice to the owner; this is only the single home for the one
///         convention two surfaces already shared. The allocation-free
///         <c>SideBarView.FormatTokensTo</c> span path stays where it is: the
///         sidebar repaints every frame and cannot allocate a string there.
///     </para>
///     <para>
///         The invariant culture is pinned so a non-en-US host never renders
///         a comma decimal separator into a cell that golden frames pin.
///     </para>
/// </remarks>
public static class TokenCell
{
    /// <summary>
    ///     999 → «999», 12 345 → «12.3k», 1 234 567 → «1.2M». Sub-thousand and
    ///     negative values are written raw, exactly as both callers did.
    /// </summary>
    public static string ToCompact(long tokens) => tokens switch
    {
        >= 1_000_000 => (tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (tokens / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };
}
