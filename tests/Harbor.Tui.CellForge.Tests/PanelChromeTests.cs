using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// #58: serialized vs theme tests — rendering reads global TerminalColorPalette.
[NotInParallel("pty")]
public class PanelChromeTests
{
    // ── Palette-only colors (no hardcoded hex / Indexed literals) ───────────

    [Test]
    public async Task ChromeStyles_ComeFromPaletteSlots()
    {
        await Assert.That(PanelChrome.BorderStyle.Fg).IsEqualTo(ChatPalette.Border);
        await Assert.That(PanelChrome.TitleStyle.Fg).IsEqualTo(ChatPalette.Accent);
        await Assert.That(PanelChrome.PanelBackground).IsEqualTo(ChatPalette.Panel);
        await Assert.That(PanelChrome.SurfaceBackground).IsEqualTo(ChatPalette.Surface);
        await Assert.That(PanelChrome.SeparatorStyle).IsEqualTo(ChatPalette.Dim);
    }

    [Test]
    public async Task TitleStyle_IsBoldAccent()
    {
        await Assert.That(PanelChrome.TitleStyle.Fg.IsDefault).IsFalse();
        await Assert.That((int)(PanelChrome.TitleStyle.Attrs & StyleAttr.Bold)).IsNotEqualTo(0);
    }

    // ── Border rules ────────────────────────────────────────────────────────

    [Test]
    public async Task PaintTopRule_DrawsBorderGlyphsInBorderColor()
    {
        var buffer = new ScreenBuffer(10, 3);
        PanelChrome.PaintTopRule(buffer, new Rect(0, 0, 10, 3));

        string art = GridDump.Art(buffer);
        await Assert.That(art.Split('\n')[0]).IsEqualTo(new string('─', 10));

        for (int x = 0; x < 10; x++)
        {
            await Assert.That(buffer.Get(x, 0).Style.Fg).IsEqualTo(ChatPalette.Border);
        }
    }

    [Test]
    public async Task PaintBottomRule_DrawsOnLastRowOnly()
    {
        var buffer = new ScreenBuffer(8, 3);
        PanelChrome.PaintBottomRule(buffer, new Rect(0, 0, 8, 3));

        string art = GridDump.Art(buffer);
        var rows = art.Split('\n');
        await Assert.That(rows[2]).IsEqualTo(new string('─', 8));
        await Assert.That(rows[0].Trim()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task PaintLeftRule_DrawsSideGlyphs()
    {
        var buffer = new ScreenBuffer(6, 4);
        PanelChrome.PaintLeftRule(buffer, new Rect(0, 0, 6, 4));

        string art = GridDump.Art(buffer);
        foreach (var row in art.Split('\n'))
        {
            await Assert.That(row[0]).IsEqualTo('│');
        }
    }

    // ── Title rows ──────────────────────────────────────────────────────────

    [Test]
    public async Task PaintTitleRow_ShowsTitleWithRules()
    {
        var buffer = new ScreenBuffer(20, 1);
        PanelChrome.PaintTitleRow(buffer, 0, 0, 20, "INPUT");

        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("INPUT");
        await Assert.That(art).Contains("─");

        // Title cells carry the accent-bold style, rule cells the border tone.
        await Assert.That(buffer.Get(2, 0).Style).IsEqualTo(PanelChrome.TitleStyle);
        await Assert.That(buffer.Get(0, 0).Style.Fg).IsEqualTo(ChatPalette.Border);
    }

    [Test]
    public async Task PaintTitleRow_TinyRects_NoThrow()
    {
        var buffer = new ScreenBuffer(4, 2);
        PanelChrome.PaintTitleRow(buffer, 0, 0, 0, "INPUT");
        PanelChrome.PaintTitleRow(buffer, 0, 0, 2, "INPUT");
        PanelChrome.PaintTitleRow(buffer, 0, 0, 4, string.Empty);
        PanelChrome.PaintTopRule(buffer, new Rect(0, 0, 0, 0));
        PanelChrome.PaintBottomRule(buffer, new Rect(0, 0, 0, 0));
        PanelChrome.PaintLeftRule(buffer, new Rect(0, 0, 0, 0));
        PanelChrome.PaintMessageSeparator(buffer, 0, 0, 0);
        PanelChrome.FillPanelBackground(buffer, new Rect(0, 0, 0, 0));
        await Assert.That(true).IsTrue();
    }

    // ── Composer chrome ─────────────────────────────────────────────────────

    [Test]
    public async Task ComposerPanel_ChromeOff_PaintsLegacyLayout()
    {
        var composer = new ComposerController();
        composer.Buffer.InsertText("hello");
        var panel = new ComposerPanel("c", composer, 10, 3);

        var buffer = new ScreenBuffer(10, 3);
        panel.Rect = new Rect(0, 0, 10, 3);
        panel.Paint(buffer);

        string art = GridDump.Art(buffer);
        await Assert.That(art.Split('\n')[0]).Contains("hello");
    }

    [Test]
    public async Task ComposerPanel_ChromeOn_ShowsInputTitleAndRules()
    {
        var composer = new ComposerController();
        composer.Buffer.InsertText("hello");
        var panel = new ComposerPanel("c", composer, 10, 4) { ShowChrome = true };

        var buffer = new ScreenBuffer(10, 4);
        panel.Rect = new Rect(0, 0, 10, 4);
        panel.Paint(buffer);

        string art = GridDump.Art(buffer);
        var rows = art.Split('\n');
        await Assert.That(rows[0]).Contains("INPUT");
        await Assert.That(rows[0]).Contains("─");
        await Assert.That(rows[1]).Contains("hello");
        await Assert.That(rows[3]).IsEqualTo(new string('─', 10));

        // Chrome rows sit on the Panel surface, title in accent.
        await Assert.That(buffer.Get(0, 0).Style.Fg).IsEqualTo(ChatPalette.Border);
    }

    // ── Status chrome ───────────────────────────────────────────────────────

    [Test]
    public async Task StatusPanel_ChromeOn_FillsPanelBackground()
    {
        var status = new StatusViewModel { Model = "m", Mode = StatusBarMode.Idle };
        var panel = new StatusPanel("s", status, 10, 1) { ShowChrome = true };

        var buffer = new ScreenBuffer(30, 1);
        panel.Rect = new Rect(0, 0, 30, 1);
        panel.Paint(buffer);

        // Trailing gap cells (past the short segments) sit on the Panel
        // surface, not the default background.
        await Assert.That(buffer.Get(29, 0).Style.Bg).IsEqualTo(ChatPalette.Panel);
    }

    // ── Sidebar title ───────────────────────────────────────────────────────

    [Test]
    public async Task SideBarView_PaintsContextTitleRow()
    {
        var buffer = new ScreenBuffer(60, 24);
        var rect = new Rect(60 - SideBarLayout.DefaultWidth, 0, SideBarLayout.DefaultWidth, 23);
        SideBarView.Paint(buffer, rect, SideBarState.Empty);

        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("CONTEXT");
        await Assert.That(art).Contains("SESSION");
        await Assert.That(art).Contains("MODEL");
        await Assert.That(art).Contains("TOKENS");
    }

    // ── Feed separators ─────────────────────────────────────────────────────

    [Test]
    public async Task Timeline_SeparatorsOff_GapRowsStayBlank()
    {
        var buffer = new ScreenBuffer(30, 8);
        var tl = new VirtualizedChatTimeline();
        tl.Append(new UserBlock("first"));
        tl.Append(new AssistantMarkdownBlock("second"));
        _ = tl.PrepareFrame(30, 8);
        tl.Paint(buffer, new Rect(0, 0, 30, 8));

        string art = GridDump.Art(buffer);
        await Assert.That(art.Contains('─')).IsFalse();
    }

    [Test]
    public async Task Timeline_SeparatorsOn_DrawsDimDividers()
    {
        var buffer = new ScreenBuffer(30, 8);
        var tl = new VirtualizedChatTimeline { ShowSeparators = true };
        tl.Append(new UserBlock("first"));
        tl.Append(new AssistantMarkdownBlock("second"));
        _ = tl.PrepareFrame(30, 8);
        tl.Paint(buffer, new Rect(0, 0, 30, 8));

        string art = GridDump.Art(buffer);
        await Assert.That(art.Contains('─')).IsTrue();

        // Separator cells use the palette Dim tone — never a hardcoded color.
        string[] artRows = GridDump.Art(buffer).Split('\n');
        bool anyDimRule = false;
        for (int y = 0; y < 8 && !anyDimRule; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                if (buffer.Get(x, y).Style == ChatPalette.Dim && artRows[y][x] == '─')
                {
                    anyDimRule = true;
                    break;
                }
            }
        }

        await Assert.That(anyDimRule).IsTrue();
    }

    [Test]
    public async Task Blocks_EmptyAndUnicode_PaintSeparatorsWithoutThrow()
    {
        foreach (string text in new[] { string.Empty, " ", "привет 🌍\n日本語\t• tab", new string('x', 200) })
        {
            var ubuffer = new ScreenBuffer(24, 6);
            var user = new UserBlock(text);
            int uh = user.Measure(24).MinLines;
            user.Paint(new BlockPaintContext(ubuffer, new Rect(0, 0, 24, Math.Min(6, uh)), 0, showSeparators: true));

            var abuffer = new ScreenBuffer(24, 8);
            var assistant = new AssistantMarkdownBlock(text);
            int ah = assistant.Measure(24).MinLines;
            assistant.Paint(new BlockPaintContext(abuffer, new Rect(0, 0, 24, Math.Min(8, ah)), 0, showSeparators: true));

            var sbuffer = new ScreenBuffer(24, 3);
            var system = new SystemBlock(text);
            int sh = system.Measure(24).MinLines;
            system.Paint(new BlockPaintContext(sbuffer, new Rect(0, 0, 24, Math.Min(3, sh)), 0, showSeparators: true));
        }

        await Assert.That(true).IsTrue();
    }
}
