using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
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

    /// <summary>True when any cell in <c>[x0, x1)</c> of a row carries that foreground.</summary>
    private static bool HasFg(ScreenBuffer buffer, int row, int x0, int x1, CellStyle style)
    {
        for (int x = x0; x < x1; x++)
        {
            if (buffer.Get(x, row).Style.Fg == style.Fg)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One context/delete/add/context hunk, numbered from <paramref name="hunkStart"/>.</summary>
    private static string DiffAt(int hunkStart) => $"""
        --- a/src/app.cs
        +++ b/src/app.cs
        @@ -{hunkStart},4 +{hunkStart},4 @@ namespace App;
         context line
        -removed line
        +added line
         more context
        """;

    /// <summary>
    /// #737: the body is painted at <c>Rect.X + GutterWidth</c>, so the gutter
    /// is a fixed-size column and the number fields are what have to give,
    /// never the width. The fields were <c>PadLeft(4)</c>, which only ever
    /// grows — under 10 000 a row came out at exactly
    /// <see cref="DiffBlock.GutterWidth"/> cells, and this test's old fixture
    /// (rows 10..13) plus its sibling (rows 1..4) sat in that safe zone, so
    /// the guard had never once run on the input that breaks it. From 10 005
    /// up the gutter outgrew the constant and the body painted over the
    /// numbers' trailing gap.
    /// </summary>
    /// <remarks>
    /// Every number length is a case, not just the 5-digit one: a fix that
    /// clamps the long rows while shifting the 2- and 3-digit alignment is
    /// worse than the bug it closes.
    /// </remarks>
    [Test]
    [Arguments(1)]
    [Arguments(9)]
    [Arguments(12)]
    [Arguments(123)]
    [Arguments(1234)]
    [Arguments(12345)]
    [Arguments(100005)]
    public async Task Gutter_Alignment_IsFixedWidth_AtEveryNumberLength(int hunkStart)
    {
        var lines = UnifiedDiffParser.Parse(DiffAt(hunkStart));
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

    /// <summary>
    /// The same invariant in the form a reader sees it. Painted, a body row is
    /// a number column, then the two-cell gap, then the sign — and the body
    /// starts after the sign. On a 5-digit hunk the ungrown fields ate that
    /// gap: the body was drawn at +11 regardless of how wide the gutter
    /// actually was, so the numbers ran into it with no blank between them.
    /// </summary>
    [Test]
    public async Task PaintedGutter_KeepsTheGapBeforeTheSign_OnFiveDigitHunks()
    {
        const int hunkStart = 10005;
        var lines = UnifiedDiffParser.Parse(DiffAt(hunkStart));
        var buffer = new ScreenBuffer(48, lines.Count);
        new DiffBlock(DiffAt(hunkStart))
            .Paint(new BlockPaintContext(buffer, new Rect(0, 0, 48, lines.Count), 0));

        for (int y = 0; y < lines.Count; y++)
        {
            var dl = lines[y];
            if (dl.Kind is DiffLineKind.HunkHeader or DiffLineKind.FileHeader)
            {
                continue; // headers carry their own markers, no sign column
            }

            for (int x = 0; x < DiffBlock.GutterWidth; x++)
            {
                char cell = (char)buffer.Get(x, y).Rune;
                await Assert.That(cell == ' ' || char.IsAsciiDigit(cell))
                    .IsTrue(); // anything else here is a body cell inside the gutter
            }

            for (int x = DiffBlock.GutterWidth - 2; x < DiffBlock.GutterWidth; x++)
            {
                await Assert.That((char)buffer.Get(x, y).Rune).IsEqualTo(' ');
            }

            char sign = dl.Kind switch
            {
                DiffLineKind.Add => '+',
                DiffLineKind.Delete => '-',
                _ => ' ',
            };
            await Assert.That((char)buffer.Get(DiffBlock.GutterWidth, y).Rune).IsEqualTo(sign);
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

    [Test]
    public async Task InsertionWithinLine_AccentsAddedTokensOnly()
    {
        const string sample = """
            @@ -1,1 +1,1 @@
            -git commit
            +git commit --amend
            """;

        var block = new DiffBlock(sample);
        var buffer = new ScreenBuffer(48, 3);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 48, 3), 0));

        int signCol = DiffBlock.GutterWidth;
        // Delete row carries no deletions: every body cell stays dim context.
        bool sawError = false;
        for (int x = signCol + 1; x < 48; x++)
        {
            sawError |= buffer.Get(x, 1).Style.Fg == ChatPalette.ToolError.Fg;
        }

        // Add row: "git commit" dim, " --amend" takes the add accent.
        // Body starts at signCol+1; "git commit" is 10 cells + one blank separator.
        await Assert.That(buffer.Get(signCol + 1, 2).Style.Fg).IsEqualTo(ChatPalette.ToolBody.Fg);
        await Assert.That(buffer.Get(signCol + 12, 2).Style.Fg).IsEqualTo(ChatPalette.ToolOk.Fg);
        await Assert.That(sawError).IsFalse();
    }

    [Test]
    public async Task RewriteRun_2To3_MarksChangedTokensAndLeavesLeftoversPlain()
    {
        // #380: a run of 2 deletes rewritten into 3 adds. Only the two
        // genuine rewrites get word emphasis; the net-new add stays a plain
        // line-level row.
        const string sample = """
            @@ -1,2 +1,3 @@
            -var port = 8080
            -var listen = host
            +var port = 9090
            +var bind = host
            +var listen = host
             trailing context
            """;

        var block = new DiffBlock(sample);
        var buffer = new ScreenBuffer(48, UnifiedDiffParser.Parse(sample).Count);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 48, buffer.Rows), 0));

        // Rows: 0 header, 1-2 deletes, 3-5 adds, 6 context. Body starts at
        // signCol+1; "var port =" is 10 cells plus one blank separator, so the
        // changed token lands at +11 from the body start.
        int body = DiffBlock.GutterWidth + 1;
        int changed = body + 11;

        // Delete pair: context dim, the rewritten number in the delete accent.
        await Assert.That(buffer.Get(body, 1).Style.Fg).IsEqualTo(ChatPalette.ToolBody.Fg);
        await Assert.That(buffer.Get(changed, 1).Style.Fg).IsEqualTo(ChatPalette.ToolError.Fg);

        // Add pair: same layout on the add side, accent flipped.
        await Assert.That(buffer.Get(body, 3).Style.Fg).IsEqualTo(ChatPalette.ToolBody.Fg);
        await Assert.That(buffer.Get(changed, 3).Style.Fg).IsEqualTo(ChatPalette.ToolOk.Fg);

        // The unpaired add row paints line-level: a segmented row always has
        // at least one dim context run, so "no dim cell" means unpaired.
        await Assert.That(HasFg(buffer, 4, body, 48, ChatPalette.ToolBody)).IsFalse();
        await Assert.That(GridDump.Art(buffer)).Contains("var bind = host");
    }

    [Test]
    public async Task RewriteRun_UnrelatedRows_PaintLineLevelOnly()
    {
        // 2 deletes into 3 adds with no shared tokens: nothing anchors, so the
        // whole run stays line-level instead of being rainbowed.
        const string sample = """
            @@ -1,2 +1,3 @@
            -zzz qqq xxx
            -www vvv uuu
            +aaa bbb ccc
            +ddd eee fff
            +ggg hhh iii
            """;

        var block = new DiffBlock(sample);
        var buffer = new ScreenBuffer(40, UnifiedDiffParser.Parse(sample).Count);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, buffer.Rows), 0));

        int body = DiffBlock.GutterWidth + 1;
        for (int row = 1; row <= 5; row++)
        {
            await Assert.That(HasFg(buffer, row, body, 40, ChatPalette.ToolBody)).IsFalse();
        }

        // Whole rows keep their line-level kind colour.
        await Assert.That(buffer.Get(body, 1).Style.Fg).IsEqualTo(ChatPalette.ToolError.Fg);
        await Assert.That(buffer.Get(body, 3).Style.Fg).IsEqualTo(ChatPalette.ToolOk.Fg);
        await Assert.That(GridDump.Art(buffer)).Contains("zzz qqq xxx");
    }

    [Test]
    public async Task RewriteRun_SingleLineOver4Kb_PaintsLineLevelOnly()
    {
        // Guardrail: a minified row past WordDiff.MaxPairableLineChars must not
        // reach the O(tokens²) LCS matrix, and paints as a plain row.
        string huge = new('a', WordDiff.MaxPairableLineChars + 1);
        string sample = $"@@ -1,1 +1,2 @@\n-{huge}\n+totally different text\n+one more line\n";

        var block = new DiffBlock(sample);
        var buffer = new ScreenBuffer(60, UnifiedDiffParser.Parse(sample).Count);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 60, buffer.Rows), 0));

        int body = DiffBlock.GutterWidth + 1;
        for (int row = 1; row <= 3; row++)
        {
            await Assert.That(HasFg(buffer, row, body, 60, ChatPalette.ToolBody)).IsFalse();
        }

        await Assert.That(GridDump.Art(buffer)).Contains("totally different text");
    }

    [Test]
    public async Task DeletionWithinLine_AccentsDeletedTokensOnly()
    {
        const string sample = """
            @@ -1,1 +1,1 @@
            -git commit --amend
            +git commit
            """;

        var block = new DiffBlock(sample);
        var buffer = new ScreenBuffer(48, 3);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 48, 3), 0));

        int signCol = DiffBlock.GutterWidth;
        // Delete row: "git commit" dim, " --amend" takes the delete accent.
        await Assert.That(buffer.Get(signCol + 1, 1).Style.Fg).IsEqualTo(ChatPalette.ToolBody.Fg);
        await Assert.That(buffer.Get(signCol + 12, 1).Style.Fg).IsEqualTo(ChatPalette.ToolError.Fg);

        // Add row carries no additions: no add accent in its body.
        bool sawOk = false;
        for (int x = signCol + 1; x < 48; x++)
        {
            sawOk |= buffer.Get(x, 2).Style.Fg == ChatPalette.ToolOk.Fg;
        }

        await Assert.That(sawOk).IsFalse();
    }
}

/// <summary>Golden grid-dump of the diff block painted through the real pipeline.</summary>
// Serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
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
        var eng = GridDump.ToEngine(back);
        engine.Flush(eng, writer);
        await writer.EndFrameAsync();

        string doc = GoldenDoc.Build("ce3-diff-block", back, backend);
        string expected = Golden.Verify("ce3-diff-block", doc, GridDump.ToSvg(back));
        await Assert.That(doc).IsEqualTo(expected);
        await Assert.That(engine.FrontMatches(eng)).IsTrue();
    }
}
