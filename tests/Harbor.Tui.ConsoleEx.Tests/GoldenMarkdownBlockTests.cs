using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// CE-3 goldens for the styled markdown block painted into the cell grid
/// (headings, inline bold/italic/code, bullets, fences).
/// </summary>
public class GoldenMarkdownBlockTests
{
    [Test]
    public async Task MarkdownBlock_Grid_Golden()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var engine = new DiffEngine(64, 10);
        var back = new ScreenBuffer(64, 10);

        const string doc = """
            # Release notes

            The renderer is **fast** and *safe*: `zero` allocs.
            - item one
            - item two

            ```
            literal fence body
            ```
            """;

        var block = new AssistantMarkdownBlock(doc);
        var measure = block.Measure(64);
        back.Fill(new Rect(0, 0, 64, measure.MinLines), Cell.Blank);
        block.Paint(new BlockPaintContext(back, new Rect(0, 0, 64, measure.MinLines), tick: 0));

        writer.BeginFrame();
        engine.Flush(back, writer);
        await writer.EndFrameAsync();

        string doc2 = GoldenDoc.Build("ce3-markdown-block", back, backend);
        string expected = Golden.Verify("ce3-markdown-block", doc2, GridDump.ToSvg(back));
        await Assert.That(doc2).IsEqualTo(expected);
        await Assert.That(engine.FrontMatches(back)).IsTrue();

        // Style spot checks on the live buffer.
        bool sawBold = false, sawCode = false, sawFence = false, sawHeading = false;
        for (int y = 0; y < back.Rows; y++)
        {
            for (int x = 0; x < back.Cols; x++)
            {
                var st = back.Get(x, y).Style;
                sawBold |= (st.Attrs & StyleAttr.Bold) != 0 && st.Fg == default;
                sawCode |= st.Fg == PackedColor.Indexed(3);
                sawFence |= st.Attrs == StyleAttr.Dim || st == ChatPalette.Dim;
                sawHeading |= st.Fg == PackedColor.Indexed(4) && (st.Attrs & StyleAttr.Bold) != 0;
            }
        }

        await Assert.That(sawHeading).IsTrue();
        await Assert.That(sawBold).IsTrue();
        await Assert.That(sawCode).IsTrue();
        await Assert.That(sawFence).IsTrue();
    }
}
