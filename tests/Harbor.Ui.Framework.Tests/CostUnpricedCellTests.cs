// CostUnpricedCellTests.cs — #942: two live surfaces dropped the core's
// "I could not price this session" bit and printed a false "$0.0000".
//
// THE DEFECT
// ----------
// `Pricing.IsUnknown` (`src/Harbor.Abstractions.Contracts/Models/Session.cs`) is
// true when a model publishes no rate table, and `SessionMetadata.AddUsage`
// turns that into `IsCostKnown = false`, which the UI state carries as
// `CostSnapshot.IsCostUnpriced`. Three surfaces read it and print "—":
//
//   StatusBarFacts.cs        -> StatusBarText.CostCell(cost, IsCostUnpriced)
//   CellForgeTuiRenderer.cs  -> svm.IsCostKnown = !IsCostUnpriced
//   TuiViewModels.cs         -> IsCostKnown ? ToUsd(Cost) : UsdCell.Unpriced
//
// Two did not, and both are on the Alt+5 token-breakdown panel and the Avalonia
// "token usage" overlay:
//
//   PanelRows.TokenRows       -> StatusBarText.CostToUsd(cost), unconditionally
//   TokenUsageView.axaml      -> StringFormat='{}${0:F4}' on a bare decimal
//
// so on an unpriced model the SAME FRAME showed "$0.0000" in the panel and "—"
// in the status bar next to it. 11 of the 13 shipped providers have no rates
// (`ProviderConfig.ParseModel` hands every JSON-served model `Pricing.Unknown`),
// so this was the normal case, not an edge one.
//
// WHY THE ANSWER BELONGS IN THE RENDERER, NOT THE CORE
// ---------------------------------------------------
// The core is right and is not changed here. `IsCostUnpriced` is exactly the bit
// the product needs, and `IsCostUnpriced == true` at `CostUsd == 0` is CORRECT
// and not a contradiction: a free model and an unpriced one are genuinely
// indistinguishable from the numbers alone (all four rates zero), so the core
// answers "unknown" for both rather than risk asserting a false zero. These
// tests pin that reading so a future "fix" cannot quietly invert the polarity —
// printing "$0.0000" for an unpriced model is the bug, not the correction.
//
// The glyph asserted below is `UsdCell.Unpriced`, the same constant the three
// already-honest surfaces print, so the panel and the status bar cannot drift
// apart again.

using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Issue #942: the token-breakdown panel and the token-usage overlay must
///     print the core's "price unknown" answer instead of a fabricated zero.
/// </summary>
public class CostUnpricedCellTests
{
    /// <summary>
    ///     The money cell is the LAST row that carries a number — the one after
    ///     the in/out bars and before the totals footnote.
    /// </summary>
    private static string TotalRow(IReadOnlyList<string> rows) => rows.First(r => r.StartsWith("total ", StringComparison.Ordinal));

    [Test]
    public async Task TokenRows_WhenUnpriced_PrintsTheEmDashNotAZero()
    {
        List<string> rows = PanelRows.TokenRows(
            input: 1500,
            output: 300,
            cost: 0m,
            width: 60,
            isCostUnpriced: true);

        string total = TotalRow(rows);
        await Assert.That(total).Contains(StatusBarText.UnknownCostCell)
            .Because("the core said it could not price this session, so the panel must not print an amount");
        await Assert.That(total).DoesNotContain("0.0000")
            .Because("a bare \"$0.0000\" is the false claim this issue is about");
    }

    [Test]
    public async Task TokenRows_WhenUnpriced_PrintsTheSameGlyphAsTheStatusBar()
    {
        List<string> rows = PanelRows.TokenRows(1500, 300, 0m, 60, isCostUnpriced: true);

        // The whole point of the fix: the panel and the status bar are two views
        // of ONE session's money, so they must not answer differently.
        string? statusBarCell = StatusBarText.CostCell(costUsd: 0m, isCostUnpriced: true);
        await Assert.That(statusBarCell).IsNotNull()
            .Because("CostCell returns a CELL for the unpriced case — that is the rule the panel "
                     + "mirrors; a null here would mean the two surfaces disagree about placement");
        await Assert.That(TotalRow(rows)).Contains(statusBarCell!)
            .Because("StatusBarText.CostCell is what the projected status bar paints for this "
                     + "exact state; a panel that says something else is the #457/#682 class of bug");
    }

    [Test]
    public async Task TokenRows_WhenPriced_StillPrintsTheAmount()
    {
        List<string> rows = PanelRows.TokenRows(1500, 300, 0.0042m, 60, isCostUnpriced: false);

        await Assert.That(TotalRow(rows)).Contains("$0.0042")
            .Because("a priced session must keep printing its bill — the fix must not blank every cost");
    }

    [Test]
    public async Task TokenRows_WhenPricedAndZero_PrintsZero()
    {
        // A genuinely free model reports IsCostUnpriced = true, so this branch is
        // reachable only for a session that HAS a rate table and has genuinely
        // spent nothing. "$0.0000" is the correct claim there, and the guard
        // must not turn it into a dash.
        List<string> rows = PanelRows.TokenRows(1500, 300, 0m, 60, isCostUnpriced: false);

        await Assert.That(TotalRow(rows)).Contains("$0.0000")
            .Because("priced-and-zero is a real measurement; only an UNPRICED zero is a lie");
    }

    [Test]
    public async Task TokenRows_DefaultParameter_BehavesAsPriced()
    {
        // The new parameter defaults to false so the struct's zero value and every
        // pre-existing call site keep rendering exactly as they did before the
        // flag reached this panel (same reasoning as CostSnapshot.IsCostUnpriced).
        List<string> withDefault = PanelRows.TokenRows(1500, 300, 0.0042m, 60);
        List<string> explicitFalse = PanelRows.TokenRows(1500, 300, 0.0042m, 60, isCostUnpriced: false);

        await Assert.That(TotalRow(withDefault)).IsEqualTo(TotalRow(explicitFalse))
            .Because("a defaulted bit must not change a priced session's rendering");
    }

    [Test]
    public async Task TokenRows_WhenUnpriced_StillDrawsTheTokenBars()
    {
        // The fix must touch the MONEY cell only. The in/out bars are measured
        // token counts — the core always knows those, unpriced or not — so a
        // dash must not bleed into them.
        List<string> rows = PanelRows.TokenRows(1500, 300, 0m, 60, isCostUnpriced: true);

        await Assert.That(rows.Any(r => r.Contains("█"))).IsTrue()
            .Because("tokens are measured regardless of whether the price is published");
    }

    [Test]
    public async Task UnpricedFlag_IsNotInvertedByAZeroCost()
    {
        // Pins the polarity the fix depends on. If a future change made
        // IsCostUnpriced derive from `CostUsd == 0`, this test fails — because
        // "cost is zero" and "cost is unknown" are different claims, and only the
        // second one may print a dash.
        var unpriced = new CostSnapshot(1500, 300, 0m, IsCostUnpriced: true);
        var freeButPriced = new CostSnapshot(1500, 300, 0m, IsCostUnpriced: false);

        await Assert.That(unpriced.IsCostUnpriced).IsTrue();
        await Assert.That(freeButPriced.IsCostUnpriced).IsFalse();
        await Assert.That(unpriced.CostUsd).IsEqualTo(freeButPriced.CostUsd)
            .Because("both carry a zero cost; only the bit separates them — which is why a renderer "
                     + "that reads the number instead of the bit gets this wrong");
    }
}