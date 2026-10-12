namespace Harbor.Tui.CellForge.Tests;

public class MarkupCellsTests
{
    [Test]
    public async Task Parse_PlainText_SingleRun()
    {
        var runs = EngineCells.MarkupCells.Parse("hello");
        await Assert.That(runs.Count).IsEqualTo(1);
        await Assert.That(runs[0].Text).IsEqualTo("hello");
        await Assert.That(runs[0].Style.IsPlain).IsTrue();
        await Assert.That(runs[0].LinkTarget).IsNull();
    }

    [Test]
    public async Task Parse_BoldAndDim_SetAttrs()
    {
        var runs = EngineCells.MarkupCells.Parse("[bold]b[/] [dim]d[/]");
        await Assert.That(runs.Count).IsEqualTo(3);
        await Assert.That((runs[0].Style.Attrs & EngineCells.StyleAttr.Bold)).IsEqualTo(EngineCells.StyleAttr.Bold);
        await Assert.That(runs[1].Style.IsPlain).IsTrue();
        await Assert.That((runs[2].Style.Attrs & EngineCells.StyleAttr.Dim)).IsEqualTo(EngineCells.StyleAttr.Dim);
    }

    [Test]
    public async Task Parse_Link_CarriesTargetAndUnderline()
    {
        var runs = EngineCells.MarkupCells.Parse("[link=https://x]go[/]");
        await Assert.That(runs.Count).IsEqualTo(1);
        await Assert.That(runs[0].LinkTarget).IsEqualTo("https://x");
        await Assert.That((runs[0].Style.Attrs & EngineCells.StyleAttr.Underline)).IsEqualTo(EngineCells.StyleAttr.Underline);
    }

    [Test]
    public async Task Parse_Nested_CombinesAttrs()
    {
        var runs = EngineCells.MarkupCells.Parse("[bold]a[dim]b[/]c[/]");
        await Assert.That(runs.Count).IsEqualTo(3);
        await Assert.That((runs[1].Style.Attrs & (EngineCells.StyleAttr.Bold | EngineCells.StyleAttr.Dim)))
            .IsEqualTo(EngineCells.StyleAttr.Bold | EngineCells.StyleAttr.Dim);
    }

    [Test]
    public async Task Parse_UnknownTag_StaysLiteral()
    {
        await Assert.That(EngineCells.MarkupCells.Strip("[blue]hi[/]")).IsEqualTo("[blue]hi[/]");
    }

    [Test]
    public async Task Escape_RoundTrips()
    {
        await Assert.That(EngineCells.MarkupCells.Strip(EngineCells.MarkupCells.Escape("[bold]"))).IsEqualTo("[bold]");
    }

    [Test]
    public async Task MeasureWidth_WideRune_CountsTwo()
    {
        await Assert.That(EngineCells.MarkupCells.MeasureWidth("[bold]\u6F22[/]")).IsEqualTo(2);
    }

    [Test]
    public async Task Paint_WritesCellsAndLinkRange()
    {
        var buffer = new EngineCells.ScreenBuffer(10, 1);
        var links = new List<EngineCells.MarkupLinkRange>();
        int w = EngineCells.MarkupCells.Paint(buffer, 0, 0, "a[link=u]bc[/]", links: links);
        await Assert.That(w).IsEqualTo(3);
        await Assert.That(links.Count).IsEqualTo(1);
        await Assert.That(links[0].X).IsEqualTo(1);
        await Assert.That(links[0].Length).IsEqualTo(2);
        await Assert.That(buffer.Get(1, 0).Style.Attrs.HasFlag(EngineCells.StyleAttr.Underline)).IsTrue();
    }
}
