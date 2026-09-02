using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

public class AnsiWriterTests
{
    private static (AnsiWriter Writer, RecordingBackend Backend) Make(bool sync = false)
    {
        var backend = new RecordingBackend();
        return (new AnsiWriter(backend, sync), backend);
    }

    [Test]
    public async Task Frame_SyncEnabled_WrapsWith2026()
    {
        var (w, backend) = Make(sync: true);
        w.BeginFrame();
        w.WriteText("hi");
        await w.EndFrameAsync();

        var text = backend.Text;
        await Assert.That(text.StartsWith("\x1B[?2026h")).IsTrue();
        await Assert.That(text.EndsWith("\x1B[?2026l")).IsTrue();
        await Assert.That(text.Contains("hi")).IsTrue();
    }

    [Test]
    public async Task Frame_SyncDisabled_HasNoWrapper()
    {
        var (w, backend) = Make();
        w.BeginFrame();
        w.WriteText("hi");
        await w.EndFrameAsync();

        await Assert.That(backend.Text).IsEqualTo("hi");
    }

    [Test]
    public async Task MoveTo_EmitsAbsoluteCup_OneBased()
    {
        var (w, backend) = Make();
        w.BeginFrame();
        w.MoveTo(0, 0);
        await w.EndFrameAsync();

        await Assert.That(backend.Text).IsEqualTo("\x1B[1;1H");
    }

    [Test]
    public async Task MoveTo_ElidesRepeatedPosition()
    {
        var (w, backend) = Make();
        w.BeginFrame();
        w.MoveTo(5, 3);
        w.PutRune((Rune)'A'); // advances pen to x=6
        w.MoveTo(6, 3); // elided — same as tracked pen
        w.PutRune((Rune)'B');
        await w.EndFrameAsync();

        await Assert.That(backend.Text).IsEqualTo("\x1B[4;6HAB");
    }

    [Test]
    public async Task PutRune_WideAdvancesPenByTwo()
    {
        var (w, _) = Make();
        w.BeginFrame();
        w.MoveTo(0, 0);
        w.PutRune(new Rune(0x4E2D)); // 中 wide
        await Assert.That(w.TrackedX).IsEqualTo(2);
    }

    [Test]
    public async Task Style_FirstEmit_IsFullApplication()
    {
        var style = new CellStyle(PackedColor.Indexed(196), PackedColor.Rgb(10, 20, 30), StyleAttr.Bold | StyleAttr.Italic);
        var (w, backend) = Make();
        w.BeginFrame();
        w.SetStyle(in style);
        w.WriteText("x");
        await w.EndFrameAsync();

        await Assert.That(backend.Text).IsEqualTo("\x1B[0m\x1B[1;3m\x1b[38;5;196m\x1b[48;2;10;20;30mx");
    }

    [Test]
    public async Task Style_UnchangedBetweenCells_EmitsNothingExtra()
    {
        var style = new CellStyle(PackedColor.Indexed(21));
        var (w, backend) = Make();
        w.BeginFrame();
        w.SetStyle(in style);
        w.WriteText("a");
        w.SetStyle(in style); // identical — no bytes
        w.WriteText("b");
        await w.EndFrameAsync();

        await Assert.That(backend.Text).IsEqualTo("\x1B[0m\x1b[38;5;21mab");
    }

    [Test]
    public async Task Style_PlainTarget_CollapsesToReset()
    {
        var styled = new CellStyle(PackedColor.Indexed(9), attrs: StyleAttr.Underline);
        var (w, backend) = Make();
        w.BeginFrame();
        w.SetStyle(in styled);
        w.WriteText("a");
        w.SetStyle(CellStyle.Plain);
        w.WriteText("b");
        await w.EndFrameAsync();

        await Assert.That(backend.Text).IsEqualTo("\x1B[0m\x1B[4m\x1b[38;5;9ma\x1B[0mb");
    }

    [Test]
    public async Task Style_AttributeDelta_EmitsOnlyOffOnPair()
    {
        var bold = new CellStyle(attrs: StyleAttr.Bold);
        var underline = new CellStyle(attrs: StyleAttr.Underline);
        var (w, backend) = Make();
        w.BeginFrame();
        w.SetStyle(in bold);
        w.SetStyle(in underline); // off 22, on 4 — colors untouched
        await w.EndFrameAsync();

        await Assert.That(backend.Text).IsEqualTo("\x1B[0m\x1B[1m\x1B[22;4m");
    }

    [Test]
    public async Task Style_BoldOffKeepsDim_ReappliesDimAfterShared22()
    {
        var both = new CellStyle(attrs: StyleAttr.Bold | StyleAttr.Dim);
        var dim = new CellStyle(attrs: StyleAttr.Dim);
        var (w, backend) = Make();
        w.BeginFrame();
        w.SetStyle(in both);
        w.SetStyle(in dim);
        await w.EndFrameAsync();

        await Assert.That(backend.Text).IsEqualTo("\x1B[0m\x1B[1;2m\x1B[22;2m");
    }

    [Test]
    public async Task Style_AllGroupsChange_CollapsesToResetPlusReapply()
    {
        var first = new CellStyle(PackedColor.Indexed(1), PackedColor.Indexed(2), StyleAttr.Bold);
        var second = new CellStyle(PackedColor.Indexed(3), PackedColor.Indexed(4), StyleAttr.Reverse);
        var (w, backend) = Make();
        w.BeginFrame();
        w.SetStyle(in first);
        w.SetStyle(in second); // fg+bg+attrs all differ → SGR 0 path
        await w.EndFrameAsync();

        await Assert.That(backend.Text)
            .IsEqualTo("\x1B[0m\x1B[1m\x1b[38;5;1m\x1b[48;5;2m\x1B[0m\x1B[7m\x1b[38;5;3m\x1b[48;5;4m");
    }

    [Test]
    public async Task EraseFromCursorDown_PreparesDefaultBackgroundFirst()
    {
        var styled = new CellStyle(PackedColor.Indexed(2));
        var (w, backend) = Make();
        w.BeginFrame();
        w.SetStyle(in styled);
        w.EraseFromCursorDown();
        await w.EndFrameAsync();

        await Assert.That(backend.Text).IsEqualTo("\x1B[0m\x1b[38;5;2m\x1B[0m\x1B[0J");
    }

    [Test]
    public async Task EmptyFrame_WritesNothing()
    {
        var (w, backend) = Make(sync: true);
        w.BeginFrame();
        await w.EndFrameAsync();

        await Assert.That(backend.Writes.Count).IsEqualTo(0);
    }
}
