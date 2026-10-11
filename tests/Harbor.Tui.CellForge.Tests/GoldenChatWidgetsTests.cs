using System.Globalization;
using System.Text;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Services;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// T3 (#1185, epic #1155) — pytest-textual-snapshot steal, per-widget half:
/// one golden frame snapshot per Chat/Widgets widget. The frame-serialize +
/// approve half already exists (<see cref="Golden"/> /
/// <see cref="GridDump"/> / <c>HARBOR_UPDATE_GOLDENS=1</c>); this suite adds
/// the per-widget coverage — each test paints one widget into a small
/// <c>ScreenBuffer</c> in-process (fast — NOT PtyTests) and pins the
/// art + exact-cell dump under <c>tests/fixtures/celldiff/t3-*.golden.txt</c>.
///
/// Approve flow (same as the CE-3 goldens): normal CI compares; to
/// (re)approve after an intentional paint change run
/// <c>gh workflow run goldens.yml --ref &lt;branch&gt;</c>, which regenerates
/// with <c>HARBOR_UPDATE_GOLDENS=1</c> and commits baselines back. Every test
/// also carries art spot-asserts on intent, so a regenerated golden that
/// drifted off-spec still fails (the golden pins bytes, the asserts pin
/// meaning).
/// </summary>
// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class GoldenChatWidgetsTests
{
    private static string Snap(string name, ScreenBuffer buffer)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"# golden: {name}\n");
        sb.Append(CultureInfo.InvariantCulture, $"## art ({buffer.Cols}x{buffer.Rows})\n");
        sb.Append(GridDump.Art(buffer));
        sb.Append("## cells\n");
        sb.Append(GridDump.Cells(buffer));
        return sb.ToString();
    }

    private static async Task<string> VerifyAsync(string name, ScreenBuffer buffer)
    {
        string doc = Snap(name, buffer);
        string expected = Golden.Verify(name, doc);
        await Assert.That(doc).IsEqualTo(expected);
        return GridDump.Art(buffer);
    }

    private static void PaintBlock(IChatBlock block, ScreenBuffer buffer)
    {
        int h = block.Measure(buffer.Cols).MinLines;
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, buffer.Cols, h), 0));
    }

    [Test]
    public async Task TableBlock_TwoColumns_Golden()
    {
        string[] lines = ["| name | age |", "| --- | ---: |", "| ada | 36 |", "| grace | 85 |"];
        bool ok = TableBlock.TryParse(lines, 0, out var block, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(block).IsNotNull();

        var buffer = new ScreenBuffer(32, block!.Measure(32).MinLines);
        PaintBlock(block, buffer);

        string art = await VerifyAsync("t3-table-block", buffer);
        await Assert.That(art.Contains("ada")).IsTrue();
        await Assert.That(art.Contains("grace")).IsTrue();
        await Assert.That(art.Contains('┌')).IsTrue();
    }

    [Test]
    public async Task ToolCallBlock_Running_And_Done_Golden()
    {
        var runningInfo = new ToolCallInfo("tc1", "read", "{\"path\":\"a.cs\"}");
        var doneInfo = new ToolCallInfo("tc2", "edit", "{\"path\":\"b.cs\"}");
        var running = new ToolCallBlock(runningInfo);
        var done = new ToolCallBlock(doneInfo);
        done.Complete(new ToolResultBody("patched 3 lines", isError: false, duration: TimeSpan.FromMilliseconds(120)));

        const int w = 48;
        int h1 = running.Measure(w).MinLines;
        int h2 = done.Measure(w).MinLines;
        var buffer = new ScreenBuffer(w, h1 + h2);
        running.Paint(new BlockPaintContext(buffer, new Rect(0, 0, w, h1), 0));
        done.Paint(new BlockPaintContext(buffer, new Rect(0, h1, w, h2), 0));

        string art = await VerifyAsync("t3-toolcall-block", buffer);
        await Assert.That(art.Contains("read")).IsTrue();
        await Assert.That(art.Contains("edit")).IsTrue();
        await Assert.That(art.Contains("patched 3 lines")).IsTrue();
    }

    [Test]
    public async Task ThinkingBlock_Text_Golden()
    {
        var block = new ThinkingBlock("Considering edge cases in the diff path.");
        var buffer = new ScreenBuffer(40, block.Measure(40).MinLines);
        PaintBlock(block, buffer);

        string art = await VerifyAsync("t3-thinking-block", buffer);
        await Assert.That(art.Contains("edge cases")).IsTrue();
    }

    [Test]
    public async Task UserBlock_Prompt_Golden()
    {
        var block = new UserBlock("please fix the parser");
        var buffer = new ScreenBuffer(40, block.Measure(40).MinLines);
        PaintBlock(block, buffer);

        string art = await VerifyAsync("t3-user-block", buffer);
        await Assert.That(art.Contains("YOU")).IsTrue();
        await Assert.That(art.Contains("please fix the parser")).IsTrue();
    }

    [Test]
    public async Task StreamingMarkdownBlock_Push_Golden()
    {
        var stream = new StreamingMarkdownBlock();
        stream.Push("Ship **fast**.\n- one\n");
        var buffer = new ScreenBuffer(40, stream.Measure(40).MinLines);
        PaintBlock(stream, buffer);

        string art = await VerifyAsync("t3-streaming-markdown", buffer);
        await Assert.That(art.Contains("Ship")).IsTrue();
        await Assert.That(art.Contains("one")).IsTrue();
    }

    [Test]
    public async Task GaugeBar_Half_Golden()
    {
        var buffer = new ScreenBuffer(24, 1);
        GaugeBar.Paint(buffer, new Rect(0, 0, 24, 1), new GaugeState(0.5));

        string art = await VerifyAsync("t3-gauge-bar", buffer);
        await Assert.That(GaugeBar.FilledCells(0.5, 24)).IsEqualTo(12);
        await Assert.That(art.Contains("50%")).IsTrue();
    }

    [Test]
    public async Task Scrollbar_Track_And_Thumb_Golden()
    {
        var buffer = new ScreenBuffer(4, 8);
        var viewport = new ScrollableViewport();
        viewport.Configure(100, 8);
        viewport.SetOffset(20);

        bool painted = Scrollbar.TryPaint(buffer, new Rect(0, 0, 4, 8), viewport);

        await Assert.That(painted).IsTrue();
        string art = await VerifyAsync("t3-scrollbar", buffer);
        var rows = art.Split('\n');
        await Assert.That(rows.Any(r => r.Length >= 4 && (r[3] == '█' || r[3] == '│'))).IsTrue();
        await Assert.That(rows.Any(r => r.Length >= 4 && r[3] == '█')).IsTrue();
    }

    [Test]
    public async Task CommandPaletteView_List_Golden()
    {
        var palette = new CommandPaletteView();
        palette.Show([
            new CommandItem("setup", "Setup", "First-run wizard"),
            new CommandItem("reload", "Reload config"),
            new CommandItem("quit", "Quit harbor"),
        ]);

        var buffer = new ScreenBuffer(32, 8);
        palette.Paint(buffer, new Rect(0, 0, 32, 8));

        string art = await VerifyAsync("t3-command-palette", buffer);
        await Assert.That(art.Contains("Setup")).IsTrue();
        await Assert.That(art.Contains("Reload config")).IsTrue();
    }

    [Test]
    public async Task FilePickerView_List_Golden()
    {
        var picker = new FilePickerView();
        picker.Show([
            new FilePickerItem("src/a.cs", "2 lines", PreviewLines: ["line one", "line two"]),
            new FilePickerItem("README.md", "docs"),
        ]);

        var buffer = new ScreenBuffer(32, 8);
        picker.Paint(buffer, new Rect(0, 0, 32, 8));

        string art = await VerifyAsync("t3-file-picker", buffer);
        await Assert.That(art.Contains("src/a.cs")).IsTrue();
        await Assert.That(art.Contains("README.md")).IsTrue();
    }

    [Test]
    public async Task ToastOverlay_Two_Golden()
    {
        var overlay = new ToastOverlay();
        overlay.Show("saved", ToastKind.Success);
        overlay.Show("disk full", ToastKind.Error);

        var buffer = new ScreenBuffer(40, 6);
        overlay.Paint(buffer, new Rect(0, 0, 40, 6)); // first pass drains the queue
        overlay.Paint(buffer, new Rect(0, 0, 40, 6)); // second pass paints

        string art = await VerifyAsync("t3-toast-overlay", buffer);
        await Assert.That(art.Contains("saved")).IsTrue();
        await Assert.That(art.Contains("disk full")).IsTrue();
    }

    [Test]
    public async Task SideBarView_Snapshot_Golden()
    {
        var state = new SideBarState(
            SessionTitle: "fix parser",
            Model: "kilocode/hy3",
            TokensIn: 1200,
            TokensOut: 300,
            CostUsd: 0.001);

        var buffer = new ScreenBuffer(30, 8);
        SideBarView.Paint(buffer, new Rect(0, 0, 30, 8), state);

        string art = await VerifyAsync("t3-sidebar", buffer);
        await Assert.That(art.Contains("hy3")).IsTrue();
    }

    [Test]
    public async Task PanelChrome_BorderBox_Golden()
    {
        var buffer = new ScreenBuffer(24, 6);
        PanelChrome.PaintBorderBox(buffer, new Rect(0, 0, 24, 6));

        string art = await VerifyAsync("t3-panel-chrome", buffer);
        var rows = art.Split('\n');
        await Assert.That(rows[0].Contains('╭')).IsTrue();
        await Assert.That(rows[0].Contains('╮')).IsTrue();
    }

    [Test]
    public async Task SpinnerStrip_Frame_Golden()
    {
        var buffer = new ScreenBuffer(16, 1);
        buffer.SetText(0, 0, SpinnerStrip.FrameString(1) + " working", CellStyle.Plain);

        string art = await VerifyAsync("t3-spinner-strip", buffer);
        await Assert.That(art.Contains("⠙")).IsTrue();
        await Assert.That(art.Contains("working")).IsTrue();
    }
}
