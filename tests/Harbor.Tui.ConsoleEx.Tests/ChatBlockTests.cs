using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

public class ChatBlockTests
{
    [Test]
    public async Task UserBlock_Measure_WrapsBodyUnderPrefix()
    {
        var block = new UserBlock("hello brave new world");
        // prefix "› " takes 2 columns; body wraps at width-2.
        await Assert.That(block.Measure(12).IsExact).IsTrue();
        await Assert.That(block.Measure(12).MinLines).IsEqualTo(3);
        await Assert.That(block.Measure(80).MinLines).IsEqualTo(1);
    }

    [Test]
    public async Task UserBlock_Paint_PrefixOnlyOnFirstLine()
    {
        var buffer = new ScreenBuffer(14, 4);
        var block = new UserBlock("hello world again");
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 14, 4), tick: 0));

        string art = GridDump.Art(buffer);
        var rows = art.Split('\n');
        await Assert.That(rows[0].TrimEnd()).IsEqualTo("› hello world");
        await Assert.That(rows[1].TrimEnd()).IsEqualTo("  again"); // continuation aligns under body
        // Prefix must not repeat on continuation rows.
        await Assert.That(rows[1].StartsWith('›')).IsFalse();
        await Assert.That(string.IsNullOrWhiteSpace(rows[2])).IsTrue();
    }

    [Test]
    public async Task SystemBlock_PaintsDimItalic()
    {
        var buffer = new ScreenBuffer(20, 1);
        var block = new SystemBlock("compacting…");
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 20, 1), 0));

        var cell = buffer.Get(0, 0);
        await Assert.That((int)(cell.Style.Attrs & StyleAttr.Dim)).IsNotEqualTo(0);
        await Assert.That((int)(cell.Style.Attrs & StyleAttr.Italic)).IsNotEqualTo(0);
        await Assert.That(block.RawText()).IsEqualTo("compacting…");
    }

    [Test]
    public async Task ToolCallBlock_Running_IsSingleHeaderLine()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "bash", "ls -la"));
        await Assert.That(block.Status).IsEqualTo(ToolCallStatus.Running);
        var m = block.Measure(40);
        await Assert.That((m.MinLines, m.MaxLines)).IsEqualTo((1, 1));
    }

    [Test]
    public async Task ToolCallBlock_Complete_ShowsStatusDurationAndCollapsedBody()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "read", "src/a.cs"))
        {
            MaxBodyLines = 2,
        };
        block.Complete(new ToolResultBody("l1\nl2\nl3\n", isError: false, TimeSpan.FromMilliseconds(850)));

        var m = block.Measure(60);
        await Assert.That(m.MinLines).IsEqualTo(1 /*header*/ + 2 /*body*/ + 1 /*continuation*/);

        var buffer = new ScreenBuffer(40, m.MinLines);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, m.MinLines), 0));
        string art = GridDump.Art(buffer);

        await Assert.That(art).Contains("✔ read (850ms)");
        await Assert.That(art).Contains("l1");
        await Assert.That(art).Contains("…"); // collapsed continuation
        await Assert.That(art).DoesNotContain("l3");
    }

    [Test]
    public async Task ToolCallBlock_Error_UsesErrorStyleAndFirstResultWins()
    {
        var block = new ToolCallBlock(new ToolCallInfo("t2", "edit", ""));
        block.Complete(new ToolResultBody("boom", isError: true, TimeSpan.FromMilliseconds(5)));
        block.Complete(new ToolResultBody("second", isError: false, TimeSpan.FromMilliseconds(9)));

        await Assert.That(block.Status).IsEqualTo(ToolCallStatus.Error);
        await Assert.That(block.Body!.Output).IsEqualTo("boom");

        var buffer = new ScreenBuffer(30, 2);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 30, 2), 0));
        await Assert.That(GridDump.Art(buffer)).Contains("✖ edit (5ms)");
    }

    [Test]
    public async Task ToolCallBlock_DiffBody_ReplacesPlainText()
    {
        var diff = "--- a/f.cs\n+++ b/f.cs\n@@ -1,1 +1,2 @@\n-old\n+new\n ctx";
        var block = new ToolCallBlock(new ToolCallInfo("t3", "patch", "f.cs"));
        block.Complete(new ToolResultBody("", isError: false, TimeSpan.Zero, diffText: diff));

        int expected = 1 + 6; // header + 6 diff lines
        await Assert.That(block.Measure(50).MinLines).IsEqualTo(expected);
    }

    [Test]
    public async Task AssistantMarkdownBlock_SkeletonWrapsPlainLines()
    {
        var block = new AssistantMarkdownBlock("# Title\ntext line");
        await Assert.That(block.Kind).IsEqualTo("assistant");
        await Assert.That(block.Measure(20).MinLines).IsEqualTo(2);
        await Assert.That(block.RawText()).Contains("# Title");
    }

    [Test]
    public async Task FormatDuration_HumanBuckets()
    {
        await Assert.That(ToolResultBody.FormatDuration(TimeSpan.FromMicroseconds(400))).IsEqualTo("<1ms");
        await Assert.That(ToolResultBody.FormatDuration(TimeSpan.FromMilliseconds(250))).IsEqualTo("250ms");
        await Assert.That(ToolResultBody.FormatDuration(TimeSpan.FromSeconds(2.34))).IsEqualTo("2.3s");
    }
}
