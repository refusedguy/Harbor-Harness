using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Harbor.App.Avalonia.Views;
using Harbor.App.Avalonia.Views.Controls;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.ViewModels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Screenshot-diff tests (sprint Testing Strategy З.2): four core
///     components rendered in a headless Skia compositor, captured as PNG and
///     compared against SHA-256 golden baselines under
///     <c>tests/fixtures/golden/&lt;TestName&gt;.golden.png</c>.
///
///     ANY pixel-level difference from the baseline fails the test — this is
///     a hash compare, not an existence check. Regenerate baselines with
///     <c>HARBOR_UPDATE_GOLDENS=1</c> when a visual change is intended.
///
///     The goldens are skipped on CI (shared runners render with a different
///     Skia/font stack, so the sha never matches the dev-captured baseline —
///     ToolCallCardView_GoldenFrame proved it on the first ci.yml run). Set
///     <c>HARBOR_GOLDENS_STRICT=1</c> on a pinned reference machine to
///     enforce them there.
/// </summary>
[NotInParallel]
public sealed class GoldenFrameTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync("Golden").ConfigureAwait(false);

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    [SkipGoldenOnCi]
    public async Task ToolCallCardView_GoldenFrame()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var vm = new ToolCallViewModel
        {
            ToolName = "edit",
            IconText = "✎",
            ArgsPreview = "{\"path\": \"src/App.cs\"}",
            ResultPreview = "Applied 1 hunk — +12 −3",
            Status = ToolCallStatus.Success,
            Duration = TimeSpan.FromMilliseconds(340),
        };

        // Everything runs in ONE synchronous UI-thread delegate: pin theme →
        // construct → capture → close. The Avalonia dispatcher cannot pump
        // mid-delegate, so no orphaned continuation from another host can
        // flip the theme between pin and pixels.
        var (png, sha) = UI(() =>
        {
            GoldenFrame.PinDarkTheme();
            var card = new ToolCallCardView { DataContext = vm };
            var host = new StackPanel { Margin = new Thickness(12) };
            host.Children.Add(card);
            var window = GoldenFrame.CreateHostWindow(host, 380, 120);
            var frame = GoldenFrame.CaptureSettledFrame(window);
            window.Close();
            return frame;
        });

        GoldenFrame.Verify(nameof(ToolCallCardView_GoldenFrame), png, sha);
        await Assert.That(sha).HasLength().EqualTo(64);
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    [SkipGoldenOnCi]
    public async Task TypewriterStreamingText_GoldenFrame()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // IsStreaming=false keeps the blink cursor hidden entirely — the
        // 530 ms blink timer can never flip the frame between runs.
        var (png, sha) = UI(() =>
        {
            GoldenFrame.PinDarkTheme();
            var text = new TypewriterStreamingText
            {
                Text = "Streaming code response…",
                IsStreaming = false,
            };
            var host = new StackPanel { Margin = new Thickness(12) };
            host.Children.Add(text);
            var window = GoldenFrame.CreateHostWindow(host, 380, 90);
            var frame = GoldenFrame.CaptureSettledFrame(window);
            window.Close();
            return frame;
        });

        GoldenFrame.Verify(nameof(TypewriterStreamingText_GoldenFrame), png, sha);
        await Assert.That(sha).HasLength().EqualTo(64);
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    [SkipGoldenOnCi]
    public async Task Sparkline_GoldenFrame()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Values are set WITHOUT going through OnValuesChanged (no animation
        // timer started) and the pulse phase stays at 0 — the endpoint dot
        // renders at its deterministic base radius.
        var (png, sha) = UI(() =>
        {
            GoldenFrame.PinDarkTheme();
            var sparkline = new Sparkline
            {
                Values = [4, 9, 2, 7, 5, 11, 3, 8],
                StrokeBrush = new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)), // Mocha green
                Width = 160,
                Height = 40,
            };
            var host = new StackPanel { Margin = new Thickness(12) };
            host.Children.Add(sparkline);
            var window = GoldenFrame.CreateHostWindow(host, 200, 80);
            var frame = GoldenFrame.CaptureSettledFrame(window);
            window.Close();
            return frame;
        });

        GoldenFrame.Verify(nameof(Sparkline_GoldenFrame), png, sha);
        await Assert.That(sha).HasLength().EqualTo(64);
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    [SkipGoldenOnCi]
    public async Task ToastNotificationsView_GoldenFrame()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var vm = new GoldenToastHostViewModel();
        vm.Toasts.Add(new ToastNotification("Info: connection established.", ToastKind.Info));
        vm.Toasts.Add(new ToastNotification("Success: file saved.", ToastKind.Success));
        vm.Toasts.Add(new ToastNotification("Error: provider returned 503.", ToastKind.Error));

        var (png, sha) = UI(() =>
        {
            GoldenFrame.PinDarkTheme();
            var window = GoldenFrame.CreateHostWindow(
                new ToastNotificationsView { DataContext = vm }, 420, 260);
            var frame = GoldenFrame.CaptureSettledFrame(window);
            window.Close();
            return frame;
        });

        GoldenFrame.Verify(nameof(ToastNotificationsView_GoldenFrame), png, sha);
        await Assert.That(sha).HasLength().EqualTo(64);
    }
}

/// <summary>Minimal toast host for the golden frame — same shape the
/// production MainViewModel exposes (Toasts collection only).</summary>
file sealed class GoldenToastHostViewModel
{
    public ObservableCollection<ToastNotification> Toasts { get; } = new();
}

/// <summary>
///     Skip the pixel-golden tests on shared CI runners: the SHA-256 baselines
///     are captured on a specific dev machine (Skia build + font stack), and
///     runner rasterization differs by pixels that no repo change touched.
///     Override with HARBOR_GOLDENS_STRICT=1 on a pinned reference machine.
/// </summary>
internal sealed class SkipGoldenOnCiAttribute : SkipAttribute
{
    public SkipGoldenOnCiAttribute()
        : base("pixel-golden baselines are machine-pinned — skipped on shared CI runners (HARBOR_GOLDENS_STRICT=1 to enforce)")
    { }

    /// <inheritdoc />
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(
            Environment.GetEnvironmentVariable("CI") == "1"
            && Environment.GetEnvironmentVariable("HARBOR_GOLDENS_STRICT") != "1");
}
