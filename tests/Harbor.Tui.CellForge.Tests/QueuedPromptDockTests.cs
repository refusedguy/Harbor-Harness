using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// O7 (#1482): headless, sleep-free coverage of the queue dock and its dialog.
/// The counter is painted by the real <see cref="ComposerPanel"/> into a real
/// <see cref="ScreenBuffer"/> (Pilot-style frame-text asserts); the dialog is
/// the stock <see cref="DialogOverlay"/> seated through the stock
/// <see cref="DialogOverlayLayer"/> on an <see cref="OverlayStack"/>. Nothing
/// here registers a plugin axis or a key plane, so the freeze (#555) holds.
/// </summary>
// Rendering reads global TerminalColorPalette, serialized vs theme tests.
[NotInParallel("pty")]
public class QueuedPromptDockTests
{
    private const int Cols = 64;
    private const int Rows = 20;

    private static string PaintFrame(ComposerController composer, QueuedPromptDock? dock = null)
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var session = new ScreenSession(writer, Cols, Rows);
        var screen = ChatScreen.Build(composer, new StatusViewModel(), includeSidebar: false, includeTabStrip: false);
        screen.Composer.ShowChrome = true;
        screen.Tree.Solve(session.CurrentCols, session.CurrentRows);

        session.BeginFrame();
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(session.PaintBuffer);
        }

        if (dock is not null && dock.IsOpen)
        {
            dock.Layer.Sync(session.FrameArea);
            var overlays = new OverlayStack();
            overlays.Push(dock.Layer);
            overlays.PaintOver(session.PaintBuffer);
        }

        return TestRender.Art(session.PaintBuffer);
    }

    private static QueuedPromptDock Opened(out ComposerController composer)
    {
        composer = new ComposerController();
        composer.EnqueueQueued("first");
        composer.EnqueueQueued("second");
        var dock = new QueuedPromptDock(composer);
        dock.Open();
        return dock;
    }

    [Test]
    public async Task DockFrame_ShowsCounter_WhenQueued_AndClearsAfterDrain()
    {
        var composer = new ComposerController();
        composer.EnqueueQueued("first");
        composer.EnqueueQueued("second");

        await Assert.That(PaintFrame(composer)).Contains("⏳ 2 queued");

        _ = composer.TryDequeueQueued(out _);
        await Assert.That(PaintFrame(composer)).Contains("⏳ 1 queued");

        _ = composer.TryDequeueQueued(out _);
        await Assert.That(PaintFrame(composer)).DoesNotContain("queued");
    }

    [Test]
    public async Task Dialog_ShowsQueueOldestFirst()
    {
        var dock = Opened(out var composer);

        await Assert.That(dock.IsOpen).IsTrue();
        await Assert.That(dock.Dialog.Options).IsEquivalentTo(new[] { "first", "second" });
        string art = PaintFrame(composer, dock);
        await Assert.That(art).Contains("Queued prompts");
        await Assert.That(art.IndexOf("first", StringComparison.Ordinal))
            .IsLessThan(art.IndexOf("second", StringComparison.Ordinal));
    }

    [Test]
    public async Task UndoNewest_RestoresTextToComposer_WithCaretAtEnd_AndDropsCounter()
    {
        var dock = Opened(out var composer);

        await Assert.That(dock.UndoNewest()).IsTrue();

        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("second");
        await Assert.That(composer.Buffer.Cursor).IsEqualTo("second".Length);
        await Assert.That(dock.CounterText).IsEqualTo("⏳ 1 queued");
        await Assert.That(dock.Dialog.Options).IsEquivalentTo(new[] { "first" });
        await Assert.That(PaintFrame(composer, dock)).Contains("⏳ 1 queued");
    }

    [Test]
    public async Task DeleteNewest_RemovesEntry_WithoutRestoringText()
    {
        var dock = Opened(out var composer);

        await Assert.That(dock.DeleteNewest()).IsTrue();

        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo(string.Empty);
        await Assert.That(dock.CounterText).IsEqualTo("⏳ 1 queued");
        await Assert.That(dock.Dialog.Options).IsEquivalentTo(new[] { "first" });
    }

    [Test]
    public async Task LastEntry_ClosesDialog_AfterChange()
    {
        var composer = new ComposerController();
        composer.EnqueueQueued("only");
        var dock = new QueuedPromptDock(composer);
        dock.Open();

        await Assert.That(dock.IsOpen).IsTrue();
        await Assert.That(dock.DeleteNewest()).IsTrue();
        await Assert.That(dock.IsOpen).IsFalse();
        await Assert.That(dock.CounterText).IsEqualTo(string.Empty);
    }
}
