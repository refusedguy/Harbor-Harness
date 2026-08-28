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

    [Test]
    public async Task ToggleWrap_MidWord_WrapsAndCaretLandsAfterOpeningMarker()
    {
        var b = NewAt("hello world", 1);
        _ = MarkdownEditOps.ToggleWrap(b, "**");

        await Assert.That(b.SnapshotText()).IsEqualTo("**hello** world");
        await Assert.That(b.Cursor).IsEqualTo(2);
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
    public async Task ToggleWrap_WhitespaceOnly_FallsBackToBarePair()
    {
        var b = NewAt("   ", 3);
        _ = MarkdownEditOps.ToggleWrap(b, "**");

        await Assert.That(b.SnapshotText()).IsEqualTo("   ****");
        await Assert.That(b.Cursor).IsEqualTo(5);
    }

    [Test]
    public async Task ToggleWrap_OnGapBetweenWords_WrapsNextWord()
    {
        var b = NewAt("a bb c", 1);
        _ = MarkdownEditOps.ToggleWrap(b, "~~");

        await Assert.That(b.SnapshotText()).IsEqualTo("a ~~bb~~ c");
        await Assert.That(b.Cursor).IsEqualTo(4);
    }

    [Test]
    public async Task ToggleWrap_CaretPastLastWord_WrapsThatWordSkippingTrailingSpace()
    {
        var b = NewAt("done todo ", 10);
        _ = MarkdownEditOps.ToggleWrap(b, "**");

        await Assert.That(b.SnapshotText()).IsEqualTo("done **todo** ");
        await Assert.That(b.Cursor).IsEqualTo(7);
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
