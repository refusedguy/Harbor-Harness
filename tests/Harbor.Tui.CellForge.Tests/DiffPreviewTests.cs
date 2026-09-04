using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// CF-E-011: <see cref="DiffPreview"/> port of the Avalonia
/// <c>DiffPreviewHelper</c> — edit/write/patch extraction, file-path
/// fallback, preview/full budgets, sentinels — plus the
/// <c>ToolCallBlock.DiffRenderer</c> 6-line preview cap.
/// </summary>
public class DiffPreviewTests
{
    private static int LineCount(string text) => text.Split('\n').Length;

    private static string PaintCard(string diffText, out int measuredLines, int width = 40)
    {
        var block = new ToolCallBlock(new ToolCallInfo("t1", "edit", "big.cs"));
        block.Complete(new ToolResultBody("ok", isError: false, TimeSpan.FromMilliseconds(3), diffText));
        measuredLines = block.Measure(width).MinLines;
        var buffer = new ScreenBuffer(width, measuredLines);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, width, measuredLines), 0));
        return GridDump.Art(buffer);
    }

    [Test]
    public async Task Edit_Produces_ContextDiff_With_FilePath()
    {
        const string args = """
            {"path":"src/a.cs","oldString":"a\nb\nc","newString":"a\nB\nc"}
            """;

        var (isDiff, path, preview, full) = DiffPreview.ExtractDiff("edit", args, resultText: null);

        await Assert.That(isDiff).IsTrue();
        await Assert.That(path).IsEqualTo("src/a.cs");
        await Assert.That(preview!).Contains("- b");
        await Assert.That(preview!).Contains("+ B");
        await Assert.That(full!).Contains("- b");
        await Assert.That(full!).Contains("+ B");
        await Assert.That(LineCount(preview!) <= DiffPreview.MaxPreviewLines + 1).IsTrue();
    }

    [Test]
    public async Task Edit_Identical_Returns_NoLineDiff_Sentinel()
    {
        const string args = """
            {"path":"src/a.cs","oldString":"a\nb","newString":"a\nb"}
            """;

        var (isDiff, path, preview, full) = DiffPreview.ExtractDiff("edit", args, resultText: null);

        await Assert.That(isDiff).IsTrue();
        await Assert.That(path).IsEqualTo("src/a.cs");
        await Assert.That(preview).IsEqualTo(DiffPreview.NoLineDiffSentinel);
        await Assert.That(full).IsEqualTo(DiffPreview.NoLineDiffSentinel);
    }

    [Test]
    public async Task Write_Produces_PlusPrefixed_Content()
    {
        const string args = """
            {"file":"notes.txt","content":"x\ny"}
            """;

        var (isDiff, path, preview, full) = DiffPreview.ExtractDiff("write", args, resultText: null);

        await Assert.That(isDiff).IsTrue();
        await Assert.That(path).IsEqualTo("notes.txt");
        await Assert.That(preview!).Contains("+ x");
        await Assert.That(preview!).Contains("+ y");
        await Assert.That(full!).Contains("+ x");
    }

    [Test]
    public async Task Patch_Short_Passthrough_Verbateim()
    {
        const string patch = "@@ -1 +1 @@\n-old\n+new";
        const string args = """
            {"path":"f.cs","patch":"@@ -1 +1 @@\n-old\n+new"}
            """;

        var (isDiff, path, preview, full) = DiffPreview.ExtractDiff("patch", args, resultText: null);

        await Assert.That(isDiff).IsTrue();
        await Assert.That(path).IsEqualTo("f.cs");
        await Assert.That(preview).IsEqualTo(patch);
        await Assert.That(full).IsEqualTo(patch);
    }

    [Test]
    public async Task UnknownTool_Returns_NotDiff()
    {
        var (isDiff, path, preview, full) = DiffPreview.ExtractDiff("bash", """{"command":"ls"}""", resultText: null);

        await Assert.That(isDiff).IsFalse();
        await Assert.That(path is null).IsTrue();
        await Assert.That(preview is null).IsTrue();
        await Assert.That(full is null).IsTrue();
    }

    [Test]
    public async Task MissingPayload_Returns_NotDiff()
    {
        var edit = DiffPreview.ExtractDiff("edit", """{"path":"f.cs"}""", resultText: null);
        await Assert.That(edit.IsDiffTool).IsFalse();

        var write = DiffPreview.ExtractDiff("write", """{"path":"f.cs"}""", resultText: null);
        await Assert.That(write.IsDiffTool).IsFalse();

        var malformed = DiffPreview.ExtractDiff("edit", "{oops", resultText: null);
        await Assert.That(malformed.IsDiffTool).IsFalse();
    }

    [Test]
    public async Task UnknownPath_When_NoFileField()
    {
        var (isDiff, path, preview, _) = DiffPreview.ExtractDiff("write", """{"content":"hi"}""", resultText: null);

        await Assert.That(isDiff).IsTrue();
        await Assert.That(path).IsEqualTo(DiffPreview.UnknownPath);
        await Assert.That(preview!).Contains("+ hi");
    }

    [Test]
    public async Task ExtractFilePath_Prefers_First_FileOrPath_Field()
    {
        await Assert.That(DiffPreview.ExtractFilePath("""{"command":"ls","filePath":"a/b.cs"}"""))
            .IsEqualTo("a/b.cs");
        await Assert.That(DiffPreview.ExtractFilePath("""{"command":"ls"}"""))
            .IsEqualTo(DiffPreview.UnknownPath);
        await Assert.That(DiffPreview.ExtractFilePath("")).IsEqualTo(DiffPreview.UnknownPath);
        await Assert.That(DiffPreview.ExtractFilePath("{oops")).IsEqualTo(DiffPreview.UnknownPath);
    }

    [Test]
    public async Task Truncate_Edit_Preview_Capped_Full_Intact()
    {
        var oldLines = new string[20];
        var newLines = new string[20];
        for (int i = 0; i < 20; i++)
        {
            oldLines[i] = "old" + i;
            newLines[i] = "new" + i;
        }
        string args = "{\"path\":\"big.cs\",\"oldString\":\"" + string.Join("\\n", oldLines)
            + "\",\"newString\":\"" + string.Join("\\n", newLines) + "\"}";

        var (isDiff, _, preview, full) = DiffPreview.ExtractDiff("edit", args, resultText: null);

        await Assert.That(isDiff).IsTrue();
        await Assert.That(preview!.EndsWith(DiffPreview.DiffTruncatedSentinel, StringComparison.Ordinal)).IsTrue();
        await Assert.That(LineCount(preview)).IsEqualTo(DiffPreview.MaxPreviewLines + 1);
        await Assert.That(full!.Contains(DiffPreview.DiffTruncatedSentinel)).IsFalse();
        await Assert.That(full.Contains("- old0")).IsTrue();
        await Assert.That(full.Contains("+ new19")).IsTrue();
    }

    [Test]
    public async Task Truncate_Patch_Keeps_Head_Plus_Marker()
    {
        var patchLines = new string[10];
        for (int i = 0; i < 10; i++)
            patchLines[i] = " line" + i;
        string args = "{\"path\":\"f.cs\",\"patch\":\"" + string.Join("\\n", patchLines) + "\"}";

        var (isDiff, _, preview, full) = DiffPreview.ExtractDiff("patch", args, resultText: null);

        await Assert.That(isDiff).IsTrue();
        await Assert.That(LineCount(preview!)).IsEqualTo(DiffPreview.MaxPreviewLines + 1);
        await Assert.That(preview!.EndsWith(DiffPreview.DiffTruncatedSentinel, StringComparison.Ordinal)).IsTrue();
        await Assert.That(preview.Contains(" line0")).IsTrue();
        await Assert.That(full!.Contains(" line9")).IsTrue();
    }

    [Test]
    public async Task Truncate_Write_Uses_Content_Sentinel()
    {
        var contentLines = new string[10];
        for (int i = 0; i < 10; i++)
            contentLines[i] = "row" + i;
        string args = "{\"path\":\"f.txt\",\"content\":\"" + string.Join("\\n", contentLines) + "\"}";

        var (isDiff, _, preview, full) = DiffPreview.ExtractDiff("write", args, resultText: null);

        await Assert.That(isDiff).IsTrue();
        await Assert.That(preview!.EndsWith(DiffPreview.ContentTruncatedSentinel, StringComparison.Ordinal)).IsTrue();
        await Assert.That(LineCount(preview)).IsEqualTo(DiffPreview.MaxPreviewLines + 1);
        await Assert.That(full!.Contains(DiffPreview.ContentTruncatedSentinel)).IsFalse();
        await Assert.That(full.Contains("+ row9")).IsTrue();
    }

    [Test]
    public async Task ToolCallInfo_Carries_Preview_Fields()
    {
        const string args = """
            {"path":"src/a.cs","oldString":"a\nb","newString":"a\nB"}
            """;
        var (_, path, preview, full) = DiffPreview.ExtractDiff("edit", args, resultText: null);

        var info = new ToolCallInfo("t1", "edit", "src/a.cs", FilePath: path, DiffPreview: preview, DiffFull: full);

        await Assert.That(info.Id).IsEqualTo("t1");
        await Assert.That(info.FilePath).IsEqualTo("src/a.cs");
        await Assert.That(info.DiffPreview!).Contains("+ B");
        await Assert.That(info.DiffFull!).Contains("- b");

        // Legacy 3-arg construction stays source-compatible with null preview surface.
        var legacy = new ToolCallInfo("t2", "bash", "ls");
        await Assert.That(legacy.FilePath is null).IsTrue();
        await Assert.That(legacy.DiffPreview is null).IsTrue();
        await Assert.That(legacy.DiffFull is null).IsTrue();
    }

    [Test]
    public async Task DiffRenderer_Caps_Long_Diff_At_Six_Plus_Overflow()
    {
        var lines = new string[10];
        for (int i = 0; i < 10; i++)
            lines[i] = "- old" + i;
        string art = PaintCard(string.Join("\n", lines), out int measured);

        await Assert.That(measured).IsEqualTo(1 + DiffPreview.MaxPreviewLines + 1);
        await Assert.That(art).Contains("- old0");
        await Assert.That(art).Contains(DiffPreview.DiffTruncatedSentinel);
        await Assert.That(art.Contains("- old6")).IsFalse();
    }

    [Test]
    public async Task DiffRenderer_Short_Diff_Has_No_Overflow_Marker()
    {
        string art = PaintCard("- gone\n+ fresh\n  ctx", out int measured);

        await Assert.That(measured).IsEqualTo(1 + 3);
        await Assert.That(art).Contains("+ fresh");
        await Assert.That(art.Contains(DiffPreview.DiffTruncatedSentinel)).IsFalse();
    }
}
