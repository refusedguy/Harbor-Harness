using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// #58: rendering reads the global TerminalColorPalette — keep off parallel with pty.
[NotInParallel("pty")]
public class TestInputQueueTests
{
    [Test]
    public async Task PushText_ReadKey_RoundTripsCharacters()
    {
        var queue = new TestInputQueue();
        queue.PushText("hi");
        await Assert.That(queue.Count).IsEqualTo(2);
        await Assert.That(queue.ReadKey().KeyChar).IsEqualTo('h');
        await Assert.That(queue.ReadKey().KeyChar).IsEqualTo('i');
        await Assert.That(queue.IsEmpty).IsTrue();
    }

    [Test]
    public async Task ReadKey_Empty_ThrowsInvalidOperationException()
    {
        var queue = new TestInputQueue();
        await Assert.That(() => queue.ReadKey()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task PushKey_SpecialKey_RoundTrips()
    {
        var queue = new TestInputQueue();
        queue.PushKey(ConsoleKey.DownArrow);
        await Assert.That(queue.ReadKey().Key).IsEqualTo(ConsoleKey.DownArrow);
    }

    [Test]
    public async Task TestScreen_Geometry_IsFixed80x24()
    {
        var buffer = TestScreen.NewBuffer();
        await Assert.That((buffer.Cols, buffer.Rows)).IsEqualTo((80, 24));
        await Assert.That((TestScreen.Viewport().Width, TestScreen.Viewport().Height)).IsEqualTo((80, 24));
    }

    [Test]
    public async Task Lines_Output_AssertTextOverCells()
    {
        var buffer = TestScreen.NewBuffer();
        buffer.SetText(0, 0, "hello", CellStyle.Plain);
        await Assert.That(TestScreen.Lines(buffer)[0]).IsEqualTo("hello");
        await Assert.That(TestScreen.Lines(buffer)[1]).IsEqualTo(string.Empty);
        await Assert.That(TestScreen.Output(buffer).StartsWith("hello\n")).IsTrue();
    }

    [Test]
    public async Task DrainTo_DialogPrompt_TypesInput()
    {
        var dialog = new DialogOverlay();
        dialog.ShowPrompt("T", "Name?");
        var queue = new TestInputQueue();
        queue.PushText("hi");
        queue.DrainTo(k => dialog.HandleKey(k));
        await Assert.That(dialog.Input).IsEqualTo("hi");

        var buffer = TestScreen.NewBuffer();
        dialog.Paint(buffer, TestScreen.Viewport());
        await Assert.That(TestScreen.Output(buffer).Contains("hi")).IsTrue();
    }
}
