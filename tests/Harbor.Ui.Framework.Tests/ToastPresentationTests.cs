// ToastPresentationTests.cs — #1543: the one table that says how a toast
// kind looks.
//
// WHAT WAS WRONG
// --------------
// The same mapping — a toast KIND and the shape it is drawn in — was written in
// three places by two UI stacks:
//
//   * src/Harbor.Tui.CellForge/Chat/Widgets/ToastOverlay.cs   the glyph
//   * apps/Harbor.App.Avalonia/Views/ToastConverters.cs       the icon key
//   * apps/Harbor.App.Avalonia/Views/ToastConverters.cs       the brush key
//
// and a fourth copy in the desktop shell had already drifted in VALUE: it drew
// "▲" and "✕" where the terminal renderer drew "!" and "✗" for the same two
// kinds. Nothing caught it, because a mapping that lives in a switch is only
// wrong to the eye.
//
// WHAT IS ASSERTED HERE
// ---------------------
//   1. every kind has a glyph and a pair of resource keys, and the values are
//      the ones the three call sites used.
//   2. the table is COMPLETE. A kind added to the enum without a row fails
//      here, which is the property a switch with a discard arm cannot hold on
//      its own: such a switch renders the new kind as an informational toast
//      and says so nowhere.
//   3. a kind that is not a declared member still renders, as the
//      informational row — the fallback all three call sites had.
//
// The companion guards that the CONSUMERS read this table rather than their
// own copies live next to the consumers:
// tests/Harbor.App.Avalonia.Tests/ToastPresentationTableTests.cs and
// tests/Harbor.Tui.CellForge.Tests/ToastOverlayGlyphTests.cs.

using Harbor.TestKit;
using Harbor.Ui.Framework.Services;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #1543 — the shared toast presentation table: one row per
///     <see cref="ToastKind" />, and the property that keeps it complete.
/// </summary>
public class ToastPresentationTests
{
    /// <summary>
    ///     The rows, as the test reads them: one per kind, with the glyph and
    ///     the two theme resource keys the kind is drawn with.
    /// </summary>
    /// <remarks>
    ///     The array IS the table this test checks, so the totality guard below
    ///     can be handed its own membership — there is no second list of kinds
    ///     to forget (the property <see cref="Exhaustive" /> exists for).
    /// </remarks>
    public static (string Name, ToastKind Kind, char Glyph, string IconKey, string BrushKey)[] Rows =>
    [
        ("Info", ToastKind.Info, 'ℹ', "IcInfo", "AccentPrimaryBrush"),
        ("Success", ToastKind.Success, '✓', "IcCheck", "StateSuccessBrush"),
        ("Warning", ToastKind.Warning, '!', "IcWarning", "StateWarningBrush"),
        ("Error", ToastKind.Error, '✗', "IcError", "StateErrorBrush"),
    ];

    /// <summary>
    ///     Every kind leads its toast with a glyph and names two theme
    ///     resources — the icon geometry and the accent brush.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Rows))]
    public async Task Every_ToastKind_Has_Its_Glyph_And_Its_Keys(
        string name,
        ToastKind kind,
        char glyph,
        string iconKey,
        string brushKey)
    {
        ToastPresentation presentation = ToastPresentations.For(kind);

        await Assert.That(presentation.Glyph).IsEqualTo(glyph)
            .Because($"{name} leads the toast row with '{glyph}'");
        await Assert.That(presentation.IconResourceKey).IsEqualTo(iconKey)
            .Because($"{name} paints the {iconKey} icon");
        await Assert.That(presentation.BrushResourceKey).IsEqualTo(brushKey)
            .Because($"{name} wears the {brushKey} brush");
    }

    /// <summary>
    ///     The table covers every declared kind — the property that makes a new
    ///     kind a build failure instead of a silent fallback.
    /// </summary>
    [Test]
    public async Task Every_ToastKind_Has_A_Row_In_The_Table()
    {
        List<ToastKind> declared = [.. Enum.GetValues<ToastKind>()];
        List<ToastKind> covered = [.. ToastPresentations.All.Keys.OrderBy(kind => (int)kind)];

        await Assert.That(covered).IsEquivalentTo(declared)
            .Because(
                "a kind with no row falls back to the informational presentation, and a toast that "
                + "renders as information says so nowhere");
    }

    /// <summary>
    ///     The totality check over this file's own rows: a kind added to
    ///     <see cref="ToastKind" /> has no expectation here until it gets one.
    /// </summary>
    [Test]
    public async Task Every_ToastKind_Has_A_Row_In_The_Expectation_Table()
    {
        await Assert.That(Rows.Length).IsGreaterThan(0).Because("the table has rows to check");
        Exhaustive.AssertAllCases<ToastKind>(
            Rows.Select(row => row.Kind).ToHashSet(),
            "every ToastKind must have a row in the toast presentation table");
    }

    /// <summary>
    ///     A kind that is not a declared member still renders: the fallback is
    ///     the informational row, which is what every one of the three call
    ///     sites did before the table existed.
    /// </summary>
    [Test]
    public async Task An_Undeclared_Kind_Falls_Back_To_The_Informational_Row()
    {
        ToastKind undeclared = (ToastKind)999;

        await Assert.That(ToastPresentations.For(undeclared))
            .IsEqualTo(ToastPresentations.For(ToastKind.Info))
            .Because("a toast whose kind did not arrive still has to render");
        await Assert.That(ToastPresentations.Glyph(undeclared))
            .IsEqualTo(ToastPresentations.Glyph(ToastKind.Info))
            .Because("the fallback is one row, not a second opinion per column");
    }

    /// <summary>
    ///     The table is a table and not four scattered answers: one row per
    ///     kind, and nothing else in it.
    /// </summary>
    [Test]
    public async Task The_Table_Holds_One_Row_Per_Kind_And_Nothing_Else()
    {
        await Assert.That(ToastPresentations.All.Count).IsEqualTo(Enum.GetValues<ToastKind>().Length)
            .Because("a row for a kind that does not exist is a mapping nothing can reach");
    }
}
