using Avalonia.Controls;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core.Enums;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Overlay-surface component E2E tests — the floating MainWindow overlays:
///     Diff viewer, TokenUsage popover, and the Provider browser.
/// </summary>
/// <remarks>
///     <para>
///         Overlays stay attached to the visual tree while collapsed, so every
///         visibility assertion uses the visibility-honest
///         <c>WaitForRenderedTextAsync</c> probe, never the unfiltered walk.
///     </para>
/// </remarks>
[NotInParallel("e2e-framework")]
public sealed class OverlaySurfacesTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync("Overlays").ConfigureAwait(false);

    /// <summary>
    ///     Diff overlay opens with the viewer chrome (title + Compute button).
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Diff_Open_ShowsViewerChrome()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsDiffOpen = true);

        var hasTitle = await Driver.WaitForRenderedTextAsync("Diff viewer", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(hasTitle).IsTrue();

        var hasCompute = await Driver.WaitForRenderedTextAsync("Compute", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasCompute).IsTrue();

        UI(() => Vm.IsDiffOpen = false);
        var closed = UI(() => Vm.IsDiffOpen);
        await Assert.That(closed).IsFalse();

        await CaptureAsync("overlay-diff-open").ConfigureAwait(false);
    }

    /// <summary>
    ///     Diff compute projects a modified row for a one-line change and the
    ///     new text renders in the overlay.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Diff_Compute_ShowsModifiedRow()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsDiffOpen = true);
        var opened = await Driver.WaitForRenderedTextAsync("Diff viewer", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(opened).IsTrue();

        UI(() =>
        {
            Vm.Diff.LeftText = "line1\nline2\nline3";
            Vm.Diff.RightText = "line1\nline2 changed\nline3";
            Vm.Diff.ComputeCommand.Execute(null);
        });

        var rows = UI(() => Vm.Diff.Rows.Count);
        await Assert.That(rows).IsGreaterThan(0);

        var hasModified = UI(() => Vm.Diff.Rows.Any(r => r.Kind == "modified"));
        await Assert.That(hasModified).IsTrue();

        var hasNewText = await Driver.WaitForRenderedTextAsync("line2 changed", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(hasNewText).IsTrue();

        UI(() => Vm.IsDiffOpen = false);

        await CaptureAsync("overlay-diff-computed").ConfigureAwait(false);
    }

    /// <summary>
    ///     TokenUsage overlay opens with the per-turn chart labels.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task TokenUsage_Open_ShowsTotalsLabels()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsTokenUsageOpen = true);

        var hasTitle = await Driver.WaitForRenderedTextAsync("Token usage per turn", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(hasTitle).IsTrue();

        var hasInput = await Driver.WaitForRenderedTextAsync("Total input", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasInput).IsTrue();

        var hasCost = await Driver.WaitForRenderedTextAsync("Cumulative cost", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasCost).IsTrue();

        UI(() => Vm.IsTokenUsageOpen = false);
        var closed = UI(() => Vm.IsTokenUsageOpen);
        await Assert.That(closed).IsFalse();

        await CaptureAsync("overlay-tokenusage-open").ConfigureAwait(false);
    }

    /// <summary>
    ///     TokenUsage clear empties the per-turn bars while the overlay stays open.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task TokenUsage_Clear_EmptiesBars()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsTokenUsageOpen = true);
        var opened = await Driver.WaitForRenderedTextAsync("Token usage per turn", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(opened).IsTrue();

        UI(() => Vm.TokenUsage.ClearCommand.Execute(null));
        var bars = UI(() => Vm.TokenUsage.Bars.Count);
        await Assert.That(bars).IsEqualTo(0);

        var stillOpen = UI(() => Vm.IsTokenUsageOpen);
        await Assert.That(stillOpen).IsTrue();

        UI(() => Vm.IsTokenUsageOpen = false);

        await CaptureAsync("overlay-tokenusage-cleared").ConfigureAwait(false);
    }

    /// <summary>
    ///     ProviderBrowser overlay opens with the browser chrome
    ///     (title + Providers/Models groups).
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ProviderBrowser_Open_ShowsBrowserChrome()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsProviderBrowserOpen = true);

        var hasTitle = await Driver.WaitForRenderedTextAsync("Provider browser", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(hasTitle).IsTrue();

        var hasProviders = await Driver.WaitForRenderedTextAsync("Providers", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasProviders).IsTrue();

        var hasModels = await Driver.WaitForRenderedTextAsync("Models", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasModels).IsTrue();

        UI(() => Vm.IsProviderBrowserOpen = false);
        var closed = UI(() => Vm.IsProviderBrowserOpen);
        await Assert.That(closed).IsFalse();

        await CaptureAsync("overlay-providerbrowser-open").ConfigureAwait(false);
    }
}
