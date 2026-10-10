using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Harbor.App.Avalonia.Views.Components;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core.Enums;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Micro-control component E2E tests — the small reusable pills the shell
///     is built from: <c>Kbd</c> (keyboard-hint chip) and
///     <c>StatusBadge</c> (dot + label). Each test hosts the control in its
///     own window, following the <c>StandaloneControlsTests</c> pattern.
/// </summary>
[NotInParallel("e2e-framework")]
public sealed class MicroControlsTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync("MicroControls").ConfigureAwait(false);

    /// <summary>Visibility-honest text walk for a standalone host window.</summary>
    private static string RenderedTextOf(Visual visual)
    {
        var sb = new StringBuilder();
        AppendVisible(visual, sb);
        return sb.ToString();
    }

    private static void AppendVisible(Visual visual, StringBuilder sb)
    {
        if (!visual.IsVisible)
        {
            return;
        }

        switch (visual)
        {
            case TextBlock tb when tb.Text is { } t:
                sb.AppendLine(t);
                break;
            case TextBox txb when txb.Text is { } tx:
                sb.AppendLine(tx);
                break;
            case ContentControl cc when cc.Content is string s:
                sb.AppendLine(s);
                break;
        }

        foreach (var child in visual.GetVisualChildren())
        {
            AppendVisible(child, sb);
        }
    }

    /// <summary>Show a control in a host window on the UI thread.</summary>
    private static Window ShowHost(Control content, double width = 560, double height = 220)
    {
        return UI(() =>
        {
            GoldenFrame.PinDarkTheme();
            var window = GoldenFrame.CreateHostWindow(content, width, height);
            window.Show();
            return window;
        });
    }

    private static void CloseHost(Window window) => UI(() => window.Close());

    /// <summary>
    ///     Kbd inflates with the bound keystroke and re-renders when the
    ///     binding changes.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Kbd_Inflates_ShowsKeysAndUpdates()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var kbd = UI(() => new Kbd { Keys = "Ctrl+B" });
        var window = ShowHost(kbd, 200, 80);
        try
        {
            var shown = await Driver.WaitForTextInWindowAsync(
                window, "Ctrl+B", TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(shown).IsTrue();

            UI(() => kbd.Keys = "Ctrl+Shift+T");
            var updated = await Driver.WaitForConditionAsync(
                () => UI(() => RenderedTextOf(window).Contains("Ctrl+Shift+T", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(updated).IsTrue();
        }
        finally
        {
            CloseHost(window);
        }

        await CaptureAsync("micro-kbd").ConfigureAwait(false);
    }

    /// <summary>
    ///     StatusBadge inflates with the dot + label and re-renders the label
    ///     when the status text changes.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task StatusBadge_Inflates_ShowsLabelAndUpdates()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var badge = UI(() => new StatusBadge { StatusText = "running", BrushKey = "MochaGreen" });
        var window = ShowHost(badge, 200, 80);
        try
        {
            var shown = await Driver.WaitForTextInWindowAsync(
                window, "running", TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(shown).IsTrue();

            UI(() => badge.StatusText = "idle");
            var updated = await Driver.WaitForConditionAsync(
                () => UI(() => RenderedTextOf(window).Contains("idle", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(updated).IsTrue();
        }
        finally
        {
            CloseHost(window);
        }

        await CaptureAsync("micro-statusbadge").ConfigureAwait(false);
    }
}
