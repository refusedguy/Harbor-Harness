// #1547: the list prompt's command table (ListPromptCommandTable) replaced
// the KeyCode switch plus the UpdateVim char switch. The table is internal
// — the Prompts csproj grants this project InternalsVisibleTo for exactly
// this: the rows ARE the spec, and a behavioural test alone cannot see a row
// that no key ever reaches.

using System.Text;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #1547: every input in every mode maps to the same action the old
/// <c>KeyCode</c> switch + <c>UpdateVim</c> pair produced, and the vim rows
/// run the SAME command objects as the arrow rows — the duplication the old
/// two-level dispatch carried.
/// </summary>
public sealed class ListPromptCommandTableTests
{
    private static KeyEvent Key(KeyCode code) => KeyEvent.Simple(code);

    private static KeyEvent CharKey(char c) => KeyEvent.Char(new Rune(c));

    private static KeyEvent ShiftedCharKey(char c) => KeyEvent.Char(new Rune(c), KeyModifiers.Shift);

    private static KeyEvent CtrlCharKey(char c) => KeyEvent.Char(new Rune(c), KeyModifiers.Ctrl);

    /// <summary>Four rows: "apple", "jump", "carrot", "melon" — "a" matches two of them.</summary>
    private static ListPromptState<string> Items(bool searchEnabled = false, int initialIndex = 0) =>
        ListPromptState<string>.FromValues(
            ["apple", "jump", "carrot", "melon"],
            searchEnabled: searchEnabled,
            initialIndex: initialIndex);

    // ------------------------------------------------------------- behaviour --

    [Test]
    public async Task Navigation_Mode_ArrowsEdgesAndPaging_MoveWhereTheOldSwitchMovedThem()
    {
        var up = Items();
        await Assert.That(up.Update(Key(KeyCode.Up))).IsFalse();
        await Assert.That(up.Index).IsEqualTo(0);

        var down = Items();
        await Assert.That(down.Update(Key(KeyCode.Down))).IsTrue();
        await Assert.That(down.Index).IsEqualTo(1);

        var home = Items(initialIndex: 2);
        await Assert.That(home.Update(Key(KeyCode.Home))).IsTrue();
        await Assert.That(home.Index).IsEqualTo(0);

        var end = Items();
        await Assert.That(end.Update(Key(KeyCode.End))).IsTrue();
        await Assert.That(end.Index).IsEqualTo(3);

        var pageUp = Items(initialIndex: 3);
        await Assert.That(pageUp.Update(Key(KeyCode.PageUp))).IsTrue();
        await Assert.That(pageUp.Index).IsEqualTo(0);

        var pageDown = Items();
        await Assert.That(pageDown.Update(Key(KeyCode.PageDown))).IsTrue();
        await Assert.That(pageDown.Index).IsEqualTo(3);
    }

    [Test]
    public async Task Navigation_Mode_VimKeys_MoveTheSameWayTheArrowsDo()
    {
        var j = Items();
        await Assert.That(j.Update(CharKey('j'))).IsTrue();
        await Assert.That(j.Index).IsEqualTo(1);

        var capitalJ = Items();
        await Assert.That(capitalJ.Update(ShiftedCharKey('J'))).IsTrue();
        await Assert.That(capitalJ.Index).IsEqualTo(1);

        var k = Items(initialIndex: 2);
        await Assert.That(k.Update(CharKey('k'))).IsTrue();
        await Assert.That(k.Index).IsEqualTo(1);

        var capitalK = Items(initialIndex: 2);
        await Assert.That(capitalK.Update(ShiftedCharKey('K'))).IsTrue();
        await Assert.That(capitalK.Index).IsEqualTo(1);

        var g = Items(initialIndex: 2);
        await Assert.That(g.Update(CharKey('g'))).IsTrue();
        await Assert.That(g.Index).IsEqualTo(0);

        var capitalG = Items();
        await Assert.That(capitalG.Update(CharKey('G'))).IsTrue();
        await Assert.That(capitalG.Index).IsEqualTo(3);
    }

    [Test]
    public async Task Navigation_Mode_RefusesTheSearchKeysTheChordsAndTheUnknownRunes()
    {
        var state = Items();

        await Assert.That(state.Update(Key(KeyCode.Backspace))).IsFalse();
        await Assert.That(state.Update(Key(KeyCode.Tab))).IsFalse();
        await Assert.That(state.Update(CharKey('x'))).IsFalse();
        await Assert.That(state.Update(CtrlCharKey('j'))).IsFalse();
        await Assert.That(state.Update(Key(KeyCode.Enter))).IsFalse();

        await Assert.That(state.Index).IsEqualTo(0);
        await Assert.That(state.SearchText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Search_Mode_TypedRunesExtendTheQuery_AndTheVimKeysDoNotMove()
    {
        var state = Items(searchEnabled: true);

        await Assert.That(state.Update(CharKey('j'))).IsTrue();
        await Assert.That(state.SearchText).IsEqualTo("j");
        await Assert.That(state.Index).IsEqualTo(1);

        var chord = Items(searchEnabled: true);
        await Assert.That(chord.Update(CtrlCharKey('j'))).IsFalse();
        await Assert.That(chord.SearchText).IsEqualTo(string.Empty);
        await Assert.That(chord.Index).IsEqualTo(0);

        var control = Items(searchEnabled: true);
        await Assert.That(control.Update(KeyEvent.Char(new Rune('\t')))).IsFalse();
        await Assert.That(control.SearchText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Search_Mode_BackspaceShrinksTheQuery_AndTabCyclesTheMatches()
    {
        var state = Items(searchEnabled: true);

        _ = state.Update(CharKey('a'));
        await Assert.That(state.SearchText).IsEqualTo("a");
        await Assert.That(state.Index).IsEqualTo(0);

        await Assert.That(state.Update(Key(KeyCode.Tab))).IsTrue();
        await Assert.That(state.Index).IsEqualTo(2);

        await Assert.That(state.Update(Key(KeyCode.Backspace))).IsTrue();
        await Assert.That(state.SearchText).IsEqualTo(string.Empty);
        await Assert.That(state.Index).IsEqualTo(0);
    }

    [Test]
    public async Task Search_Mode_KeepsTheNavigationKeysAlive()
    {
        var state = Items(searchEnabled: true);

        await Assert.That(state.Update(Key(KeyCode.Down))).IsTrue();
        await Assert.That(state.Index).IsEqualTo(1);

        await Assert.That(state.Update(Key(KeyCode.End))).IsTrue();
        await Assert.That(state.Index).IsEqualTo(3);
    }

    // -------------------------------------------------------------- the table --

    [Test]
    public async Task TheVimRows_RunTheSameCommandObjectsAsTheArrowRows()
    {
        await Assert.That(ReferenceEquals(CommandForChar('j'), CommandFor(KeyCode.Down))).IsTrue()
            .Because("'j' is the Down move behind a second gesture — the old switch spelled it out twice");
        await Assert.That(ReferenceEquals(CommandForChar('J'), CommandFor(KeyCode.Down))).IsTrue();
        await Assert.That(ReferenceEquals(CommandForChar('k'), CommandFor(KeyCode.Up))).IsTrue();
        await Assert.That(ReferenceEquals(CommandForChar('K'), CommandFor(KeyCode.Up))).IsTrue();
        await Assert.That(ReferenceEquals(CommandForChar('g'), CommandFor(KeyCode.Home))).IsTrue();
        await Assert.That(ReferenceEquals(CommandForChar('G'), CommandFor(KeyCode.End))).IsTrue();
    }

    [Test]
    public async Task NoTwoRowsInTheSameScope_ClaimTheSameGesture()
    {
        List<string> clashes = [];

        foreach (ListPromptScope scope in new[] { ListPromptScope.Any, ListPromptScope.Search, ListPromptScope.Navigation })
        {
            var seen = new HashSet<ListPromptGesture>();
            foreach (ListPromptBinding row in ListPromptCommandTable.Bindings)
            {
                if (row.Scope != scope)
                {
                    continue;
                }

                if (!seen.Add(row.Gesture))
                {
                    clashes.Add($"{scope}: {row.Gesture.Key} {row.Gesture.Character}");
                }
            }
        }

        await Assert.That(string.Join(" | ", clashes)).IsEmpty()
            .Because("two rows with the same gesture in one scope make the first one unreachable");
    }

    [Test]
    public async Task EveryRowOfTheTable_FiresInItsOwnScope()
    {
        List<string> dead = [];

        foreach (ListPromptBinding row in ListPromptCommandTable.Bindings)
        {
            KeyEvent key = KeyFor(row.Gesture);

            if (row.Scope != ListPromptScope.Search)
            {
                var navigating = Items(initialIndex: 1);
                if (!navigating.Update(key))
                {
                    dead.Add($"navigation scope: {Describe(row.Gesture)}");
                }
            }

            if (row.Scope != ListPromptScope.Navigation)
            {
                var searching = Items(searchEnabled: true, initialIndex: 1);
                if (row.Scope == ListPromptScope.Search)
                {
                    _ = searching.Update(CharKey('a')); // the search commands need a query to act on
                }

                if (!searching.Update(key))
                {
                    dead.Add($"search scope: {Describe(row.Gesture)}");
                }
            }
        }

        await Assert.That(string.Join(" | ", dead)).IsEmpty()
            .Because("a row no key reaches is a row the old switch never had either");
    }

    // ---------------------------------------------------------------- helpers --

    private static KeyEvent KeyFor(ListPromptGesture gesture)
    {
        if (gesture.Key != KeyCode.Char)
        {
            return KeyEvent.Simple(gesture.Key);
        }

        char c = gesture.Character is { } rune ? (char)rune.Value : 'a';
        return KeyEvent.Char(new Rune(c));
    }

    private static string Describe(ListPromptGesture gesture) =>
        gesture.Character is { } rune ? $"{gesture.Key} '{rune}'" : gesture.Key.ToString();

    private static IListPromptCommand CommandFor(KeyCode key)
    {
        foreach (ListPromptBinding row in ListPromptCommandTable.Bindings)
        {
            if (row.Scope == ListPromptScope.Any && row.Gesture.Key == key && row.Gesture.Character is null)
            {
                return row.Command;
            }
        }

        throw new InvalidOperationException($"The table has no navigation row for {key}.");
    }

    private static IListPromptCommand CommandForChar(char c)
    {
        foreach (ListPromptBinding row in ListPromptCommandTable.Bindings)
        {
            if (row.Scope == ListPromptScope.Navigation
                && row.Gesture.Character is { } rune
                && rune.Value == c)
            {
                return row.Command;
            }
        }

        throw new InvalidOperationException($"The table has no vim row for '{c}'.");
    }
}
