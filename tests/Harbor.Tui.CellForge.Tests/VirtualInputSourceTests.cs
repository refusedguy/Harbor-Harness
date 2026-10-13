using System.Text;
using Harbor.Tui.CellForge.Input;

namespace Harbor.Tui.CellForge.Tests;

// TR9 (#1228, epic #1195): VirtualInputSource unit tests — the scripted
// queue over the engine vocabulary. Engine-typed sites qualify through the
// EngineInput alias (see GlobalUsings); shared-typed names stay shared.
public class VirtualInputSourceTests
{
    [Test]
    public async Task NewSource_IsEmpty()
    {
        var source = new VirtualInputSource();
        await Assert.That(source.IsEmpty).IsTrue();
        await Assert.That(source.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SendKey_Enqueues_One_Key_Event()
    {
        var source = new VirtualInputSource();
        source.SendKey(EngineInput.KeyEvent.Simple(EngineInput.KeyCode.Enter));
        await Assert.That(source.Count).IsEqualTo(1);
        await Assert.That(source.TryTake(out var evt)).IsTrue();
        await Assert.That(evt.Kind).IsEqualTo(EngineInput.InputEventKind.Key);
        await Assert.That(evt.Key.Key).IsEqualTo(EngineInput.KeyCode.Enter);
        await Assert.That(source.IsEmpty).IsTrue();
    }

    [Test]
    public async Task SendText_Splits_Into_Char_Presses()
    {
        var source = new VirtualInputSource();
        source.SendText("ab");
        await Assert.That(source.Count).IsEqualTo(2);
        await Assert.That(source.TryTake(out var first)).IsTrue();
        await Assert.That(first.Key.Character).IsEqualTo(new Rune('a'));
        await Assert.That(source.TryTake(out var second)).IsTrue();
        await Assert.That(second.Key.Character).IsEqualTo(new Rune('b'));
    }

    [Test]
    public async Task SendText_Crlf_Is_One_Enter()
    {
        var source = new VirtualInputSource();
        source.SendText("a\r\nb\rc\nd");
        // a, Enter, b, Enter, c, Enter, d — never two submits for one CRLF.
        await Assert.That(source.Count).IsEqualTo(7);
        var kinds = new List<EngineInput.KeyCode>();
        while (source.TryTake(out var evt))
        {
            kinds.Add(evt.Key.Key);
        }

        await Assert.That(kinds.Count(k => k == EngineInput.KeyCode.Enter)).IsEqualTo(3);
    }

    [Test]
    public async Task SendText_Null_Is_NoOp()
    {
        var source = new VirtualInputSource();
        source.SendText(null);
        await Assert.That(source.IsEmpty).IsTrue();
    }

    [Test]
    public async Task SendResize_Enqueues_Resize_With_Extent()
    {
        var source = new VirtualInputSource();
        source.SendResize(100, 30);
        await Assert.That(source.TryTake(out var evt)).IsTrue();
        await Assert.That(evt.Kind).IsEqualTo(EngineInput.InputEventKind.Resize);
        await Assert.That(evt.Resize.Width).IsEqualTo(100);
        await Assert.That(evt.Resize.Height).IsEqualTo(30);
    }

    [Test]
    public async Task SendPaste_Enqueues_Atomic_Payload()
    {
        var source = new VirtualInputSource();
        source.SendPaste("hello\nworld");
        await Assert.That(source.TryTake(out var evt)).IsTrue();
        await Assert.That(evt.Kind).IsEqualTo(EngineInput.InputEventKind.Paste);
        await Assert.That(evt.Paste.Text).IsEqualTo("hello\nworld");
    }

    [Test]
    public async Task TryTake_Empty_Returns_False()
    {
        var source = new VirtualInputSource();
        await Assert.That(source.TryTake(out _)).IsFalse();
    }

    [Test]
    public async Task DrainTo_Delivers_In_Order_Then_Empties()
    {
        var source = new VirtualInputSource();
        source.SendSimple(EngineInput.KeyCode.Up);
        source.SendSimple(EngineInput.KeyCode.Down);
        var seen = new List<EngineInput.KeyCode>();
        source.DrainTo(evt => seen.Add(evt.Key.Key));
        await Assert.That(seen).IsEquivalentTo(
            new[] { EngineInput.KeyCode.Up, EngineInput.KeyCode.Down });
        await Assert.That(source.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Clear_Drops_Queued_Events()
    {
        var source = new VirtualInputSource();
        source.SendText("abc");
        source.Clear();
        await Assert.That(source.IsEmpty).IsTrue();
    }
}
