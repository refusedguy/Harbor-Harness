using System.Globalization;

namespace Harbor.Abstractions.Models;

/// <summary>
///     The ONE shape of a session's money cell — the "<c>$0.0123</c>" a surface
///     paints beside the token counts. Issue #682.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this assembly, and why not beside the canon it came from.</b>
///         The rule used to live in <c>StatusBarText</c>
///         (<c>Harbor.Ui.Framework.State</c>), chosen because that was the only
///         project the projection layer and the view-model layer both referenced.
///         By the time #682 was picked up it had six writers, because <b>two of
///         its callers cannot reach it at all</b>:
///     </para>
///     <list type="bullet">
///         <item>
///             <c>Harbor.Ui.Framework.Rendering</c> is a BCL-only leaf; its own
///             XML doc at <c>StatusViewModel.SetUsage</c> records that the State
///             edge "would be circular".
///         </item>
///         <item>
///             <c>LayerDependencyTests.TuiAbstractions_ReferencesOnlyAbstractions</c>
///             forbids <c>Harbor.Terminal.Abstractions</c> from referencing any
///             Harbor assembly but <c>Harbor.Abstractions</c>.
///         </item>
///     </list>
///     <para>
///         A canon two of its five callers are architecturally forbidden to reach
///         is not a canon; it is the first copy wearing the costume of one. This
///         is the same wall <see cref="ContextUsage" /> (#75, #651) was placed in
///         the contracts layer to get around — that class is documented as "the
///         single definition of a status number shared by every surface,
///         including the legacy status-bar view model", for exactly this reason.
///         The money cell's text now sits beside it.
///     </para>
///     <para>
///         <b>What this type is NOT.</b> It is the cell's <i>shape</i> and
///         nothing else. Whether a cell is shown at all is a placement decision
///         that differs per surface and belongs to them:
///         <c>StatusBarText.CostCell</c> hides the cell at zero (the #457 rule
///         "no data ⇒ no cell"), while the positional one-line terminal status
///         cannot drop a slot without shifting the rest of the line. Keeping the
///         shape here and the placement up there is what lets those two differ
///         without either of them re-deriving the number.
///     </para>
///     <para>
///         <b>Two conventions exist and this type does not arbitrate.</b>
///         Convention A is this one — fixed four decimals, <c>"$0.0000"</c> /
///         <c>"$12.5000"</c>. Convention B is <c>"0.####"</c>, trailing zeros
///         trimmed, and lives in <c>StatusViewModel.SetUsage</c> because the
///         narrow sidebar has better use for the width than four always-on
///         zeros. Collapsing B into A (or the reverse) changes what a person sees
///         and has golden-frame and screenshot-hash blast radius, so it is a
///         separate decision; <c>MoneyCellSingleHomeRules</c> freezes the count
///         in the meantime rather than choosing. Pure BCL, allocation-free,
///         AOT-safe.
///     </para>
/// </remarks>
public static class UsdCell
{
    /// <summary>
    ///     A USD cost as one status cell reads it: a literal <c>$</c> plus four
    ///     fixed decimals in the invariant culture (<c>"$0.0123"</c>).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Negative input clamps to zero — a cell has no room for a minus
    ///         sign, and a negative cost is a bug upstream, not a value to render.
    ///     </para>
    ///     <para>
    ///         The <c>$</c> is written literally rather than taken from
    ///         <c>"C4"</c> so the cell never renders a culture currency symbol
    ///         (<c>¤</c>) on a non-en-US host. That is the whole reason the
    ///         invariant culture is pinned here and not left to the caller: a
    ///         ru-RU host must read <c>$0,0000</c> nowhere, in any surface, ever.
    ///     </para>
    /// </remarks>
    public static string ToUsd(decimal costUsd) =>
        "$" + (costUsd < 0 ? 0m : costUsd).ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>
    ///     The cell shown when the model publishes no price. An em dash, not a
    ///     zero and not silence: it is the one glyph that cannot be misread as
    ///     an amount. A <c>"$0.0000"</c> there claims the session was free,
    ///     which is true for a local Ollama and false for a paid provider whose
    ///     catalogue entry carries no rates, and nothing downstream can tell
    ///     those apart (#653).
    /// </summary>
    public const string Unpriced = "—";
}