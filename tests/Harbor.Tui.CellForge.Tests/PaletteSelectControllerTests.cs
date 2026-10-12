using Harbor.Ui.Framework.Overlays;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Contract tests for <see cref="PaletteSelectController" />: the BCL port
///     of opencode's <c>select-controller.ts</c> (#1177, epic #1155). Every case
///     is hand-evaluated against the TypeScript source (branch-for-branch),
///     not against the implementation — a port bug fails here, not in a host.
/// </summary>
public class PaletteSelectControllerTests
{
    [Test]
    public async Task ReconcileSelection_Clamps_And_EmptyIsZero()
    {
        await Assert.That(PaletteSelectController.ReconcileSelection(2, 5)).IsEqualTo(2);
        await Assert.That(PaletteSelectController.ReconcileSelection(7, 5)).IsEqualTo(4);
        await Assert.That(PaletteSelectController.ReconcileSelection(-3, 5)).IsEqualTo(0);
        await Assert.That(PaletteSelectController.ReconcileSelection(0, 0)).IsEqualTo(0);
        await Assert.That(PaletteSelectController.ReconcileSelection(4, 0)).IsEqualTo(0);
    }

    [Test]
    public async Task MoveSelection_Clamp_Stops_At_Ends()
    {
        await Assert.That(PaletteSelectController.MoveSelection(1, 3, 1)).IsEqualTo(2);
        await Assert.That(PaletteSelectController.MoveSelection(2, 3, 1)).IsEqualTo(2);
        await Assert.That(PaletteSelectController.MoveSelection(0, 3, -1)).IsEqualTo(0);
        await Assert.That(PaletteSelectController.MoveSelection(0, 3, -5)).IsEqualTo(0);
    }

    [Test]
    public async Task MoveSelection_Wrap_Jumps_To_Other_End()
    {
        await Assert.That(PaletteSelectController.MoveSelection(
            2, 3, 1, PaletteSelectController.MovePolicy.Wrap)).IsEqualTo(0);
        await Assert.That(PaletteSelectController.MoveSelection(
            0, 3, -1, PaletteSelectController.MovePolicy.Wrap)).IsEqualTo(2);
        await Assert.That(PaletteSelectController.MoveSelection(
            1, 3, 1, PaletteSelectController.MovePolicy.Wrap)).IsEqualTo(2);
    }

    [Test]
    public async Task MoveSelection_Empty_Count_IsZero()
    {
        await Assert.That(PaletteSelectController.MoveSelection(0, 0, 1)).IsEqualTo(0);
        await Assert.That(PaletteSelectController.MoveSelection(
            0, 0, -1, PaletteSelectController.MovePolicy.Wrap)).IsEqualTo(0);
    }

    [Test]
    public async Task RevealSelectionOffset_Brings_Selection_Into_View()
    {
        // Selected past the bottom: window slides so it is the last row.
        await Assert.That(PaletteSelectController.RevealSelectionOffset(0, 20, 10, 15)).IsEqualTo(6);
        // Selected above the top: window slides up to it.
        await Assert.That(PaletteSelectController.RevealSelectionOffset(5, 20, 10, 3)).IsEqualTo(3);
        // Selected inside: offset untouched.
        await Assert.That(PaletteSelectController.RevealSelectionOffset(5, 20, 10, 7)).IsEqualTo(5);
        // Never past the end, even when the selection is.
        await Assert.That(PaletteSelectController.RevealSelectionOffset(0, 8, 10, 7)).IsEqualTo(0);
    }

    [Test]
    public async Task ReconcileSelectionWindow_Pins_Selection_To_Window()
    {
        // Below the window: pinned to its last row.
        await Assert.That(PaletteSelectController.ReconcileSelectionWindow(15, 20, 10, 0)).IsEqualTo(9);
        // Above the window: pinned to its first row.
        await Assert.That(PaletteSelectController.ReconcileSelectionWindow(3, 20, 10, 5)).IsEqualTo(5);
        // Inside: untouched.
        await Assert.That(PaletteSelectController.ReconcileSelectionWindow(7, 20, 10, 5)).IsEqualTo(7);
        // Empty: zero.
        await Assert.That(PaletteSelectController.ReconcileSelectionWindow(2, 0, 10, 0)).IsEqualTo(0);
    }

    [Test]
    public async Task MoveSelectionOffset_TwoCell_Margin()
    {
        // limit 10 → margin 2. Scrolling down past row 7 slides the window.
        await Assert.That(PaletteSelectController.MoveSelectionOffset(0, 20, 10, 8, 1)).IsEqualTo(1);
        // Row 7 is the last one that still fits with the margin — no slide.
        await Assert.That(PaletteSelectController.MoveSelectionOffset(0, 20, 10, 7, 1)).IsEqualTo(0);
        // Scrolling up above the margin slides the window up.
        await Assert.That(PaletteSelectController.MoveSelectionOffset(5, 20, 10, 5, -1)).IsEqualTo(3);
        // Mid-window steps never move the offset.
        await Assert.That(PaletteSelectController.MoveSelectionOffset(5, 20, 10, 9, 1)).IsEqualTo(5);
        await Assert.That(PaletteSelectController.MoveSelectionOffset(5, 20, 10, 9, -1)).IsEqualTo(5);
    }

    [Test]
    public async Task MoveSelectionOffset_Margin_Shrinks_On_Small_Windows()
    {
        // limit 3 → margin 1.
        await Assert.That(PaletteSelectController.MoveSelectionOffset(0, 10, 3, 2, 1)).IsEqualTo(1);
        await Assert.That(PaletteSelectController.MoveSelectionOffset(0, 10, 3, 1, 1)).IsEqualTo(0);
        // limit 1 → margin 0: the selection IS the window, offset follows exactly.
        await Assert.That(PaletteSelectController.MoveSelectionOffset(2, 5, 1, 2, 1)).IsEqualTo(2);
        await Assert.That(PaletteSelectController.MoveSelectionOffset(2, 5, 1, 2, -1)).IsEqualTo(2);
    }
}
