using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

public class DiffBlockTests
{
    private const string Sample = """
        diff --git a/src/app.cs b/src/app.cs
        index 83db48f..bf269f4 100644
        --- a/src/app.cs
        +++ b/src/app.cs
        @@ -10,7 +10,8 @@ namespace App;
         context line
        -removed line one
        -removed line two
        +added line
         more context
        """;

    [Test]
    public async Task Parse_ResolvesNumbersAndKinds()
    {
        var lines = UnifiedDiffParser.Parse(Sample);

        await Assert.That(lines.Count(l => l.Kind == DiffLineKind.FileHeader)).IsEqualTo(4);
        await Assert.That(lines.Count(l => l.Kind == DiffLineKind.HunkHeader)).IsEqualTo(1);
        await Assert.That(lines.Count(l => l.Kind == DiffLineKind.Add)).IsEqualTo(1);
        await Assert.That(lines.Count(l => l.Kind == DiffLineKind.Delete)).IsEqualTo(2);
        await Assert.That(lines.Count(l => l.Kind == DiffLineKind.Context)).IsEqualTo(2);

        var ctx = lines.First(l => l.Kind == DiffLineKind.Context);
        await Assert.That(ctx.OldNo).IsEqualTo(11);   // hunk starts at 10, first row consumed by "context"
        var del = lines.Where(l => l.Kind == DiffLineKind.Delete).ToList();
        await Assert.That(del[0].OldNo).IsEqualTo(12);
        await Assert.That(del[1].OldNo).IsEqualTo(13);
        var add = lines.Single(l => l.Kind == DiffLineKind.Add);
        await Assert.That(add.NewNo).IsEqualTo(12);
    }

    [Test]
    public async Task Parse_NonDiff_ReturnsEmpty()
    {
        await Assert.That(UnifiedDiffParser.LooksLikeDiff("Wrote 12 lines to file")).IsFalse();
        await Assert.That(UnifiedDiffParser.Parse("Wrote 12 lines to file")).IsEmpty();
        await Assert.That(UnifiedDiffParser.LooksLikeDiff("@@ -1,2 +3,4 @@")).IsTrue();
    }

    [Test]
    public async Task Measure_EqualsParsedRowCount()
    {
        var block = new DiffBlock(Sample);
        int rows = UnifiedDiffParser.Parse(Sample).Count;
        await Assert.That(block.Measure(60).MinLines).IsEqualTo(rows);
    }

    [Test]
    public async Task Paint_GutterSignsAndColors()
    {
        var buffer = new ScreenBuffer(50, UnifiedDiffParser.Parse(Sample).Count);
        var block = new DiffBlock(Sample);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 50, buffer.Rows), 0));

        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("-removed line one");
        await Assert.That(art).Contains("+added line");
        await Assert.That(art).Contains(" context line");
        await Assert.That(art).Contains("@@ -10,7 +10,8 @@");

        // Delete rows carry the error accent, add rows the success accent —
        // compared against ChatPalette semantics, not raw palette indices,
        // so future truecolor/index token changes stay transparent.
        bool sawRed = false, sawGreen = false;
        for (int y = 0; y < buffer.Rows; y++)
        {
            for (int x = 0; x < buffer.Cols; x++)
            {
                var fg = buffer.Get(x, y).Style.Fg;
                sawRed |= fg == ChatPalette.ToolError.Fg;
                sawGreen |= fg == ChatPalette.ToolOk.Fg;
            }
        }

        await Assert.That(sawRed).IsTrue();
        await Assert.That(sawGreen).IsTrue();
    }

    [Test]
    public async Task Gutter_Alignment_IsFixedWidth()
    {
        var lines = UnifiedDiffParser.Parse(Sample);
        foreach (var dl in lines)
        {
            if (dl.Kind is DiffLineKind.Context or DiffLineKind.Add or DiffLineKind.Delete)
            {
                await Assert.That(DiffBlock.Gutter(dl).Length).IsEqualTo(DiffBlock.GutterWidth);
            }
            else
            {
                await Assert.That(DiffBlock.Gutter(dl)).IsEqualTo(new string(' ', DiffBlock.GutterWidth));
            }
        }
    }

    [Test]
    public async Task PairedDeleteAdd_WordDiff_AccentsChangedTokensOnly()
    {
        const string sample = """
            @@ -1,2 +1,2 @@
            -old line alpha
            +new line beta
             context tail
            """;

        var block = new DiffBlock(sample);
        var buffer = new ScreenBuffer(40, 4);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 4), 0));

        int signCol = DiffBlock.GutterWidth;
        // Row layout: 0 hunk header, 1 delete, 2 add, 3 context.
        CellStyle delChanged = buffer.Get(signCol + 2, 1).Style;
        CellStyle addChanged = buffer.Get(signCol + 2, 2).Style;
        CellStyle addContext = buffer.Get(signCol + 6, 2).Style;
        CellStyle addMark = buffer.Get(signCol + 10, 2).Style;

        await Assert.That(delChanged.Fg).IsEqualTo(ChatPalette.ToolError.Fg); // changed delete token
        await Assert.That(addChanged.Fg).IsEqualTo(ChatPalette.ToolOk.Fg);    // changed add token
        await Assert.That(addContext.Fg).IsEqualTo(ChatPalette.ToolBody.Fg);  // unchanged context
        await Assert.That(addMark.Fg).IsEqualTo(ChatPalette.ToolOk.Fg);       // '+' gutter sign

        // Unpaired context row keeps plain styling.
        await Assert.That(buffer.Get(signCol + 2, 3).Style.Fg)
            .IsEqualTo(CellStyle.Plain.Fg);
    }
}

/// <summary>Golden grid-dump of the diff block painted through the real pipeline.</summary>
public class GoldenDiffBlockTests
{
    [Test]
    public async Task DiffBlock_Grid_Golden()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var engine = new DiffEngine(56, 9);
        var back = new ScreenBuffer(56, 9);

        const string sample = """
            --- a/src/app.cs
            +++ b/src/app.cs
            @@ -10,7 +10,8 @@ namespace App;
             context line
            -removed line one
            -removed line two
            +added line
             more context
            """;

        var block = new DiffBlock(sample, path: "src/app.cs");
        var m = block.Measure(56);
        block.Paint(new BlockPaintContext(back, new Rect(0, 0, 56, m.MinLines), 0));

        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        string doc = GoldenDoc.Build("ce3-diff-block", back, backend);
        string expected = Golden.Verify("ce3-diff-block", doc, GridDump.ToSvg(back));
        await Assert.That(doc).IsEqualTo(expected);
        await Assert.That(engine.FrontMatches(back)).IsTrue();
    }
}
