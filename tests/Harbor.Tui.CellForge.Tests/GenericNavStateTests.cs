using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class GenericNavStateTests
{
    [Test]
    public async Task NavState_Empty_SelectsNothing()
    {
        var state = new GenericNavState();
        state.SetContent(0);
        await Assert.That(state.Selected).IsEqualTo(-1);
        await Assert.That(state.AtTop).IsTrue();
        state.SelectNext();
        await Assert.That(state.Selected).IsEqualTo(-1);
    }

    [Test]
    public async Task NavState_NextPrevious_ClampWithoutWrap()
    {
        var state = new GenericNavState();
        state.SetContent(3);
        state.SelectPrevious();
        await Assert.That(state.Selected).IsEqualTo(0);
        state.SelectNext();
        state.SelectNext();
        state.SelectNext();
        await Assert.That(state.Selected).IsEqualTo(2);
        await Assert.That(state.AtBottom).IsTrue();
    }

    [Test]
    public async Task NavState_FirstLast_JumpToEnds()
    {
        var state = new GenericNavState();
        state.SetContent(5);
        state.SelectLast();
        await Assert.That(state.Selected).IsEqualTo(4);
        state.SelectFirst();
        await Assert.That(state.Selected).IsEqualTo(0);
        await Assert.That(state.AtTop).IsTrue();
    }

    [Test]
    public async Task NavState_MoveTo_Clamps()
    {
        var state = new GenericNavState();
        state.SetContent(3);
        state.MoveTo(99);
        await Assert.That(state.Selected).IsEqualTo(2);
        state.MoveTo(-99);
        await Assert.That(state.Selected).IsEqualTo(0);
    }

    [Test]
    public async Task NavState_SetContent_ResetsSelectionAndScroll()
    {
        var state = new GenericNavState();
        state.SetContent(10, viewportH: 3);
        state.SelectLast();
        await Assert.That(state.Offset).IsGreaterThan(0);
        state.SetContent(4);
        await Assert.That(state.Selected).IsEqualTo(0);
        await Assert.That(state.Offset).IsEqualTo(0);
        await Assert.That(state.Count).IsEqualTo(4);
    }

    [Test]
    public async Task NavState_Selection_DragsViewport()
    {
        var state = new GenericNavState();
        state.SetContent(10, viewportH: 3);
        state.MoveTo(9);
        (int first, int last) = state.VisibleSlice();
        await Assert.That(first).IsLessThanOrEqualTo(9);
        await Assert.That(last).IsGreaterThan(9);
    }

    [Test]
    public async Task NavState_ScrollBy_LeavesSelectionAlone()
    {
        var state = new GenericNavState();
        state.SetContent(10, viewportH: 3);
        state.MoveTo(2);
        state.ScrollBy(5);
        await Assert.That(state.Selected).IsEqualTo(2);
        await Assert.That(state.Offset).IsEqualTo(5);
        state.ScrollBy(99);
        await Assert.That(state.Offset).IsEqualTo(7);
        state.ScrollToTop();
        await Assert.That(state.Offset).IsEqualTo(0);
        state.ScrollToEnd();
        await Assert.That(state.Offset).IsEqualTo(7);
    }

    [Test]
    public async Task SelectionList_State_MirrorsMoveShorthands()
    {
        var list = new GenericSelectionList();
        list.SetItems(["a", "b", "c"]);
        list.State.SelectNext();
        await Assert.That(list.SelectedIndex).IsEqualTo(1);
        await Assert.That(list.SelectedItem).IsEqualTo("b");
        list.State.SelectLast();
        await Assert.That(list.SelectedIndex).IsEqualTo(2);
    }

    [Test]
    public async Task SelectionList_SharedState_DrivesTwoWidgets()
    {
        // The #1198 contract: state is external, so one state object can drive
        // two widgets and both paint the same selection.
        var left = new GenericSelectionList();
        var right = new GenericSelectionList();
        left.SetItems(["one", "two", "three", "four"]);
        right.SetItems(["one", "two", "three", "four"]);
        left.State.MoveTo(2);

        var buffer = TestRender.Buffer(20, 4);
        right.Paint(buffer, new Rect(0, 0, 20, 4), left.State);
        string art = ScreenFixtures.Art(buffer);
        await Assert.That(art.Contains("three")).IsTrue();
        await Assert.That(art.Contains("> three")).IsTrue();
    }

    [Test]
    public async Task SelectionList_ExternalState_ScrollsPaint()
    {
        var list = new GenericSelectionList { MaxVisibleRows = 2 };
        list.SetItems(["one", "two", "three", "four"]);
        var state = new GenericNavState();
        state.SetContent(list.Count, viewportH: 2);
        state.SelectLast();

        var buffer = TestRender.Buffer(20, 2);
        list.Paint(buffer, new Rect(0, 0, 20, 2), state);
        await Assert.That(ScreenFixtures.Art(buffer).Contains("four")).IsTrue();
    }
}
