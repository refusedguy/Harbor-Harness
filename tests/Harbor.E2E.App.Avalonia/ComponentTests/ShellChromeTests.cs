using Avalonia.Controls;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core.Enums;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Shell-chrome component E2E tests — the always-visible MainWindow frame:
///     TitleBar trigger, ActivityRail toggle, SessionsFlyout open/close,
///     RightDrawer open/close, and the Sessions board tab.
/// </summary>
/// <remarks>
///     <para>
///         All five surfaces live in the MainWindow visual tree, so tests drive
///         them through <c>MainViewModel</c> flags/commands and assert with the
///         visibility-honest <c>WaitForRenderedTextAsync</c> probe (the legacy
///         unfiltered walk sees text inside collapsed overlays — C1).
///     </para>
/// </remarks>
[NotInParallel("e2e-framework")]
public sealed class ShellChromeTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync("ShellChrome").ConfigureAwait(false);

    /// <summary>
    ///     TitleBar shows the "Search or jump to..." trigger; clicking it opens
    ///     the command palette, and closing restores the idle state.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task TitleBar_Trigger_OpensCommandPalette()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var hasTrigger = await Driver.WaitForRenderedTextAsync("Search or jump to...", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(hasTrigger).IsTrue();

        var trigger = Driver.FindControlByName<Button>("TitleBar_CommandPaletteTrigger");
        await Assert.That(trigger).IsNotNull();
        await Driver.ClickAsync(trigger!).ConfigureAwait(false);

        var opened = await Driver.WaitForConditionAsync(
            () => UI(() => Vm.IsCommandPaletteOpen), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Assert.That(opened).IsTrue();

        UI(() => Vm.IsCommandPaletteOpen = false);
        var closed = UI(() => Vm.IsCommandPaletteOpen);
        await Assert.That(closed).IsFalse();

        await CaptureAsync("shell-titlebar-palette").ConfigureAwait(false);
    }

    /// <summary>
    ///     ActivityRail toggle collapses the sidebar (the "Explorer" label
    ///     disappears from the rendered tree) and expands it back.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ActivityRail_Toggle_CollapsesAndExpandsSidebar()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        UI(() => Vm.IsSidebarVisible = true);

        var hasExplorer = await Driver.WaitForRenderedTextAsync("Explorer", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(hasExplorer).IsTrue();

        var toggle = Driver.FindControlByName<Button>("Rail_ToggleButton");
        await Assert.That(toggle).IsNotNull();
        await Driver.ClickAsync(toggle!).ConfigureAwait(false);

        var collapsed = await Driver.WaitForConditionAsync(
            () => UI(() => !Vm.IsSidebarVisible), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Assert.That(collapsed).IsTrue();

        var explorerGone = await Driver.WaitForConditionAsync(
            () => !UI(() => Driver.GetRenderedText().Contains("Explorer", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Assert.That(explorerGone).IsTrue();

        await Driver.ClickAsync(toggle!).ConfigureAwait(false);
        var expanded = await Driver.WaitForConditionAsync(
            () => UI(() => Vm.IsSidebarVisible), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Assert.That(expanded).IsTrue();

        await CaptureAsync("shell-activityrail-toggled").ConfigureAwait(false);
    }

    /// <summary>
    ///     SessionsFlyout opens with the SESSIONS header + search box and
    ///     closes back to the chat view.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task SessionsFlyout_Open_ShowsHeaderAndSearch()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        UI(() => Vm.IsSessionsFlyoutOpen = false);

        UI(() => Vm.IsSessionsFlyoutOpen = true);

        var hasHeader = await Driver.WaitForRenderedTextAsync("SESSIONS", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(hasHeader).IsTrue();

        var searchBox = Driver.FindControlByName<TextBox>("SessionsFlyout_SearchBox");
        await Assert.That(searchBox).IsNotNull();
        var watermark = UI(() => searchBox!.PlaceholderText);
        await Assert.That(watermark).IsEqualTo("Search sessions...");

        UI(() => Vm.IsSessionsFlyoutOpen = false);
        var closed = UI(() => Vm.IsSessionsFlyoutOpen);
        await Assert.That(closed).IsFalse();

        await CaptureAsync("shell-sessions-flyout").ConfigureAwait(false);
    }

    /// <summary>
    ///     RightDrawer opens on the requested tab (header + Ready footer render)
    ///     and closes via the toggle command.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task RightDrawer_Open_ShowsTabAndCloses()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        UI(() => Vm.ToggleRightDrawerCommand.Execute(null));

        UI(() => Vm.ToggleRightDrawerCommand.Execute("inspector"));

        var opened = await Driver.WaitForConditionAsync(
            () => UI(() => Vm.IsRightDrawerOpen), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Assert.That(opened).IsTrue();

        var tab = UI(() => Vm.RightDrawerTab);
        await Assert.That(tab).IsEqualTo("inspector");

        var hasTab = await Driver.WaitForRenderedTextAsync("inspector", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(hasTab).IsTrue();

        var hasFooter = await Driver.WaitForRenderedTextAsync("Ready", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasFooter).IsTrue();

        UI(() => Vm.ToggleRightDrawerCommand.Execute(null));
        var closed = UI(() => Vm.IsRightDrawerOpen);
        await Assert.That(closed).IsFalse();

        await CaptureAsync("shell-rightdrawer").ConfigureAwait(false);
    }

    /// <summary>
    ///     Board tab: switching to "board" flips ActiveView and back to "chat"
    ///     restores the transcript view.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Board_SwitchView_FlipsActiveView()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.SwitchView("board"));
        var onBoard = await Driver.WaitForConditionAsync(
            () => UI(() => Vm.ActiveView == "board"), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Assert.That(onBoard).IsTrue();

        UI(() => Vm.SwitchView("chat"));
        var onChat = await Driver.WaitForConditionAsync(
            () => UI(() => Vm.ActiveView == "chat"), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Assert.That(onChat).IsTrue();

        await CaptureAsync("shell-board-tab").ConfigureAwait(false);
    }
}
