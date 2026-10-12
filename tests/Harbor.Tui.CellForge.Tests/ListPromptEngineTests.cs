using System.Text;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// SC7 list engine (#1215, epic #1195): Spectre.Console Prompts/List port —
/// leaf indexes, wrap, search, vim keys, paging window, group tree + parent
/// recount — and DialogSelectList as its clamp special case. Pure state, no backends.
/// </summary>
// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class ListPromptEngineTests
{
    private static KeyEvent Key(KeyCode code) => KeyEvent.Simple(code);

    private static KeyEvent CharKey(char c) => KeyEvent.Char(new Rune(c));

    private static ListPromptState<string> Flat(
        int pageSize = 5,
        bool wrapAround = false,
        ListSelectionMode mode = ListSelectionMode.Single,
        bool searchEnabled = false,
        params string[] values) =>
        ListPromptState<string>.FromValues(values, pageSize: pageSize, wrapAround: wrapAround, mode: mode, searchEnabled: searchEnabled);

    private static ListPromptState<string> Grouped(ListSelectionMode mode)
    {
        var tree = new ListPromptTree<string>();
        var group = new ListPromptItem<string>("Fruits");
        _ = group.AddChild("apple");
        _ = group.AddChild("apricot");
        tree.Add(group);
        tree.Add(new ListPromptItem<string>("carrot"));
        return new ListPromptState<string>(tree.Flatten(), mode: mode);
    }

    [Test]
    public async Task Single_Clamps_AtBothEnds()
    {
        var state = Flat(values: ["a", "b", "c"]);

        await Assert.That(state.Update(Key(KeyCode.Up))).IsFalse();
        await Assert.That(state.Index).IsEqualTo(0);

        await Assert.That(state.Update(Key(KeyCode.Down))).IsTrue();
        await Assert.That(state.Update(Key(KeyCode.Down))).IsTrue();
        await Assert.That(state.Update(Key(KeyCode.Down))).IsFalse();
        await Assert.That(state.Index).IsEqualTo(2);
    }

    [Test]
    public async Task Wrap_WrapsAround_BothDirections()
    {
        var state = Flat(wrapAround: true, values: ["a", "b", "c"]);

        await Assert.That(state.Update(Key(KeyCode.Up))).IsTrue();
        await Assert.That(state.Index).IsEqualTo(2);

        await Assert.That(state.Update(Key(KeyCode.Down))).IsTrue();
        await Assert.That(state.Index).IsEqualTo(0);
    }

    [Test]
    public async Task Leaf_SkipsGroupHeaders()
    {
        var state = Grouped(ListSelectionMode.Leaf);

        await Assert.That(state.Index).IsEqualTo(1); // first leaf, not the header
        await Assert.That(state.Update(Key(KeyCode.Up))).IsFalse(); // already first leaf

        await Assert.That(state.Update(Key(KeyCode.Down))).IsTrue();
        await Assert.That(state.Index).IsEqualTo(2);
        await Assert.That(state.TryGetCurrent(out string? cur)).IsTrue();
        await Assert.That(cur!).IsEqualTo("apricot");
    }

    [Test]
    public async Task Page_MovesByPageSize_WithClamp()
    {
        var state = Flat(pageSize: 2, values: ["a", "b", "c", "d", "e"]);

        await Assert.That(state.Update(Key(KeyCode.PageDown))).IsTrue();
        await Assert.That(state.Index).IsEqualTo(2);

        await Assert.That(state.Update(Key(KeyCode.PageDown))).IsTrue();
        await Assert.That(state.Index).IsEqualTo(4);

        await Assert.That(state.Update(Key(KeyCode.PageDown))).IsFalse();
        await Assert.That(state.Update(Key(KeyCode.PageUp))).IsTrue();
        await Assert.That(state.Index).IsEqualTo(2);
    }

    [Test]
    public async Task CalculatePageSize_Clamps_And_EmptyYieldsOne()
    {
        var strategy = new SingleListPromptStrategy<string>();

        await Assert.That(strategy.CalculatePageSize(10, 99)).IsEqualTo(10);
        await Assert.That(strategy.CalculatePageSize(10, 0)).IsEqualTo(1);
        await Assert.That(strategy.CalculatePageSize(0, 5)).IsEqualTo(1);
    }

    [Test]
    public async Task Vim_JK_Move_WhenSearchOff()
    {
        var state = Flat(values: ["a", "b", "c"]);

        await Assert.That(state.Update(CharKey('j'))).IsTrue();
        await Assert.That(state.Index).IsEqualTo(1);
        await Assert.That(state.Update(CharKey('k'))).IsTrue();
        await Assert.That(state.Index).IsEqualTo(0);
        await Assert.That(state.Update(CharKey('G'))).IsTrue();
        await Assert.That(state.Index).IsEqualTo(2);
        await Assert.That(state.Update(CharKey('g'))).IsTrue();
        await Assert.That(state.Index).IsEqualTo(0);
        await Assert.That(state.Update(CharKey('x'))).IsFalse();
    }

    [Test]
    public async Task Vim_JK_TypeIntoSearch_WhenSearchOn()
    {
        var state = Flat(searchEnabled: true, values: ["apple", "jump", "carrot"]);

        await Assert.That(state.Update(CharKey('j'))).IsTrue();
        await Assert.That(state.SearchText).IsEqualTo("j");
        await Assert.That(state.TryGetCurrent(out string? cur)).IsTrue();
        await Assert.That(cur!).IsEqualTo("jump"); // jumped to match, cursor did NOT step by one
    }

    [Test]
    public async Task Search_BackspaceShrinks_And_TabCyclesMatches()
    {
        var state = Flat(searchEnabled: true, values: ["apple", "apricot", "carrot"]);

        _ = state.Update(CharKey('a'));
        _ = state.Update(CharKey('p'));
        await Assert.That(state.SearchText).IsEqualTo("ap");
        await Assert.That(state.Index).IsEqualTo(0);

        await Assert.That(state.Update(Key(KeyCode.Tab))).IsTrue(); // cycle to second match
        await Assert.That(state.Index).IsEqualTo(1);

        await Assert.That(state.Update(Key(KeyCode.Backspace))).IsTrue();
        await Assert.That(state.SearchText).IsEqualTo("a");
        await Assert.That(state.Index).IsEqualTo(0); // re-jumped to first match
    }

    [Test]
    public async Task Multi_ToggleLeaf_RecountsParent()
    {
        var state = Grouped(ListSelectionMode.Multiple);

        _ = state.MoveTo(1); // apple
        await Assert.That(state.ToggleCurrent()).IsTrue();
        await Assert.That(state.GetSelectedLeaves()).Count().IsEqualTo(1);

        _ = state.MoveTo(2); // apricot
        await Assert.That(state.ToggleCurrent()).IsTrue();
        await Assert.That(state.Items[0].IsSelected).IsTrue(); // parent recounted: all children on

        _ = state.MoveTo(0); // group header itself toggles the subtree off
        await Assert.That(state.ToggleCurrent()).IsTrue();
        await Assert.That(state.GetSelectedLeaves()).Count().IsEqualTo(0);
        await Assert.That(state.Items[0].IsSelected).IsFalse();
    }

    [Test]
    public async Task ToggleCurrent_NoOp_OutsideMultiple()
    {
        var state = Grouped(ListSelectionMode.Leaf);

        await Assert.That(state.ToggleCurrent()).IsFalse();
    }

    [Test]
    public async Task ScrollOffsetFor_KeepsCursorVisible()
    {
        var state = Flat(values: ["a", "b", "c", "d", "e"]);
        _ = state.MoveTo(4);

        await Assert.That(state.ScrollOffsetFor(0, 2)).IsEqualTo(3);
        await Assert.That(state.ScrollOffsetFor(3, 2)).IsEqualTo(3);

        _ = state.MoveTo(0);
        await Assert.That(state.ScrollOffsetFor(3, 2)).IsEqualTo(0);
    }

    [Test]
    public async Task Empty_IsNoOp_And_IndexMinusOne()
    {
        var state = Flat(values: []);

        await Assert.That(state.Index).IsEqualTo(-1);
        await Assert.That(state.Update(Key(KeyCode.Down))).IsFalse();
        await Assert.That(state.MoveBy(1)).IsFalse();
        await Assert.That(state.MoveTo(0)).IsFalse();
        await Assert.That(state.ToggleCurrent()).IsFalse();
        await Assert.That(state.TryGetCurrent(out _)).IsFalse();
        await Assert.That(state.ScrollOffsetFor(0, 5)).IsEqualTo(0);
    }

    [Test]
    public async Task Strategy_MapsEnterEscape_And_NavigationToRefresh()
    {
        var strategy = new SingleListPromptStrategy<string>();
        var state = Flat(values: ["a", "b"]);

        await Assert.That(strategy.HandleInput(Key(KeyCode.Enter), state)).IsEqualTo(ListPromptInputResult.Submit);
        await Assert.That(strategy.HandleInput(Key(KeyCode.Escape), state)).IsEqualTo(ListPromptInputResult.Abort);
        await Assert.That(strategy.HandleInput(Key(KeyCode.Up), state)).IsEqualTo(ListPromptInputResult.None);
        await Assert.That(strategy.HandleInput(Key(KeyCode.Down), state)).IsEqualTo(ListPromptInputResult.Refresh);
        await Assert.That(strategy.CalculateInitialIndex([])).IsEqualTo(-1);
    }

    [Test]
    public async Task MultiStrategy_SpaceToggles_ButTypes_WhenSearchOn()
    {
        var strategy = new MultiListPromptStrategy<string>();
        var plain = Grouped(ListSelectionMode.Multiple);
        var searching = Flat(searchEnabled: true, mode: ListSelectionMode.Multiple, values: ["a b", "c"]);

        await Assert.That(strategy.HandleInput(CharKey(' '), plain)).IsEqualTo(ListPromptInputResult.Refresh);
        await Assert.That(strategy.HandleInput(CharKey(' '), searching)).IsEqualTo(ListPromptInputResult.Refresh);
        await Assert.That(searching.SearchText).IsEqualTo(" ");
        await Assert.That(searching.GetSelectedLeaves()).Count().IsEqualTo(0);
    }

    [Test]
    public async Task DialogSelectList_IsClampSpecialCase()
    {
        var select = new DialogSelectList();
        select.SetItems(["a", "b", "c"]);

        select.Move(99);
        await Assert.That(select.SelectedIndex).IsEqualTo(2);
        await Assert.That(select.SelectedItem).IsEqualTo("c");

        select.MoveTo(-5);
        await Assert.That(select.SelectedIndex).IsEqualTo(0);
        await Assert.That(select.SelectedItem).IsEqualTo("a");

        select.Move(-1);
        await Assert.That(select.SelectedIndex).IsEqualTo(0); // no wrap, like before

        select.SetItems([]);
        await Assert.That(select.SelectedIndex).IsEqualTo(-1);
        await Assert.That(select.SelectedItem).IsEqualTo(string.Empty);
    }
}
