using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Pure state/keyboard contract: no palette, no buffer, nothing to serialize.
public class SequenceNavTests
{
    [Test]
    public async Task Normalize_ClampsExternalIndexAndHandlesEmpty()
    {
        await Assert.That(SequenceNav.Normalize(SequenceNav.Page(5, 10))).IsEqualTo(5);
        await Assert.That(SequenceNav.Normalize(SequenceNav.Page(-3, 10))).IsEqualTo(0);
        await Assert.That(SequenceNav.Normalize(SequenceNav.Page(99, 10))).IsEqualTo(9);
        await Assert.That(SequenceNav.Normalize(SequenceNav.Page(5, 0))).IsEqualTo(0);
        await Assert.That(SequenceNav.Normalize(SequenceNav.Page(-1, 0))).IsEqualTo(0);
        await Assert.That(SequenceNav.Number(SequenceNav.Page(99, 10))).IsEqualTo(10);
        await Assert.That(SequenceNav.Number(SequenceNav.Page(0, 0))).IsEqualTo(1);
    }

    [Test]
    public async Task FirstLast_AreSharedByBothModes()
    {
        await Assert.That(SequenceNav.First(SequenceNav.Page(3, 10))).IsEqualTo(0);
        await Assert.That(SequenceNav.Last(SequenceNav.Page(3, 10))).IsEqualTo(9);
        await Assert.That(SequenceNav.Last(SequenceNav.Carousel(3, 7))).IsEqualTo(6);
        await Assert.That(SequenceNav.Last(SequenceNav.Carousel(0, 0))).IsEqualTo(0);
    }

    [Test]
    public async Task HasPreviousNext_PageClampsAtEdges()
    {
        var first = SequenceNav.Page(0, 10);
        await Assert.That(SequenceNav.HasPrevious(first)).IsFalse();
        await Assert.That(SequenceNav.HasNext(first)).IsTrue();

        var middle = SequenceNav.Page(4, 10);
        await Assert.That(SequenceNav.HasPrevious(middle)).IsTrue();
        await Assert.That(SequenceNav.HasNext(middle)).IsTrue();

        var last = SequenceNav.Page(9, 10);
        await Assert.That(SequenceNav.HasPrevious(last)).IsTrue();
        await Assert.That(SequenceNav.HasNext(last)).IsFalse();

        await Assert.That(SequenceNav.HasNext(SequenceNav.Page(0, 0))).IsFalse();
        await Assert.That(SequenceNav.HasNext(SequenceNav.Page(0, 1))).IsFalse();
    }

    [Test]
    public async Task PreviousNext_CarouselWrapsOnlyWhenLooping()
    {
        var looping = SequenceNav.Carousel(0, 10, loop: true);
        await Assert.That(SequenceNav.Previous(looping)).IsEqualTo(9);
        await Assert.That(SequenceNav.Next(looping)).IsEqualTo(1);
        await Assert.That(SequenceNav.Next(SequenceNav.Carousel(9, 10, loop: true))).IsEqualTo(0);

        var clamped = SequenceNav.Carousel(0, 10);
        await Assert.That(SequenceNav.Previous(clamped)).IsNull();
        await Assert.That(SequenceNav.Next(clamped)).IsEqualTo(1);
        await Assert.That(SequenceNav.Next(SequenceNav.Carousel(9, 10))).IsNull();

        // Loop is a carousel policy: a page sequence built by hand never wraps.
        var pageWithLoop = new SequenceNavState(0, 10, SequenceNavMode.Page, Loop: true);
        await Assert.That(SequenceNav.Wraps(pageWithLoop)).IsFalse();
        await Assert.That(SequenceNav.Previous(pageWithLoop)).IsNull();
    }

    [Test]
    public async Task Step_WrapsOrClampsAndRespectsDisabled()
    {
        await Assert.That(SequenceNav.Step(SequenceNav.Carousel(9, 10, loop: true), 1)).IsEqualTo(0);
        await Assert.That(SequenceNav.Step(SequenceNav.Carousel(0, 10, loop: true), -1)).IsEqualTo(9);
        await Assert.That(SequenceNav.Step(SequenceNav.Carousel(5, 10, loop: true), 7)).IsEqualTo(2);
        await Assert.That(SequenceNav.Step(SequenceNav.Page(9, 10), 1)).IsEqualTo(9);
        await Assert.That(SequenceNav.Step(SequenceNav.Page(0, 10), -1)).IsEqualTo(0);
        await Assert.That(SequenceNav.Step(SequenceNav.Carousel(5, 10), 7)).IsEqualTo(9);
        await Assert.That(SequenceNav.Step(SequenceNav.Page(5, 10, disabled: true), 1)).IsEqualTo(5);
        await Assert.That(SequenceNav.Step(SequenceNav.Page(5, 0), 1)).IsEqualTo(0);
    }

    [Test]
    public async Task TrySelect_GuardsRangeAndKeepsOwnerPolicy()
    {
        var state = SequenceNav.Carousel(0, 10, loop: true, visible: 3);
        bool handled = SequenceNav.TrySelect(state, 4, out var next);
        await Assert.That(handled).IsTrue();
        await Assert.That(next.Index).IsEqualTo(4);
        await Assert.That(next.Count).IsEqualTo(10);
        await Assert.That(next.Mode).IsEqualTo(SequenceNavMode.Carousel);
        await Assert.That(next.Loop).IsTrue();
        await Assert.That(next.Visible).IsEqualTo(3);

        await Assert.That(SequenceNav.TrySelect(state, -1, out _)).IsFalse();
        await Assert.That(SequenceNav.TrySelect(state, 10, out _)).IsFalse();
        await Assert.That(SequenceNav.TrySelect(SequenceNav.Page(0, 0), 0, out _)).IsFalse();
        await Assert.That(SequenceNav.TrySelect(SequenceNav.Page(0, 10, disabled: true), 1, out _)).IsFalse();
    }

    [Test]
    public async Task TryHandleKey_ArrowsAndHomeEnd_FoldToExpectedIndex()
    {
        var state = SequenceNav.Page(5, 10);
        (UiKey Key, int Expected)[] cases =
        [
            (new UiKey(UiKeyCode.Left), 4),
            (new UiKey(UiKeyCode.Up), 4),
            (new UiKey(UiKeyCode.PageUp), 4),
            (new UiKey(UiKeyCode.Right), 6),
            (new UiKey(UiKeyCode.Down), 6),
            (new UiKey(UiKeyCode.PageDown), 6),
            (new UiKey(UiKeyCode.Home), 0),
            (new UiKey(UiKeyCode.End), 9),
        ];

        foreach (var (key, expected) in cases)
        {
            bool handled = SequenceNav.TryHandleKey(key, state, out var next);
            await Assert.That(handled).IsTrue();
            await Assert.That(next.Index).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task TryHandleKey_OtherKeysAreNotConsumed()
    {
        var state = SequenceNav.Page(5, 10);
        UiKey[] passThrough =
        [
            UiKey.ForChar('a'),
            new UiKey(UiKeyCode.Enter),
            new UiKey(UiKeyCode.Escape),
            new UiKey(UiKeyCode.Tab),
            new UiKey(UiKeyCode.Backspace),
            UiKey.Unknown,
        ];

        foreach (var key in passThrough)
        {
            bool handled = SequenceNav.TryHandleKey(key, state, out var next);
            await Assert.That(handled).IsFalse();
            await Assert.That(next).IsEqualTo(default(SequenceNavState));
        }
    }

    [Test]
    public async Task TryHandleKey_HostChordDisabledAndEdgeKeys_AreNotConsumed()
    {
        var state = SequenceNav.Page(5, 10);
        await Assert.That(SequenceNav.TryHandleKey(
            new UiKey(UiKeyCode.Right, KeyModifierSet.Ctrl), state, out _)).IsFalse();
        await Assert.That(SequenceNav.TryHandleKey(
            new UiKey(UiKeyCode.Left, KeyModifierSet.Alt), state, out _)).IsFalse();
        await Assert.That(SequenceNav.TryHandleKey(
            new UiKey(UiKeyCode.Left), SequenceNav.Page(0, 10), out _)).IsFalse();
        await Assert.That(SequenceNav.TryHandleKey(
            new UiKey(UiKeyCode.Right), SequenceNav.Page(9, 10), out _)).IsFalse();
        await Assert.That(SequenceNav.TryHandleKey(
            new UiKey(UiKeyCode.Home), SequenceNav.Page(5, 10, disabled: true), out _)).IsFalse();
    }

    [Test]
    public async Task TryHandleKey_CarouselLoopWrapsAtEdges()
    {
        var first = SequenceNav.Carousel(0, 10, loop: true);
        await Assert.That(SequenceNav.TryHandleKey(new UiKey(UiKeyCode.Left), first, out var prev)).IsTrue();
        await Assert.That(prev.Index).IsEqualTo(9);
        await Assert.That(prev.Loop).IsTrue();

        var last = SequenceNav.Carousel(9, 10, loop: true);
        await Assert.That(SequenceNav.TryHandleKey(new UiKey(UiKeyCode.Right), last, out var next)).IsTrue();
        await Assert.That(next.Index).IsEqualTo(0);
    }

    [Test]
    public async Task TryHandleKey_PreservesOwnerSuppliedPolicy()
    {
        var state = SequenceNav.Carousel(2, 8, loop: true, visible: 3);
        await Assert.That(SequenceNav.TryHandleKey(new UiKey(UiKeyCode.Right), state, out var next)).IsTrue();
        await Assert.That(next.Count).IsEqualTo(8);
        await Assert.That(next.Mode).IsEqualTo(SequenceNavMode.Carousel);
        await Assert.That(next.Loop).IsTrue();
        await Assert.That(next.Visible).IsEqualTo(3);
        await Assert.That(next.Disabled).IsFalse();
    }
}

// Golden strings: one slot list folded into one text/layout for both modes.
public class SequenceNavGoldenTests
{
    [Test]
    public async Task Page_Text_PinsFirstLastAndFoldsHiddenRun()
    {
        await Assert.That(SequenceNav.Text(SequenceNav.Page(4, 10)))
            .IsEqualTo("< 1 … 4 [5] 6 … 10 >");
        await Assert.That(SequenceNav.Text(SequenceNav.Page(0, 10)))
            .IsEqualTo("< [1] 2 3 … 10 >");
        await Assert.That(SequenceNav.Text(SequenceNav.Page(9, 10)))
            .IsEqualTo("< 1 … 8 9 [10] >");
        await Assert.That(SequenceNav.Text(SequenceNav.Page(4, 6)))
            .IsEqualTo("< 1 … 4 [5] 6 >");
    }

    [Test]
    public async Task Page_Text_ShortAndDegenerateSequences()
    {
        await Assert.That(SequenceNav.Text(SequenceNav.Page(0, 5))).IsEqualTo("< [1] 2 3 4 5 >");
        await Assert.That(SequenceNav.Text(SequenceNav.Page(0, 1))).IsEqualTo("< [1] >");
        await Assert.That(SequenceNav.Text(SequenceNav.Page(0, 0))).IsEqualTo("<>");
    }

    [Test]
    public async Task Page_VisibleFloor_IsFiveAsInGpuiKit()
    {
        await Assert.That(SequenceNav.Text(SequenceNav.Page(0, 10, visible: 1)))
            .IsEqualTo(SequenceNav.Text(SequenceNav.Page(0, 10)));
        await Assert.That(SequenceNav.Text(SequenceNav.Page(4, 10, visible: 9)))
            .IsEqualTo("< 1 2 3 4 [5] 6 7 8 … 10 >");
        await Assert.That(SequenceNav.Text(SequenceNav.Page(0, 10, visible: 9)))
            .IsEqualTo("< [1] 2 3 4 5 … 10 >");
    }

    [Test]
    public async Task Carousel_Text_ShowsContiguousAdjacentItems()
    {
        await Assert.That(SequenceNav.Text(SequenceNav.Carousel(3, 7)))
            .IsEqualTo("< 2 3 [4] 5 6 >");
        await Assert.That(SequenceNav.Text(SequenceNav.Carousel(0, 7)))
            .IsEqualTo("< [1] 2 3 4 5 >");
        await Assert.That(SequenceNav.Text(SequenceNav.Carousel(6, 7)))
            .IsEqualTo("< 3 4 5 6 [7] >");
        await Assert.That(SequenceNav.Text(SequenceNav.Carousel(0, 4)))
            .IsEqualTo("< [1] 2 3 4 >");
    }

    [Test]
    public async Task Carousel_Text_WidthControlAndEmptySequence()
    {
        await Assert.That(SequenceNav.Text(SequenceNav.Carousel(3, 7, visible: 1)))
            .IsEqualTo("< [4] >");
        await Assert.That(SequenceNav.Text(SequenceNav.Carousel(3, 7, visible: 3)))
            .IsEqualTo("< 3 [4] 5 >");
        await Assert.That(SequenceNav.Text(SequenceNav.Carousel(0, 0))).IsEqualTo("<>");
    }

    [Test]
    public async Task Slots_CarryGapsOnlyInPageMode()
    {
        var page = SequenceNav.Slots(SequenceNav.Page(4, 10));
        await Assert.That(page.Count).IsEqualTo(7);
        await Assert.That(page[1].IsGap).IsTrue();
        await Assert.That(page[5].IsGap).IsTrue();
        await Assert.That(page[3].Number).IsEqualTo(5);

        var carousel = SequenceNav.Slots(SequenceNav.Carousel(3, 7));
        await Assert.That(carousel.Count).IsEqualTo(5);
        await Assert.That(carousel.Any(s => s.IsGap)).IsFalse();
        await Assert.That(SequenceNav.Slots(SequenceNav.Page(0, 0)).Count).IsEqualTo(0);
    }
}

// Rendering reads global styles, so serialize paint tests with other render tests.
[NotInParallel("pty")]
public class SequenceNavPaintTests
{
    private static string Art(int width, SequenceNavState state)
    {
        var buffer = TestRender.Buffer(width, 1);
        SequenceNav.Paint(buffer, new Rect(0, 0, width, 1), state);
        return TestRender.Art(buffer).TrimEnd('\n');
    }

    [Test]
    public async Task Paint_Page_Golden()
    {
        var state = SequenceNav.Page(4, 10);
        await Assert.That(Art(SequenceNav.Text(state).Length, state))
            .IsEqualTo("< 1 … 4 [5] 6 … 10 >");
    }

    [Test]
    public async Task Paint_Carousel_Golden()
    {
        var state = SequenceNav.Carousel(3, 7);
        await Assert.That(Art(SequenceNav.Text(state).Length, state))
            .IsEqualTo("< 2 3 [4] 5 6 >");
    }

    [Test]
    public async Task Paint_MatchesTextForBothModesAndEmptySequence()
    {
        foreach (var state in new[]
        {
            SequenceNav.Page(0, 10),
            SequenceNav.Page(9, 10),
            SequenceNav.Carousel(0, 7),
            SequenceNav.Carousel(0, 0),
        })
        {
            await Assert.That(Art(SequenceNav.Text(state).Length, state))
                .IsEqualTo(SequenceNav.Text(state));
        }
    }

    [Test]
    public async Task Paint_NarrowAndDegenerateRectsAreSafe()
    {
        var state = SequenceNav.Page(4, 10);
        await Assert.That(Art(5, state)).IsEqualTo("< 1 …");
        await Assert.That(Art(0, state)).IsEqualTo(string.Empty);

        var buffer = TestRender.Buffer(4, 1);
        SequenceNav.Paint(buffer, new Rect(0, 0, 4, 0), state);
        await Assert.That(TestRender.Art(buffer).TrimEnd('\n')).IsEqualTo("    ");
    }

    [Test]
    public async Task Paint_StylesCurrentBoldAndInactiveEdgesDim()
    {
        var buffer = TestRender.Buffer(20, 1);
        SequenceNav.Paint(buffer, new Rect(0, 0, 20, 1), SequenceNav.Page(0, 10));
        await Assert.That(buffer.Get(2, 0).Style.Attrs.HasFlag(StyleAttr.Bold)).IsTrue();
        await Assert.That(buffer.Get(0, 0).Style.Attrs.HasFlag(StyleAttr.Dim)).IsTrue();

        var last = SequenceNav.Page(9, 10);
        var lastText = SequenceNav.Text(last);
        var lastBuffer = TestRender.Buffer(lastText.Length, 1);
        SequenceNav.Paint(lastBuffer, new Rect(0, 0, lastText.Length, 1), last);
        await Assert.That(lastBuffer.Get(lastText.Length - 1, 0).Style.Attrs.HasFlag(StyleAttr.Dim)).IsTrue();
    }
}
