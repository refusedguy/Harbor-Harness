using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

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
