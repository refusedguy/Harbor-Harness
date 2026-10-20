using Harbor.TestKit;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #1160 (steal/ratatui, epic #1155) — slim Backend trait + cursor-save
/// audit. Pins the escape bytes of <see cref="ITerminalControlBackend"/> as
/// implemented by <see cref="AnsiWriter"/>: cursor save/restore are emit-only
/// DECSC/DECRC (never a terminal query — ratatui #2691 race lesson) and the
/// scroll region travels through trait methods, never <c>Raw</c> inserts.
/// </summary>
public class TerminalControlBackendTests
{
    [Test]
    public async Task AnsiWriter_ImplementsSlimControlTrait()
    {
        await Assert.That(typeof(ITerminalControlBackend).IsAssignableFrom(typeof(AnsiWriter))).IsTrue();
    }

    [Test]
    public async Task SaveRestoreCursor_EmitsDecscDecrc()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        writer.SaveCursor();
        writer.RestoreCursor();
        writer.FlushSync();

        await Assert.That(backend.Escaped).IsEqualTo("\\e7\\e8");
    }

    [Test]
    public async Task SetCursorPosition_EmitsCup()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        writer.SetCursorPosition(4, 2);
        writer.FlushSync();

        await Assert.That(backend.Escaped).IsEqualTo("\\e[3;5H");
    }

    [Test]
    public async Task ScrollRegion_SetReset_EmitsDecstbm()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        writer.SetScrollRegion(0, 23);
        writer.ResetScrollRegion();
        writer.FlushSync();

        await Assert.That(backend.Escaped).IsEqualTo("\\e[1;24r\\e[r");
    }

    [Test]
    public async Task ScrollRegion_UpDown_EmitsSuSd()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        writer.ScrollRegionUp(3);
        writer.ScrollRegionDown(2);
        writer.FlushSync();

        await Assert.That(backend.Escaped).IsEqualTo("\\e[3S\\e[2T");
    }

    [Test]
    public async Task ClearScreen_EmitsResetPlusEd2()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        writer.ClearScreen();
        writer.FlushSync();

        // Same bg-erase discipline as the other erase paths: SGR 0 first so
        // background-color-erase terminals fill with the default bg.
        await Assert.That(backend.Escaped).IsEqualTo("\\e[0m\\e[2J");
    }
}
