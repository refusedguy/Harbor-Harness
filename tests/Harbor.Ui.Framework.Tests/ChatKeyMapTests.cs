using System.Collections.Immutable;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Central hotkey dispatch contract: <see cref="ChatKeyMap" /> is the only
///     key→action table in the product. Shells (SpectreTui, RazorConsole,
///     Termina, TerminalGui) forward raw <see cref="UiKey" /> values and must not
///     duplicate these mappings — if a mapping moves back into a shell and out
///     of this table, these tests fail without launching any shell.
/// </summary>
public class ChatKeyMapTests
{
    private static readonly ChatKeyMap Map = new();

    [Test]
    public async Task Escape_ResolvesToQuit_FirstEntryWinsOverAbort()
    {
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Escape))).IsEqualTo(ChatAction.Quit);
    }

    [Test]
    public async Task Enter_ResolvesToSubmit()
    {
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Enter))).IsEqualTo(ChatAction.Submit);
    }

    [Test]
    public async Task NavigationKeys_ResolveToScrollActions()
    {
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Up))).IsEqualTo(ChatAction.ScrollUpLine);
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Down))).IsEqualTo(ChatAction.ScrollDownLine);
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.PageUp))).IsEqualTo(ChatAction.ScrollUpPage);
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.PageDown))).IsEqualTo(ChatAction.ScrollDownPage);
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Home))).IsEqualTo(ChatAction.ScrollTop);
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.End))).IsEqualTo(ChatAction.ScrollBottom);
    }

    [Test]
    public async Task AltArrows_ResolveToInputHistory()
    {
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Up, KeyModifierSet.Alt)))
            .IsEqualTo(ChatAction.InputHistoryPrev);
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Down, KeyModifierSet.Alt)))
            .IsEqualTo(ChatAction.InputHistoryNext);
    }

    [Test]
    public async Task EditKeys_ResolveCorrectly()
    {
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Tab))).IsEqualTo(ChatAction.Autocomplete);
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Tab, KeyModifierSet.Ctrl)))
            .IsEqualTo(ChatAction.CyclePanelFocus);
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Backspace))).IsEqualTo(ChatAction.Backspace);
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.F2))).IsEqualTo(ChatAction.ToggleFocus);
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.F12))).IsEqualTo(ChatAction.ToggleLogsPanel);
    }

    [Test]
    public async Task CtrlL_ResolvesToClear_InCoreTable()
    {
        await Assert.That(Map.Resolve(UiKey.ForChar('l', KeyModifierSet.Ctrl))).IsEqualTo(ChatAction.Clear);
    }

    [Test]
    public async Task CtrlC_ResolvesToAbort_InCoreTable()
    {
        await Assert.That(Map.Resolve(UiKey.ForChar('c', KeyModifierSet.Ctrl))).IsEqualTo(ChatAction.Abort);
    }

    [Test]
    public async Task QuestionMark_ResolvesToHelpPanel_InCoreTable()
    {
        await Assert.That(Map.Resolve(UiKey.ForChar('?'))).IsEqualTo(ChatAction.HelpPanel);
    }

    [Test]
    public async Task CtrlJ_ResolvesToJumpPalette_InCoreTable()
    {
        await Assert.That(Map.Resolve(UiKey.ForChar('j', KeyModifierSet.Ctrl)))
            .IsEqualTo(ChatAction.JumpPalette);
    }

    [Test]
    public async Task LineFeed_ResolvesToJumpPalette_TerminalAlias()
    {
        // Some terminals report Ctrl+J as a bare LF (0x0A) with no Ctrl flag.
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Char, KeyModifierSet.None, '\n')))
            .IsEqualTo(ChatAction.JumpPalette);
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.Char, KeyModifierSet.Ctrl, '\n')))
            .IsEqualTo(ChatAction.JumpPalette);
    }

    [Test]
    public async Task PrintableChar_FallsThroughToChar()
    {
        await Assert.That(Map.Resolve(UiKey.ForChar('x'))).IsEqualTo(ChatAction.Char);
    }

    [Test]
    public async Task UnknownKey_ResolvesToNone()
    {
        await Assert.That(Map.Resolve(UiKey.Unknown)).IsEqualTo(ChatAction.None);
        await Assert.That(Map.Resolve(new UiKey(UiKeyCode.None))).IsEqualTo(ChatAction.None);
    }

    [Test]
    public async Task ShellHotkeys_HaveBindingsInCoreTable()
    {
        // Regression guard: Clear / Abort / HelpPanel / JumpPalette used to be
        // (partly) resolved by per-shell if/else branches. They must stay
        // resolvable from raw UiKey values through this table alone.
        foreach (ChatAction action in new[]
                 {
                     ChatAction.Clear, ChatAction.Abort, ChatAction.HelpPanel, ChatAction.JumpPalette
                 })
        {
            var entry = Map.Get(action);
            await Assert.That(entry.Bindings.Length).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task JumpPalette_TogglesRegisteredJumpPanel()
    {
        var state = new UiState
        {
            RegisteredPanelIds = ["jump"],
            PanelStates = ImmutableDictionary<string, TuiPanelState>.Empty.SetItem("jump", TuiPanelState.Hidden)
        };
        var action = Map.Resolve(UiKey.ForChar('j', KeyModifierSet.Ctrl));
        var result = UiReducer.Update(state, new UiMsg.KeyInput(action, UiKey.ForChar('j', KeyModifierSet.Ctrl)));
        await Assert.That(result.State.PanelStates["jump"]).IsEqualTo(TuiPanelState.Visible);
    }

    [Test]
    public async Task JumpPalette_WithoutRegisteredPanel_IsNoop()
    {
        var state = new UiState();
        var action = Map.Resolve(new UiKey(UiKeyCode.Char, KeyModifierSet.None, '\n'));
        var result = UiReducer.Update(state, new UiMsg.KeyInput(action, new UiKey(UiKeyCode.Char, KeyModifierSet.None, '\n')));
        await Assert.That(result.State.PanelStates.ContainsKey("jump")).IsFalse();
    }
}
