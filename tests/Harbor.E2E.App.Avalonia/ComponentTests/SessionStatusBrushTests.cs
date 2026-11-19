using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Harbor.Abstractions.Models;
using Harbor.App.Avalonia.Views.Components;
using Harbor.Ui.Framework.ViewModels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core.Enums;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     #3422 — the session-status brush key has one product source: the
///     <c>SessionStatusToBrushKeyConverter</c> registered in the sessions
///     flyout's XAML resource dictionary and consumed by every row binding.
///     These tests drive that real binding in the headless app.
/// </summary>
/// <remarks>
///     The converter produces a KEY, not a brush. A key is a property of the
///     session status, not of the palette, so a theme switch must not change
///     it. The golden assertion below pins the same claim at the rendered-frame
///     level: a dark → light → dark round trip leaves the status row's frame
///     identical to the first dark frame.
/// </remarks>
[NotInParallel("e2e-framework")]
public sealed class SessionStatusBrushTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync("SessionStatusBrush").ConfigureAwait(false);

    /// <summary>
    ///     The key bound by the registered converter stays canonical in both
    ///     palettes (Working → "MochaYellow"), so the row and the palette cannot
    ///     disagree about which status uses which colour.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task StatusBrushKey_RegisteredConverter_IsStableAcrossThemeSwitch()
    {
        await SeedWorkingSessionAsync().ConfigureAwait(false);

        var keys = UI(() =>
        {
            GoldenFrame.PinDarkTheme();
            string dark = StatusKeyOfFirstRow();

            ApplyPalette("Lumen", ThemeVariant.Light);
            string light = StatusKeyOfFirstRow();

            GoldenFrame.PinDarkTheme();
            string restored = StatusKeyOfFirstRow();
            return (dark, light, restored);
        });

        await Assert.That(keys.dark).IsEqualTo("MochaYellow");
        await Assert.That(keys.light).IsEqualTo(keys.dark);
        await Assert.That(keys.restored).IsEqualTo(keys.dark);
    }

    /// <summary>
    ///     The status row's golden frame is unchanged after switching theme and
    ///     back. The converter keeps producing the same key, so the binding has
    ///     nothing to re-resolve into a different semantic colour.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Golden")]
    public async Task StatusBrush_Golden_IsStableAcrossThemeSwitch()
    {
        await SeedWorkingSessionAsync().ConfigureAwait(false);

        var frames = UI(() =>
        {
            GoldenFrame.PinDarkTheme();
            string dark = SnapshotFirstRow();

            ApplyPalette("Lumen", ThemeVariant.Light);
            _ = SnapshotFirstRow();

            GoldenFrame.PinDarkTheme();
            string restored = SnapshotFirstRow();
            return (dark, restored);
        });

        await Assert.That(frames.restored).IsEqualTo(frames.dark).Because(
            "the status row golden must survive a dark → light → dark theme round trip");
    }

    /// <summary>Open the flyout with one realized session in the Working state.</summary>
    private static async Task SeedWorkingSessionAsync()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        UI(() => Vm.IsSessionsFlyoutOpen = true);
        UI(() =>
        {
            Vm.Sessions.SearchText = string.Empty;
            var sessions = Vm.Sessions.Sessions;
            sessions.Clear();
            sessions.Add(new SessionItemViewModel(
                "s-status-brush", "Status brush row", "code", "qwen2.5-coder:7b",
                "ollama", DateTimeOffset.UtcNow, 1, "/tmp/status-brush")
            {
                Status = SessionStatus.Working,
            });
        });
        await Driver.ShowMainWindowAsync().ConfigureAwait(false);
        bool realized = await Driver.WaitForConditionAsync(
            () => UI(() => FirstRow() is not null),
            TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await Assert.That(realized).IsTrue().Because(Driver.LastWaitDiagnostic);
    }

    /// <summary>The first realized row of the sessions flyout.</summary>
    private static SessionRow? FirstRow()
    {
        foreach (var visual in Driver.MainWindow.GetVisualDescendants())
        {
            if (visual is SessionRow row)
                return row;
        }
        return null;
    }

    private static string StatusKeyOfFirstRow() =>
        (FirstRow() ?? throw new InvalidOperationException("the flyout realized no SessionRow")).StatusColorKey;

    private static string SnapshotFirstRow()
    {
        var row = FirstRow() ?? throw new InvalidOperationException("the flyout realized no SessionRow");
        row.UpdateLayout();
        return FrameSnapshot.Dump(row);
    }

    /// <summary>Swap the HDS palette slot and the theme variant, as ThemeService does.</summary>
    private static void ApplyPalette(string paletteName, ThemeVariant variant)
    {
        var app = global::Avalonia.Application.Current
            ?? throw new InvalidOperationException("Application.Current not initialized.");
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 1)
        {
            merged[1] = new ResourceInclude(new Uri("avares://Harbor.App.Avalonia/", UriKind.Absolute))
            {
                Source = new Uri($"avares://Harbor.App.Avalonia/Themes/Hds/{paletteName}.axaml", UriKind.Absolute),
            };
        }
        app.RequestedThemeVariant = variant;
    }
}
