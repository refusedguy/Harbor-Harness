using System.Collections.Immutable;
using Harbor.Abstractions.Models;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

public class SideBarViewTests
{
    private static (ScreenBuffer Buffer, Rect Rect) MakeBuffer(int cols = 60, int rows = 24)
    {
        var buffer = TestRender.Buffer(cols, rows);
        return (buffer, new Rect(cols - SideBarLayout.DefaultWidth, 0, SideBarLayout.DefaultWidth, rows - 1));
    }

    [Test]
    public async Task Paint_EmptyState_RendersSectionHeaders()
    {
        var (buffer, rect) = MakeBuffer();
        SideBarView.Paint(buffer, rect, SideBarState.Empty);

        string dump = Dump(buffer, rect);
        await Assert.That(dump).Contains("SESSION");
        await Assert.That(dump).Contains("MODEL");
        await Assert.That(dump).Contains("TOKENS");
        await Assert.That(dump).Contains("(no session)");
    }

    [Test]
    public async Task Paint_FullState_RendersAllSections()
    {
        var (buffer, rect) = MakeBuffer(cols: 80, rows: 30);
        var state = new SideBarState(
            SessionTitle: "Fix the parser",
            SessionId: "0123456789abcdef",
            Model: "kilocode/tencent/hy3:free",
            TokensIn: 12_345,
            TokensOut: 678,
            CostUsd: 0.0123,
            ModifiedFiles: ["src/A.cs", "src/B.cs"],
            LspErrors: 2,
            LspWarnings: 5,
            McpServers: [new McpServerStatus("git", McpServerState.Connected), new McpServerStatus("fs", McpServerState.Error)]);

        SideBarView.Paint(buffer, rect, state);
        string dump = Dump(buffer, rect);

        await Assert.That(dump).Contains("Fix the parser");
        await Assert.That(dump).Contains("01234567");
        await Assert.That(dump).Contains("12.3k");
        await Assert.That(dump).Contains("678");
        await Assert.That(dump).Contains("$0.0123");
        await Assert.That(dump).Contains("MODIFIED (2)");
        await Assert.That(dump).Contains("src/A.cs");
        await Assert.That(dump).Contains("2 errors");
        await Assert.That(dump).Contains("5 warnings");
        await Assert.That(dump).Contains("git");
        await Assert.That(dump).Contains("fs");
    }

    [Test]
    public async Task Paint_ExtraSlots_RenderTitlesAndLines()
    {
        var (buffer, rect) = MakeBuffer(cols: 80, rows: 30);
        var slots = new[]
        {
            new SideBarSlot("PLUGINS", _ => new[] { new SideBarLine("web-search", "enabled") }),
        };

        SideBarView.Paint(buffer, rect, SideBarState.Empty, slots);
        string dump = Dump(buffer, rect);

        await Assert.That(dump).Contains("PLUGINS");
        await Assert.That(dump).Contains("web-search");
        await Assert.That(dump).Contains("enabled");
    }

    [Test]
    public async Task Paint_TinyRect_NoThrow()
    {
        var buffer = TestRender.Buffer(20, 10);
        SideBarView.Paint(buffer, new Rect(19, 9, 42, 6), SideBarState.Empty);
        SideBarView.Paint(buffer, new Rect(0, 0, 5, 3), SideBarState.Empty);
    }

    [Test]
    public async Task ShouldShow_WideTerminal_True_Narrow_False()
    {
        await Assert.That(SideBarLayout.ShouldShow(160)).IsTrue();
        await Assert.That(SideBarLayout.ShouldShow(120)).IsTrue();
        await Assert.That(SideBarLayout.ShouldShow(119)).IsFalse();
        await Assert.That(SideBarLayout.ShouldShow(80)).IsFalse();
    }

    [Test]
    public async Task Area_DocksRight_AboveStatusRow()
    {
        var area = SideBarView.Area(160, 40);
        await Assert.That(area.Width).IsEqualTo(42);
        await Assert.That(area.Right).IsEqualTo(160);
        await Assert.That(area.Height).IsEqualTo(39);
    }

    [Test]
    public async Task FormatTokens_Scales()
    {
        await Assert.That(SideBarView.FormatTokens(999)).IsEqualTo("999");
        await Assert.That(SideBarView.FormatTokens(1_000)).IsEqualTo("1k");
        await Assert.That(SideBarView.FormatTokens(12_345)).IsEqualTo("12.3k");
        await Assert.That(SideBarView.FormatTokens(1_234_567)).IsEqualTo("1.2M");
    }

    [Test]
    public async Task Paint_ContextPercent_Uses_Accumulated_Totals()
    {
        // #75 convergence pin: (7400+700)/10000 = 81%, same canonical
        // definition as the status-bar VM and the status bar ratio.
        var (buffer, rect) = MakeBuffer(cols: 80, rows: 30);
        var state = new SideBarState(
            SessionTitle: "s",
            Model: "m",
            TokensIn: 7400,
            TokensOut: 700,
            ContextWindow: 10_000);
        SideBarView.Paint(buffer, rect, state);
        string dump = Dump(buffer, rect);
        await Assert.That(dump).Contains("ctx 81%");
    }

    // ── #674: the DIAGNOSTICS counts are read, not invented ────────────────

    private static DiagnosticIssue Lsp(DiagnosticIssueSeverity severity, string message) =>
        new(DiagnosticIssueSource.LanguageServer, severity, "csharp", "src/a.cs", 1, message);

    private static DiagnosticIssue Tool(DiagnosticIssueSeverity severity, string message) =>
        new(DiagnosticIssueSource.ToolOutput, severity, "node", null, 0, message);

    [Test]
    public async Task Project_CountsTheLanguagesServerRowsInTheSnapshot()
    {
        var state = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Diagnostics = ImmutableArray.Create(
                    Lsp(DiagnosticIssueSeverity.Error, "first"),
                    Lsp(DiagnosticIssueSeverity.Error, "second"),
                    Lsp(DiagnosticIssueSeverity.Warning, "third")),
            },
        };

        SideBarState projected = SideBarView.ProjectFromStore(state);

        await Assert.That(projected.LspErrors).IsEqualTo(2)
            .Because("#674 found these spelled as literal zeros, so the section could never light up "
                   + "while a second, parallel text channel stood in for the real feature.");
        await Assert.That(projected.LspWarnings).IsEqualTo(1);
    }

    [Test]
    public async Task Project_DoesNotCountToolOutputAsLanguageServerDiagnostics()
    {
        var state = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Diagnostics = ImmutableArray.Create(
                    Tool(DiagnosticIssueSeverity.Error, "npm ERR! exit 1"),
                    Tool(DiagnosticIssueSeverity.Warning, "npm WARN deprecated")),
            },
        };

        SideBarState projected = SideBarView.ProjectFromStore(state);

        await Assert.That(projected.LspErrors).IsEqualTo(0);
        await Assert.That(projected.LspWarnings).IsEqualTo(0)
            .Because("a section labelled after the language server must not report a build log as one. "
                   + "The two are separate rows in the snapshot precisely so they can be counted apart — "
                   + "summing them under one heading is how the two got fused in the first place.");
    }

    [Test]
    public async Task Project_AnEmptySnapshotCountsZero()
    {
        await Assert.That(SideBarView.ProjectFromStore(new UiState()).LspErrors).IsEqualTo(0)
            .Because("zero is the right answer here and the wrong answer was, too. #674 could not tell "
                   + "them apart, which is why the architecture guard forbids pinning the literal.");
    }

    [Test]
    public async Task ProjectionCache_ReProjectsWhenTheSnapshotChanges()
    {
        var cache = new SideBarProjectionCache();
        var before = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Diagnostics = ImmutableArray.Create(Lsp(DiagnosticIssueSeverity.Error, "one")),
            },
        };

        _ = SideBarView.Project(before, cache);
        int missesAfterFirst = cache.MissCount;

        // Same session, same model, same cost — the fingerprint is unchanged on
        // every input the cache had before #674.
        var after = new UiState
        {
            Chat = before.Chat with
            {
                Diagnostics = ImmutableArray.Create(
                    Lsp(DiagnosticIssueSeverity.Error, "one"),
                    Lsp(DiagnosticIssueSeverity.Error, "two")),
            },
        };

        SideBarState projected = SideBarView.Project(after, cache);

        await Assert.That(cache.MissCount).IsGreaterThan(missesAfterFirst)
            .Because("the counts are read from the snapshot, so a snapshot that changed has to "
                   + "invalidate. A cache that missed this would report a stale count forever — the "
                   + "same lie as the hardcoded zero, wearing a live-looking shape.");
        await Assert.That(projected.LspErrors).IsEqualTo(2);
    }

    [Test]
    public async Task ProjectionCache_StillServesTheSameStateWithoutRecomputing()
    {
        var cache = new SideBarProjectionCache();
        var state = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Diagnostics = ImmutableArray.Create(Lsp(DiagnosticIssueSeverity.Error, "one")),
            },
        };

        _ = SideBarView.Project(state, cache);
        int misses = cache.MissCount;
        _ = SideBarView.Project(state, cache);

        await Assert.That(cache.MissCount).IsEqualTo(misses)
            .Because("the cache exists for a per-frame path; adding a fingerprint input must not turn "
                   + "every frame into a miss.");
    }

    // ── O12 steal (#1181): named slot points + overlay scrollbar ────────

    [Test]
    public async Task Paint_ContentSlots_PaintInOrder()
    {
        var (buffer, rect) = MakeBuffer(cols: 80, rows: 30);
        var slots = new SideBarSlot[]
        {
            new SideBarSlot("SECOND", _ => new[] { new SideBarLine("b", "2") }, Order: 200),
            new SideBarSlot("FIRST", _ => new[] { new SideBarLine("a", "1") }, Order: 100),
        };

        SideBarView.Paint(buffer, rect, SideBarState.Empty, slots);
        string dump = Dump(buffer, rect);

        int first = dump.IndexOf("FIRST");
        int second = dump.IndexOf("SECOND");
        await Assert.That(first >= 0 && second >= 0 && first < second).IsTrue();
    }

    [Test]
    public async Task Paint_ContentSlots_RegistrationOrderBreaksTies()
    {
        var (buffer, rect) = MakeBuffer(cols: 80, rows: 30);
        var slots = new SideBarSlot[]
        {
            new SideBarSlot("AAA", _ => new[] { new SideBarLine("a", "1") }),
            new SideBarSlot("BBB", _ => new[] { new SideBarLine("b", "2") }),
        };

        SideBarView.Paint(buffer, rect, SideBarState.Empty, slots);
        string dump = Dump(buffer, rect);

        int aaa = dump.IndexOf("AAA");
        int bbb = dump.IndexOf("BBB");
        await Assert.That(aaa >= 0 && bbb >= 0 && aaa < bbb).IsTrue();
    }

    [Test]
    public async Task Paint_FooterSlot_SingleWinnerPinnedToBottom()
    {
        var (buffer, rect) = MakeBuffer(cols: 80, rows: 30);
        var slots = new SideBarSlot[]
        {
            new SideBarSlot("FOOT-A", _ => new[] { new SideBarLine("ver", "1.0") }, Point: SideBarSlotPoint.Footer, Order: 200),
            new SideBarSlot("FOOT-B", _ => new[] { new SideBarLine("ver", "2.0") }, Point: SideBarSlotPoint.Footer, Order: 100),
        };

        SideBarView.Paint(buffer, rect, SideBarState.Empty, slots);
        string dump = Dump(buffer, rect);

        await Assert.That(dump.Contains("FOOT-B")).IsTrue();
        await Assert.That(dump.Contains("FOOT-A")).IsFalse();
        int valueRow = LastLineContaining(dump, "2.0");
        await Assert.That(valueRow).IsGreaterThan(-1);
        // Pinned: below the footer value only the sidebar hairline (which
        // marks every row including the reserved last one) may leave a mark.
        string[] rows = dump.Split('\n');
        for (int i = valueRow + 1; i < rows.Length; i++)
        {
            string trimmed = rows[i].Trim();
            await Assert.That(trimmed.Length == 0 || trimmed == "│").IsTrue();
        }
    }

    [Test]
    public async Task Paint_NoSlots_NullAndEmptyPaintIdentical()
    {
        var (b1, r1) = MakeBuffer(cols: 80, rows: 30);
        var (b2, r2) = MakeBuffer(cols: 80, rows: 30);
        SideBarView.Paint(b1, r1, SideBarState.Empty, null);
        SideBarView.Paint(b2, r2, SideBarState.Empty, System.Array.Empty<SideBarSlot>());

        await Assert.That(Dump(b1, r1)).IsEqualTo(Dump(b2, r2));
    }

    [Test]
    public async Task Paint_ClippedContent_PaintsOverlayScrollbarOverMargin()
    {
        // Rect.Bottom is inclusive: 14 rows leave room for exactly two file
        // rows, so 30 files clip and the scrollbar must appear.
        var (buffer, rect) = MakeBuffer(cols: 80, rows: 14);
        var files = new System.Collections.Generic.List<string>();
        for (int i = 0; i < 30; i++)
        {
            files.Add("src/File" + i + ".cs");
        }

        var state = SideBarState.Empty with { ModifiedFiles = files };
        SideBarView.Paint(buffer, rect, state);

        bool scrollbarSeen = false;
        for (int y = rect.Y; y < System.Math.Min(rect.Bottom, buffer.Rows); y++)
        {
            char c = (char)buffer.Get(rect.Right - 1, y).Rune;
            if (c == '█' || c == '│')
            {
                scrollbarSeen = true;
                break;
            }
        }

        await Assert.That(scrollbarSeen).IsTrue();
        // Overlay covers the margin column only — content rows are unshifted.
        await Assert.That(Dump(buffer, rect).Contains("src/File0.cs")).IsTrue();
    }

    [Test]
    public async Task Paint_FittingContent_LeavesMarginColumnBlank()
    {
        var (buffer, rect) = MakeBuffer(cols: 80, rows: 30);
        SideBarView.Paint(buffer, rect, SideBarState.Empty, null);

        bool markSeen = false;
        for (int y = rect.Y; y < System.Math.Min(rect.Bottom, buffer.Rows); y++)
        {
            char c = (char)buffer.Get(rect.Right - 1, y).Rune;
            if (c == '█' || c == '│')
            {
                markSeen = true;
                break;
            }
        }

        await Assert.That(markSeen).IsFalse();
    }

    // ── #1550: the MCP state owns its row visual (State pattern) ─────────

    [Test]
    public async Task Paint_McpRows_StateCarriesItsGlyphAndColor()
    {
        // Pixel pin: (glyph, colour) per state is exactly the pair the old
        // inline switch painted — the table only moved where it lives.
        (McpServerState State, char Glyph, PackedColor Color)[] expected =
        [
            (McpServerState.Connected, '●', ChatPalette.Success),
            (McpServerState.Connecting, '◐', ChatPalette.Warning),
            (McpServerState.Error, '●', ChatPalette.Error),
        ];

        foreach (var (state, glyph, color) in expected)
        {
            var (buffer, rect) = MakeBuffer(cols: 80, rows: 30);
            SideBarView.Paint(buffer, rect, new SideBarState(
                McpServers: [new McpServerStatus("alpha", state)]));

            int row = RowOf(buffer, rect, "alpha");
            await Assert.That(row).IsGreaterThan(0);
            var glyphCell = buffer.Get(rect.X + 3, row);
            await Assert.That((char)glyphCell.Rune).IsEqualTo(glyph)
                .Because("the glyph belongs to the state; the painter must ask, not branch.");
            await Assert.That(glyphCell.Style.Fg == color).IsTrue();
        }
    }

    [Test]
    public async Task McpServerStateVisual_Of_IsTotalOverTheEnum()
    {
        await Assert.That(McpServerStateVisual.Of(McpServerState.Connected).Glyph).IsEqualTo('●');
        await Assert.That(McpServerStateVisual.Of(McpServerState.Connecting).Glyph).IsEqualTo('◐');
        await Assert.That(McpServerStateVisual.Of(McpServerState.Error).Glyph).IsEqualTo('●');

        await Assert.That(McpServerStateVisual.Of(McpServerState.Connected).Color == ChatPalette.Success).IsTrue();
        await Assert.That(McpServerStateVisual.Of(McpServerState.Connecting).Color == ChatPalette.Warning).IsTrue();
        await Assert.That(McpServerStateVisual.Of(McpServerState.Error).Color == ChatPalette.Error).IsTrue();
    }

    [Test]
    public async Task McpServerStateVisual_Of_UndefinedState_FailsFast()
    {
        // A corrupted byte must not silently paint as some other state.
        Assert.Throws<KeyNotFoundException>(() => _ = McpServerStateVisual.Of((McpServerState)9));
    }

    private static int RowOf(ScreenBuffer buffer, Rect rect, string needle)
    {
        // Dump line i ↔ buffer row rect.Y + i.
        string[] rows = Dump(buffer, rect).Split('\n');
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Contains(needle))
            {
                return rect.Y + i;
            }
        }

        return -1;
    }

    private static int LastLineContaining(string dump, string needle)
    {
        string[] rows = dump.Split('\n');
        for (int i = rows.Length - 1; i >= 0; i--)
        {
            if (rows[i].Contains(needle))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Dump(ScreenBuffer buffer, Rect rect)
    {
        var sb = new System.Text.StringBuilder();
        for (int y = rect.Y; y < Math.Min(rect.Bottom, buffer.Rows); y++)
        {
            for (int x = rect.X; x < Math.Min(rect.Right, buffer.Cols); x++)
            {
                sb.Append((char)buffer.Get(x, y).Rune);
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
}
