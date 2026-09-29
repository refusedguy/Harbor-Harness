using System.Text;
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
        foreach (var row in art.Split('\n', StringSplitOptions.RemoveEmptyEntries))
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
        PanelChrome.PaintBorderBox(buffer, new Rect(0, 0, 0, 0));
        PanelChrome.PaintBorderBox(buffer, new Rect(0, 0, 1, 1));
        PanelChrome.PaintBorderBox(buffer, new Rect(0, 0, 0, 5), BoxStyle.SquareFrame);
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

    // ── Border boxes (#553: seven private copies collapsed into one painter) ──

    /// <summary>
    /// Degenerate rects must not paint OUTSIDE themselves. Two of the seven
    /// collapsed copies had dropped the <c>Width &lt; 2</c> guard, so a
    /// squeezed pane painted corner glyphs into the neighbouring cell —
    /// silent, because the neighbouring cell is somebody else's paint.
    /// </summary>
    [Test]
    public async Task PaintBorderBox_DegenerateRect_PaintsNothingOutsideIt()
    {
        foreach (Rect rect in new[]
                 {
                     new Rect(0, 0, 1, 1),
                     new Rect(0, 0, 0, 5),
                     new Rect(3, 2, 1, 4),
                     new Rect(2, 3, 5, 1),
                 })
        {
            foreach (var style in new[] { BoxStyle.RoundedPanel, BoxStyle.SquareFrame })
            {
                var buffer = SentinelBuffer();
                PanelChrome.PaintBorderBox(buffer, rect, style);
                await AssertOutside(rect, buffer);
            }
        }
    }

    /// <summary>
    /// A real rect still gets a full frame — the guard is not a blanket skip.
    /// The buffer is one cell larger than the box on every side, so the exact
    /// art also proves the frame stayed inside it.
    /// </summary>
    [Test]
    public async Task PaintBorderBox_Rect_FramesAllFourEdges()
    {
        var buffer = SentinelBuffer(8, 6);
        PanelChrome.PaintBorderBox(buffer, new Rect(1, 1, 6, 4));

        string[] rows = GridDump.Art(buffer).Split('\n');
        await Assert.That(rows[0]).IsEqualTo("########");
        await Assert.That(rows[1]).IsEqualTo("#╭────╮#");
        await Assert.That(rows[2]).IsEqualTo("#│    │#");
        await Assert.That(rows[3]).IsEqualTo("#│    │#");
        await Assert.That(rows[4]).IsEqualTo("#╰────╯#");
        await Assert.That(rows[5]).IsEqualTo("########");
    }

    /// <summary>
    /// Rectilinear corners are a parameter of the one painter, not a seventh
    /// copy — and the square dialect is also the one that does NOT blank the
    /// interior, because the image viewer is fullscreen and opaque and wipes its
    /// own surface first (a second fill would only repaint blanks with blanks).
    /// The sentinel therefore SURVIVES inside the frame here, where the rounded
    /// sibling above shows blanks: that difference between the two dialects is
    /// exactly what the art below pins.
    /// </summary>
    [Test]
    public async Task PaintBorderBox_SquareStyle_UsesRectilinearCorners()
    {
        var buffer = SentinelBuffer(8, 6);
        PanelChrome.PaintBorderBox(buffer, new Rect(1, 1, 6, 4), BoxStyle.SquareFrame);

        string[] rows = GridDump.Art(buffer).Split('\n');
        await Assert.That(rows[0]).IsEqualTo("########");
        await Assert.That(rows[1]).IsEqualTo("#┌────┐#");
        await Assert.That(rows[2]).IsEqualTo("#│####│#");
        await Assert.That(rows[3]).IsEqualTo("#│####│#");
        await Assert.That(rows[4]).IsEqualTo("#└────┘#");
        await Assert.That(rows[5]).IsEqualTo("########");
    }

    /// <summary>
    /// The square viewer frame leaves the interior to its caller (the overlay
    /// blanks its own surface first) — so the shared painter must not fill.
    /// </summary>
    [Test]
    public async Task PaintBorderBox_SquareStyle_LeavesTheInteriorAlone()
    {
        var buffer = SentinelBuffer();
        PanelChrome.PaintBorderBox(buffer, new Rect(1, 1, 6, 4), BoxStyle.SquareFrame);

        await Assert.That(buffer.Get(3, 2).Rune).IsEqualTo((int)Sentinel);
        await Assert.That(buffer.Get(2, 1).Rune).IsEqualTo((int)'─');
        await Assert.That(buffer.Get(1, 2).Rune).IsEqualTo((int)'│');
        await Assert.That(buffer.Get(1, 1).Style.Fg).IsEqualTo(ChatPalette.Border);
    }

    // ── Single-owner guard: no second box painter may be born here ──────────

    /// <summary>
    /// The census that started #553, kept as a test. Panel corners are spelled
    /// in exactly one file in the CellForge chat layer; a new overlay that
    /// wants a frame calls <c>PanelChrome.PaintBorderBox</c> instead of
    /// copying the nearest private painter — which is how two of the seven
    /// lost the degenerate-rect guard in the first place.
    ///
    /// The scan covers BOTH dialects. The first version of this guard only
    /// looked for the rounded <c>╭╮╰╯</c>, which meant a new overlay copying
    /// the image viewer's rectilinear <c>┌┐└┘</c> — the other half of
    /// <see cref="BoxStyle"/> — walked straight past it. TableBlock is the one
    /// legitimate <c>┌</c> in this layer and is a table GRID, not a panel
    /// frame: it also draws <c>┼├┤</c>, which no outer border can contain.
    /// </summary>
    [Test]
    public async Task BoxCorners_AreSpelledInExactlyOneFile()
    {
        string widgets = Path.Combine(RepoRoot(), "src", "Harbor.Tui.CellForge");
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(widgets, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(widgets, file);
            if (relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue; // generated build output, not source
            }

            string text = File.ReadAllText(file);
            bool rounded = text.Contains('╭') || text.Contains('╮') || text.Contains('╰') || text.Contains('╯');
            bool rectilinear = text.Contains('┌') || text.Contains('┐') || text.Contains('└') || text.Contains('┘');
            if (!rounded && !rectilinear)
            {
                continue;
            }

            // A grid renderer draws junctions inside the frame; a copied panel
            // box cannot. See the summary on the exception above.
            if (rectilinear && (text.Contains('┼') || text.Contains('├') || text.Contains('┤')))
            {
                continue;
            }

            if (relative != Path.Combine("Chat", "Widgets", "PanelChrome.cs"))
            {
                offenders.Add(relative);
            }
        }

        await Assert.That(string.Join(" ", offenders)).IsEqualTo(string.Empty);
    }

    /// <summary>Every overlay frame goes through the shared painter (7 call sites).</summary>
    [Test]
    public async Task EveryOverlayBox_GoesThroughPanelChrome()
    {
        string[] overlays =
        [
            "CommandPaletteView.cs",
            "DialogOverlay.cs",
            "DiffViewerOverlay.cs",
            "FilePickerView.cs",
            "ImageViewerOverlay.cs",
            "SetupChecklistOverlay.cs",
            "WhichKeyHelpOverlay.cs",
        ];

        var missing = new List<string>();
        foreach (string file in overlays)
        {
            string text = File.ReadAllText(
                Path.Combine(RepoRoot(), "src", "Harbor.Tui.CellForge", "Chat", "Widgets", file));
            if (!text.Contains("PanelChrome.PaintBorderBox"))
            {
                missing.Add(file);
            }
        }

        await Assert.That(string.Join(" ", missing)).IsEqualTo(string.Empty);
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

    // ── Box-paint helpers (#553) ─────────────────────────────────────────────

    /// <summary>Marker cell pre-painted everywhere: whatever still reads
    /// <see cref="Sentinel"/> was never written by the painter under test.</summary>
    private const char Sentinel = '#';

    /// <summary>Big enough for every degenerate rect in the sweep below.</summary>
    private static ScreenBuffer SentinelBuffer() => SentinelBuffer(12, 8);

    private static ScreenBuffer SentinelBuffer(int cols, int rows)
    {
        var buffer = new ScreenBuffer(cols, rows);
        var cell = Cell.From(new Rune(Sentinel), new CellStyle(ChatPalette.Text));
        buffer.FillAll(in cell);
        return buffer;
    }

    /// <summary>
    /// Fails listing every cell the painter touched outside
    /// <paramref name="rect"/> — the exact failure #553 was filed for.
    /// </summary>
    private static async Task AssertOutside(Rect rect, ScreenBuffer buffer)
    {
        var strays = new List<string>();
        for (int y = 0; y < buffer.Rows; y++)
        {
            for (int x = 0; x < buffer.Cols; x++)
            {
                bool inside = x >= rect.X && x < rect.Right && y >= rect.Y && y < rect.Bottom;
                if (inside)
                {
                    continue;
                }

                int rune = buffer.Get(x, y).Rune;
                if (rune != Sentinel)
                {
                    strays.Add("(" + x + "," + y + ")=" + (char)rune);
                }
            }
        }

        await Assert.That(string.Join(" ", strays)).IsEqualTo(string.Empty);
    }

    /// <summary>Walks up from the test binaries to the repo root (<c>Harbor.slnx</c>).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbor.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
               ?? throw new InvalidOperationException("repo root (Harbor.slnx) not found from " + AppContext.BaseDirectory);
    }
}
