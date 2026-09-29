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
        var buffer = new ScreenBuffer(cols, rows);
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
        var buffer = new ScreenBuffer(20, 10);
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
