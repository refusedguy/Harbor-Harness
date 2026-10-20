using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

public class MarkdownEditOpsTests
{
    private static PromptBuffer NewAt(string text, int cursor)
    {
        var b = new PromptBuffer();
        _ = b.InsertText(text);
        _ = b.MoveTo(cursor);
        return b;
    }

    // #1770: the wrap vectors used to be one copy-pasted method each. The
    // assert never changed — only the (text, caret, marker) triple did. Every
    // row keeps its original test name as the case display name.
    [Test]
    [Arguments("hello world", 1, "**", "**hello** world", 2, DisplayName = "ToggleWrap_MidWord_WrapsAndCaretLandsAfterOpeningMarker")]
    [Arguments("   ", 3, "**", "   ****", 5, DisplayName = "ToggleWrap_WhitespaceOnly_FallsBackToBarePair")]
    [Arguments("a bb c", 1, "~~", "a ~~bb~~ c", 4, DisplayName = "ToggleWrap_OnGapBetweenWords_WrapsNextWord")]
    [Arguments("done todo ", 10, "**", "done **todo** ", 7, DisplayName = "ToggleWrap_CaretPastLastWord_WrapsThatWordSkippingTrailingSpace")]
    public async Task ToggleWrap_WrapsTheWordUnderTheCaret(string text, int caret, string marker, string expectedText, int expectedCaret)
    {
        var b = NewAt(text, caret);
        _ = MarkdownEditOps.ToggleWrap(b, marker);

        await Assert.That(b.SnapshotText()).IsEqualTo(expectedText);
        await Assert.That(b.Cursor).IsEqualTo(expectedCaret);
    }

    [Test]
    public async Task ToggleWrap_Twice_RoundTripsToOriginal()
    {
        var b = NewAt("code", 0);
        _ = MarkdownEditOps.ToggleWrap(b, "`");
        var wrapped = b.SnapshotText();
        _ = MarkdownEditOps.ToggleWrap(b, "`");

        await Assert.That(wrapped).IsEqualTo("`code`");
        await Assert.That(b.SnapshotText()).IsEqualTo("code");
        await Assert.That(b.Cursor).IsEqualTo(0);
    }

    [Test]
    public async Task ToggleWrap_EmptyBuffer_GrowsBarePair_CaretInside()
    {
        var b = new PromptBuffer();
        _ = MarkdownEditOps.ToggleWrap(b, "*");

        await Assert.That(b.SnapshotText()).IsEqualTo("**");
        await Assert.That(b.Cursor).IsEqualTo(1);
    }

    [Test]
    public async Task ToggleHeading_PlainLine_Prepends_And_TogglesOff()
    {
        var b = NewAt("title body", 3);
        _ = MarkdownEditOps.ToggleHeading(b);

        await Assert.That(b.SnapshotText()).IsEqualTo("# title body");
        await Assert.That(b.Cursor).IsEqualTo(2);

        _ = MarkdownEditOps.ToggleHeading(b);
        await Assert.That(b.SnapshotText()).IsEqualTo("title body");
    }

    [Test]
    public async Task ToggleHeading_OnLevelTwo_CollapsesWholeHashRun()
    {
        var b = NewAt("## deep", 0);
        _ = MarkdownEditOps.ToggleHeading(b);

        await Assert.That(b.SnapshotText()).IsEqualTo("deep");
    }

    [Test]
    public async Task ToggleListItem_TogglesDashPrefix()
    {
        var b = NewAt("item", 1);
        _ = MarkdownEditOps.ToggleListItem(b);
        await Assert.That(b.SnapshotText()).IsEqualTo("- item");

        _ = MarkdownEditOps.ToggleListItem(b);
        await Assert.That(b.SnapshotText()).IsEqualTo("item");
    }

    [Test]
    public async Task LinePrefixOps_OnlyTouchCaretLine()
    {
        var b = NewAt("one\ntwo\nthree", 6); // caret on "two"
        _ = MarkdownEditOps.ToggleListItem(b);

        await Assert.That(b.SnapshotText()).IsEqualTo("one\n- two\nthree");
        await Assert.That(b.Cursor).IsEqualTo(6);
    }
}
