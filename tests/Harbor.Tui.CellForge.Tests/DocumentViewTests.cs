using System.Linq;
using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Golden-style coverage for the generic document viewer (#2375): the painted
/// grid is compared to an inline snapshot (content + padding), and the token
/// adapters are checked against the shipped markdown/HTML subsets. The pager,
/// wrap re-layout and clipping paths are driven through the public API only.
/// </summary>
// Rendering reads the global terminal palette; keep these serialized with the
// other grid goldens.
[NotInParallel("pty")]
public class DocumentViewTests
{
    /// <summary>Builds the exact string TestRender.Art returns for padded rows.</summary>
    private static string Gold(int width, params string[] rows) =>
        string.Join("\n", rows.Select(r => r.PadRight(width))) + "\n";

    private static string Text(DocumentLine line) => string.Concat(line.Spans.Select(s => s.Text));

    private static bool AnyCell(ScreenBuffer buffer, Func<Cell, bool> predicate)
    {
        for (int y = 0; y < buffer.Rows; y++)
        {
            for (int x = 0; x < buffer.Cols; x++)
            {
                if (predicate(buffer.Get(x, y)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Test]
    public async Task Gold_Markdown_Document_Paints_Heading_And_Inline_Bold()
    {
        const string doc = """
            # Release

            Use **fast** now.
            """;

        var view = new DocumentView();
        view.SetDocument(doc, DocumentFormat.Markdown, 16);
        view.Configure(16, 3);

        await Assert.That(view.TotalRows).IsEqualTo(2);
        await Assert.That(string.Join("|", view.Lines.Select(Text))).IsEqualTo("Release|Use fast now.");

        var back = TestRender.Buffer(16, 3);
        view.Paint(back, new Rect(0, 0, 16, 3));

        string expected = Gold(16, "Release", "Use fast now.", "");
        await Assert.That(TestRender.Art(back)).IsEqualTo(expected);

        await Assert.That(AnyCell(back, c =>
            c.Style.Fg == PackedColor.Indexed(4) && (c.Style.Attrs & StyleAttr.Bold) != 0)).IsTrue();
        await Assert.That(AnyCell(back, c =>
            c.Style.Fg == default && (c.Style.Attrs & StyleAttr.Bold) != 0)).IsTrue();
    }

    [Test]
    public async Task Gold_Html_Document_Paints_Block_And_Inline_Styles()
    {
        const string html = """
            <h1>Docs</h1>
            <p>A <b>bold</b> word and <i>italics</i>.</p>
            <ul>
              <li>alpha</li>
              <li>beta</li>
            </ul>
            """;

        var view = new DocumentView();
        view.SetDocument(html, DocumentFormat.Html, 24);
        view.Configure(24, 5);

        await Assert.That(view.TotalRows).IsEqualTo(4);
        await Assert.That(string.Join("|", view.Lines.Select(Text)))
            .IsEqualTo("Docs|A bold word and italics.|- alpha|- beta");

        var back = TestRender.Buffer(24, 5);
        view.Paint(back, new Rect(0, 0, 24, 5));

        string expected = Gold(24, "Docs", "A bold word and italics.", "- alpha", "- beta", "");
        await Assert.That(TestRender.Art(back)).IsEqualTo(expected);

        await Assert.That(AnyCell(back, c =>
            c.Style.Fg == PackedColor.Indexed(4) && (c.Style.Attrs & StyleAttr.Bold) != 0)).IsTrue();
        await Assert.That(AnyCell(back, c => (c.Style.Attrs & StyleAttr.Italic) != 0)).IsTrue();
        await Assert.That(AnyCell(back, c =>
            c.Style.Fg == PackedColor.Indexed(4) && (c.Style.Attrs & StyleAttr.Bold) == 0)).IsTrue();
    }

    [Test]
    public async Task Markdown_Adapter_Reuses_CodeSyntaxTokenizer_For_Fences()
    {
        const string doc = """
            ```csharp
            int x = 1; // note
            ```
            """;

        var lines = DocumentAdapters.AdaptMarkdown(doc, 40);

        await Assert.That(lines.Count).IsEqualTo(3);
        await Assert.That(Text(lines[0])).IsEqualTo("```csharp");
        await Assert.That(Text(lines[2])).IsEqualTo("```");
        await Assert.That(lines[0].Spans[0].Style).IsEqualTo(ChatPalette.Dim);

        var body = lines[1];
        await Assert.That(Text(body)).IsEqualTo("int x = 1; // note");
        await Assert.That(body.Spans.Any(s => s.Style != CellStyle.Plain)).IsTrue();
    }

    [Test]
    public async Task Html_Adapter_Decodes_Entities_And_Breaks()
    {
        var lines = DocumentAdapters.AdaptHtml("<p>one<br>two &amp; &lt;three&gt;</p>", 40);

        await Assert.That(string.Join("|", lines.Select(Text))).IsEqualTo("one|two & <three>");
    }

    [Test]
    public async Task Pager_Navigation_Clamps_To_The_Window()
    {
        var view = new DocumentView();
        view.SetLines(Enumerable.Range(0, 20)
            .Select(i => new DocumentLine([new DocumentSpan("L" + i, CellStyle.Plain)]))
            .ToArray());
        view.Configure(8, 5);

        await Assert.That(view.TotalRows).IsEqualTo(20);
        await Assert.That(view.ScrollY).IsEqualTo(0);
        await Assert.That(view.AtTop).IsTrue();

        view.PageDown(5);
        view.PageDown(5);
        await Assert.That(view.ScrollY).IsEqualTo(8L); // step = viewport - 1

        view.PageUp(5);
        await Assert.That(view.ScrollY).IsEqualTo(4L);

        view.ScrollToEnd();
        await Assert.That(view.ScrollY).IsEqualTo(15L); // total - viewport
        await Assert.That(view.AtBottom).IsTrue();

        view.PageDown(5);
        await Assert.That(view.ScrollY).IsEqualTo(15L);
        view.PageUp(5);
        view.PageUp(5);
        view.PageUp(5);
        view.PageUp(5);
        await Assert.That(view.ScrollY).IsEqualTo(0L);
        await Assert.That(view.AtTop).IsTrue();
    }

    [Test]
    public async Task Paint_Clips_To_The_Rect_And_Starts_At_The_Offset()
    {
        var view = new DocumentView();
        view.SetLines(Enumerable.Range(0, 20)
            .Select(i => new DocumentLine([new DocumentSpan("L" + i, CellStyle.Plain)]))
            .ToArray());
        view.Configure(4, 2);
        view.ScrollToEnd();

        var back = TestRender.Buffer(4, 2);
        view.Paint(back, new Rect(0, 0, 4, 2));

        await Assert.That(TestRender.Art(back)).IsEqualTo(Gold(4, "L18", "L19"));
    }

    [Test]
    public async Task Width_Change_Rewraps_A_Source_Backed_Document()
    {
        var view = new DocumentView();
        view.SetDocument("abcdefghij", DocumentFormat.Plain, 4);

        await Assert.That(view.TotalRows).IsEqualTo(3);
        await Assert.That(string.Join("|", view.Lines.Select(Text))).IsEqualTo("abcd|efgh|ij");

        view.Configure(5, 3);

        await Assert.That(view.Width).IsEqualTo(5);
        await Assert.That(view.TotalRows).IsEqualTo(2);
        await Assert.That(string.Join("|", view.Lines.Select(Text))).IsEqualTo("abcde|fghij");
    }

    [Test]
    public async Task Empty_And_Null_Sources_Are_Safe()
    {
        foreach (var format in new[] { DocumentFormat.Plain, DocumentFormat.Markdown, DocumentFormat.Html })
        {
            var lines = DocumentAdapters.Adapt(null, format, 10);
            await Assert.That(lines.Count).IsEqualTo(1);
            await Assert.That(Text(lines[0])).IsEmpty();
        }

        var view = new DocumentView();
        view.SetDocument(null, DocumentFormat.Html, 10);
        view.Configure(10, 4);
        var back = TestRender.Buffer(10, 4);
        view.Paint(back, new Rect(0, 0, 10, 4)); // no throw
        await Assert.That(view.TotalRows).IsEqualTo(1);
    }
}
