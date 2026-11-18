// CommandPaletteKeyHintTests.cs — issue #2682, item Z06.
//
// The palette must show the shell's real binding next to a command row. The
// hint table is pure data so these tests pin the chip text without booting a
// headless Avalonia session or resolving the shell's DI graph.

using Harbor.App.Avalonia.Services;
using TUnit.Assertions;
using TUnit.Core;

namespace Harbor.App.Avalonia.Tests;

public class CommandPaletteKeyHintTests
{
    [Test]
    [Arguments("Palette", "Ctrl+P")]
    [Arguments("SidebarToggle", "Ctrl+B")]
    [Arguments("ThemeToggle", "Ctrl+Shift+T")]
    [Arguments("TerminalPane", "Ctrl+`")]
    [Arguments("FileOpen", "Ctrl+O")]
    [Arguments("FileSave", "Ctrl+S")]
    [Arguments("ChatClear", "Ctrl+L")]
    public async Task GlobalCommand_HasDisplayBinding(string commandId, string expected)
    {
        await Assert.That(KeyboardShortcutHints.Get(commandId)).IsEqualTo(expected);
    }

    [Test]
    public async Task CommandWithoutBinding_IsEmpty()
    {
        await Assert.That(KeyboardShortcutHints.Get("SessionNew")).IsEqualTo(string.Empty);
        await Assert.That(KeyboardShortcutHints.Get("NotACommand")).IsEqualTo(string.Empty);
    }
}
