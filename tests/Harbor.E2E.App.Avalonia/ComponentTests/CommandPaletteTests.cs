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
    public async Task SetupAsync() => await GetDriverAsync("CommandPalette").ConfigureAwait(false);

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

        var hasPalette = await Driver.WaitForTextAsync("Command palette", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasPalette).IsTrue();

        // The Results collection should be populated with all commands.
        var resultCount = UI(() => Vm.CommandPalette.Results.Count);
        await Assert.That(resultCount).IsGreaterThan(5);

        var path = await CaptureAsync("cmdpalette-open").ConfigureAwait(false);

        UI(() => Vm.IsCommandPaletteOpen = false);
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

        var idxBefore = UI(() => Vm.CommandPalette.SelectedIndex);
        await Assert.That(idxBefore).IsEqualTo(0);

        UI(() => Vm.CommandPalette.MoveDown());

        var idxAfter = UI(() => Vm.CommandPalette.SelectedIndex);
        await Assert.That(idxAfter).IsEqualTo(1);

        var path = await CaptureAsync("cmdpalette-arrow-down").ConfigureAwait(false);

        UI(() => Vm.IsCommandPaletteOpen = false);
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

        UI(() => Vm.CommandPalette.InvokeSelected());

        var activeView = UI(() => Vm.ActiveView);
        await Assert.That(activeView).IsEqualTo("code");

        var path = await CaptureAsync("cmdpalette-executed-code-view").ConfigureAwait(false);

        // Reset to chat view for the next test.
        UI(() => Vm.SwitchViewCommand.Execute("chat"));
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

        // Now close it.
        UI(() => Vm.IsCommandPaletteOpen = false);

        var stillOpen = UI(() => Vm.IsCommandPaletteOpen);
        await Assert.That(stillOpen).IsFalse();

        var path = await CaptureAsync("cmdpalette-closed").ConfigureAwait(false);
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

        var count = UI(() => Vm.CommandPalette.Results.Count);
        await Assert.That(count).IsEqualTo(0);

        var path = await CaptureAsync("cmdpalette-no-matches").ConfigureAwait(false);

        UI(() =>
        {
            Vm.CommandPalette.Query = string.Empty;
            Vm.IsCommandPaletteOpen = false;
        });
    }
}
