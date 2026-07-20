using Avalonia.Controls;
using Harbor.App.Avalonia.ViewModels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core.Enums;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     CommandPalette component E2E tests — every visible state.
/// </summary>
/// <remarks>
///     <para>
///         Tests cover: open (Ctrl+P equivalent), search filtering, arrow-down
///         navigation, command execution (Enter), Esc/closed, and the empty-
///         results state. The palette is opened by setting
///         <see cref="MainViewModel.IsCommandPaletteOpen"/> = true on the UI thread.
///     </para>
/// </remarks>
[NotInParallel]
public sealed class CommandPaletteTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync().ConfigureAwait(false);

    /// <summary>
    ///     Open (Ctrl+P equivalent): the palette is visible with a search
    ///     input pre-populated with all commands.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task CommandPalette_Open_ShowsSearchInputAndAllCommands()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsCommandPaletteOpen = true);
        await Task.Delay(300).ConfigureAwait(false);

        var hasPalette = await Driver.WaitForTextAsync("Command palette", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasPalette).IsTrue();

        // The Results collection should be populated with all commands.
        var resultCount = UI(() => Vm.CommandPalette.Results.Count);
        await Assert.That(resultCount).IsGreaterThan(5);

        var path = await CaptureAsync("cmdpalette-open").ConfigureAwait(false);

        UI(() => Vm.IsCommandPaletteOpen = false);

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Command palette modal overlay. The palette is a centered card with a '⌘ Command palette' header, " +
            "a text input below (placeholder 'Type a command, /slash, or search…'), and a scrollable list of " +
            "command rows below the input. Each row has: an icon (⚡ for commands, / for slash commands), " +
            "the command label (e.g. 'Switch to chat', 'Open settings', '/help'), and a hint on the right " +
            "(e.g. 'ChatView', 'SettingsDialog', 'Slash command'). The first row is highlighted (selected).",
            nameof(CommandPalette_Open_ShowsSearchInputAndAllCommands)).ConfigureAwait(false);
        await Assert.That(vlm.Output).IsNotNull();
    }

    /// <summary>
    ///     Type "session" in the search: only matching commands visible.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task CommandPalette_Search_FiltersResults()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsCommandPaletteOpen = true;
            Vm.CommandPalette.Query = "session";
        });
        await Task.Delay(300).ConfigureAwait(false);

        var hasNewSession = await Driver.WaitForTextAsync("New session", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasNewSession).IsTrue();

        var hasUnrelated = Driver.GetAllVisibleText().Contains("Switch to chat", StringComparison.Ordinal);
        // "Switch to chat" should NOT be in the filtered results.
        await Assert.That(hasUnrelated).IsFalse();

        var path = await CaptureAsync("cmdpalette-search-session").ConfigureAwait(false);

        UI(() =>
        {
            Vm.CommandPalette.Query = string.Empty;
            Vm.IsCommandPaletteOpen = false;
        });

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Command palette after the user typed 'session' into the search. The search input contains 'session'. " +
            "The results list below shows ONLY commands whose label or hint contains 'session' — e.g. " +
            "'New session', 'Branch active session', 'Refresh session list'. Other commands like 'Switch to chat' " +
            "or 'Open settings' are NOT visible (filtered out). The first match is highlighted.",
            nameof(CommandPalette_Search_FiltersResults)).ConfigureAwait(false);
        await Assert.That(vlm.Output).IsNotNull();
    }

    /// <summary>
    ///     Arrow-down: moves the selection highlight to the next row.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task CommandPalette_ArrowDown_MovesSelection()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsCommandPaletteOpen = true);
        await Task.Delay(300).ConfigureAwait(false);

        var idxBefore = UI(() => Vm.CommandPalette.SelectedIndex);
        await Assert.That(idxBefore).IsEqualTo(0);

        UI(() => Vm.CommandPalette.MoveDown());
        await Task.Delay(150).ConfigureAwait(false);

        var idxAfter = UI(() => Vm.CommandPalette.SelectedIndex);
        await Assert.That(idxAfter).IsEqualTo(1);

        var path = await CaptureAsync("cmdpalette-arrow-down").ConfigureAwait(false);

        UI(() => Vm.IsCommandPaletteOpen = false);

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Command palette with the SECOND row highlighted as the selected item (after pressing Arrow Down once). " +
            "The first row 'Switch to chat' is no longer highlighted; the second row 'Switch to code editor' " +
            "is now highlighted. Search input is empty (showing all commands).",
            nameof(CommandPalette_ArrowDown_MovesSelection)).ConfigureAwait(false);
        await Assert.That(vlm.Output).IsNotNull();
    }

    /// <summary>
    ///     Enter: executes the selected command. With 'Switch to code' selected,
    ///     the palette closes and the active view becomes 'code'.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task CommandPalette_Enter_ExecutesSelected()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsCommandPaletteOpen = true;
            // The 2nd command in _allCommands is "Switch to code editor".
            // SelectedIndex=1 highlights it.
            Vm.CommandPalette.SelectedIndex = 1;
        });
        await Task.Delay(200).ConfigureAwait(false);

        UI(() => Vm.CommandPalette.InvokeSelected());
        await Task.Delay(300).ConfigureAwait(false);

        var activeView = UI(() => Vm.ActiveView);
        await Assert.That(activeView).IsEqualTo("code");

        var path = await CaptureAsync("cmdpalette-executed-code-view").ConfigureAwait(false);

        // Reset to chat view for the next test.
        UI(() => Vm.SwitchViewCommand.Execute("chat"));

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "After executing the 'Switch to code editor' command from the palette, the main window now shows the " +
            "Code Editor view in the center pane. The tab strip at the top shows '📝 Code' tab as the active one " +
            "(rather than '💬 Chat'). The code editor's empty-state placeholder 'No file open — press Ctrl+O to open a file.' " +
            "is visible in the center. The command palette is closed.",
            nameof(CommandPalette_Enter_ExecutesSelected)).ConfigureAwait(false);
        await Assert.That(vlm.Output).IsNotNull();
    }

    /// <summary>
    ///     Esc / closed: after setting IsCommandPaletteOpen=false, the palette
    ///     is no longer visible.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task CommandPalette_Closed_NotVisible()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsCommandPaletteOpen = true);
        await Task.Delay(200).ConfigureAwait(false);

        // Now close it.
        UI(() => Vm.IsCommandPaletteOpen = false);
        await Task.Delay(200).ConfigureAwait(false);

        var stillOpen = UI(() => Vm.IsCommandPaletteOpen);
        await Assert.That(stillOpen).IsFalse();

        var path = await CaptureAsync("cmdpalette-closed").ConfigureAwait(false);

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Main window with the command palette CLOSED. The center shows the chat empty-state placeholder " +
            "'Start a conversation'. No palette overlay is visible — the search input and command list are gone. " +
            "Status bar at the bottom reads 'idle'.",
            nameof(CommandPalette_Closed_NotVisible)).ConfigureAwait(false);
        await Assert.That(vlm.Output).IsNotNull();
    }

    /// <summary>
    ///     Empty results: typing a query that matches no commands shows an
    ///     empty list (no rows).
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task CommandPalette_NoMatches_EmptyResultsList()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsCommandPaletteOpen = true;
            Vm.CommandPalette.Query = "zzzznomatch";
        });
        await Task.Delay(300).ConfigureAwait(false);

        var count = UI(() => Vm.CommandPalette.Results.Count);
        await Assert.That(count).IsEqualTo(0);

        var path = await CaptureAsync("cmdpalette-no-matches").ConfigureAwait(false);

        UI(() =>
        {
            Vm.CommandPalette.Query = string.Empty;
            Vm.IsCommandPaletteOpen = false;
        });

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Command palette after typing a query that matches nothing ('zzzznomatch'). The search input contains " +
            "'zzzznomatch'. The results list below the input is EMPTY — no command rows visible. " +
            "The header '⌘ Command palette' is still visible. No row is highlighted.",
            nameof(CommandPalette_NoMatches_EmptyResultsList)).ConfigureAwait(false);
        await Assert.That(vlm.Output).IsNotNull();
    }
}
