using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Pinned pair for #1112 (keymap split): <see cref="HelpKeymap" /> is the
///     display table, <see cref="ChatKeyMap" /> is the resolve table. Neither
///     derives from the other (the help rows are curated display strings, not
///     1:1 with the 20+ resolve entries), so this test pins them together:
///     every help row must resolve to a live binding, and every display-critical
///     action must have a help row. Add a binding → update the table here.
/// </summary>
public class HelpKeymapPinTests
{
    private static readonly ChatKeyMap Map = new();

    [Test]
    public async Task EveryHelpRow_ResolvesToALiveBinding()
    {
        // Display key → probe key → expected resolve. Stateless Resolve is the
        // pre-#389 contract every shell without a store uses; the Ctrl+Tab row
        // therefore pins CyclePanelFocus (NextTab needs a 2+ tab snapshot).
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Escape))).IsEqualTo(ChatAction.Quit); // "Esc"
        await Assert.That(Map.Resolve(UiKey.ForChar('c', KeyModifierSet.Ctrl))).IsEqualTo(ChatAction.Abort); // "Ctrl+C"
        await Assert.That(Map.Resolve(UiKey.ForChar('l', KeyModifierSet.Ctrl))).IsEqualTo(ChatAction.Clear); // "Ctrl+L"
        await Assert.That(Map.Resolve(UiKey.ForChar('?'))).IsEqualTo(ChatAction.HelpPanel); // "?"
        await Assert.That(Map.Resolve(UiKey.ForChar('k', KeyModifierSet.Ctrl | KeyModifierSet.Alt)))
            .IsEqualTo(ChatAction.WhichKey); // "Ctrl+Alt+K"
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.F2))).IsEqualTo(ChatAction.ToggleFocus); // "F2"
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.F12))).IsEqualTo(ChatAction.ToggleLogsPanel); // "F12"
        await Assert.That(Map.Resolve(UiKey.ForChar('1', KeyModifierSet.Alt)))
            .IsEqualTo(ChatAction.TogglePanelSlot); // "Alt+1..9"
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Tab, KeyModifierSet.Ctrl)))
            .IsEqualTo(ChatAction.CyclePanelFocus); // "Ctrl+Tab" (stateless)
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Up, KeyModifierSet.Ctrl)))
            .IsEqualTo(ChatAction.ResizePanelGrow); // "Ctrl+↑/↓" (grow half)
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Down, KeyModifierSet.Ctrl)))
            .IsEqualTo(ChatAction.ResizePanelShrink); // "Ctrl+↑/↓" (shrink half)
        // "q / Esc": the Esc half resolves to Quit (pinned above); the 'q' half
        // is panel-focus-local (ClosePanel via the reducer), not a global
        // ChatKeyMap binding — plain 'q' stays Char by design.
        await Assert.That(Map.Resolve(UiKey.ForChar('q'))).IsEqualTo(ChatAction.Char);
    }

    [Test]
    public async Task DisplayCriticalActions_AllHaveHelpRows()
    {
        // Reverse direction: these actions must stay visible in the help panel.
        // Matched by substring so display phrasing ("Ctrl+↑/↓", "q / Esc",
        // "Alt+1..9") can evolve without breaking the pin — but a deleted row
        // fails here until the table is updated deliberately.
        string[] rows = [.. HelpKeymap.Rows.Select(r => r.Key)];
        await Assert.That(Contains(rows, "Esc")).IsTrue(); // Quit (+ "q / Esc" row)
        await Assert.That(Contains(rows, "Ctrl+C")).IsTrue(); // Abort
        await Assert.That(Contains(rows, "Ctrl+L")).IsTrue(); // Clear
        await Assert.That(Contains(rows, "?")).IsTrue(); // HelpPanel
        await Assert.That(Contains(rows, "Ctrl+Alt+K")).IsTrue(); // WhichKey
        await Assert.That(Contains(rows, "F2")).IsTrue(); // ToggleFocus
        await Assert.That(Contains(rows, "F12")).IsTrue(); // ToggleLogsPanel
        await Assert.That(Contains(rows, "Alt+1")).IsTrue(); // TogglePanelSlot
        await Assert.That(Contains(rows, "Ctrl+Tab")).IsTrue(); // CyclePanelFocus
        await Assert.That(Contains(rows, "Ctrl+")).IsTrue(); // Resize pair ("Ctrl+↑/↓")
    }

    private static bool Contains(string[] rows, string needle)
    {
        foreach (string row in rows)
        {
            if (row.Contains(needle, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
