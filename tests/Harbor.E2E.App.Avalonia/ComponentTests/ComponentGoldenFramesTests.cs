using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Harbor.App.Avalonia.Views;
using Harbor.App.Avalonia.Views.Controls;
using Harbor.Ui.Framework;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.ViewModels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Frames-lane screenshot-diff tests (#417): eight deterministic component
///     states captured through <see cref="GoldenFrame.CaptureSettledFrame" />
///     and compared via <see cref="GoldenFrame.VerifyFrame" /> against
///     <c>tests/fixtures/avalonia/frames/&lt;surface&gt;/&lt;scenario&gt;.png</c>
///     with hashes mirrored in
///     <c>ComponentTests/baselines/sha256.json</c>.
///
///     Only control types already goldenized in <see cref="GoldenFrameTests" />
///     are used, with fixed text, fixed durations and no animations, so every
///     frame settles to identical bytes. Regenerate with
///     <c>HARBOR_UPDATE_GOLDENS=1</c>, which writes the PNG and the manifest
///     entry together.
/// </summary>
[NotInParallel("e2e-framework")]
public sealed class ComponentGoldenFramesTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync("Frames").ConfigureAwait(false);

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    [SkipGoldenOnCi]
    public async Task Frames_Toolcall_Success()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (png, sha) = UI(() =>
        {
            GoldenFrame.PinDarkTheme();
            var card = new ToolCallCardView
            {
                DataContext = new ToolCallViewModel
                {
                    ToolName = "edit",
                    IconText = "E",
                    ArgsPreview = "{\"path\": \"src/App.cs\"}",
                    ResultPreview = "Applied 1 hunk +12 -3",
                    Status = ToolCallState.Success,
                    Duration = TimeSpan.FromMilliseconds(340),
                },
            };
            var host = new StackPanel { Margin = new Thickness(12) };
            host.Children.Add(card);
            var window = GoldenFrame.CreateHostWindow(host, 380, 120);
            var frame = GoldenFrame.CaptureSettledFrame(window);
            window.Close();
            return frame;
        });

        GoldenFrame.VerifyFrame("toolcall", "success", png, sha);
        await Assert.That(sha).HasLength().EqualTo(64);
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    [SkipGoldenOnCi]
    public async Task Frames_Toolcall_Error()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (png, sha) = UI(() =>
        {
            GoldenFrame.PinDarkTheme();
            var card = new ToolCallCardView
            {
                DataContext = new ToolCallViewModel
                {
                    ToolName = "edit",
                    IconText = "E",
                    ArgsPreview = "{\"path\": \"src/App.cs\"}",
                    ResultPreview = "Exit 1: file not found",
                    Status = ToolCallState.Error,
                    Duration = TimeSpan.FromMilliseconds(120),
                },
            };
            var host = new StackPanel { Margin = new Thickness(12) };
            host.Children.Add(card);
            var window = GoldenFrame.CreateHostWindow(host, 380, 120);
            var frame = GoldenFrame.CaptureSettledFrame(window);
            window.Close();
            return frame;
        });

        GoldenFrame.VerifyFrame("toolcall", "error", png, sha);
        await Assert.That(sha).HasLength().EqualTo(64);
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    [SkipGoldenOnCi]
    public async Task Frames_Toolcall_Running()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (png, sha) = UI(() =>
        {
            GoldenFrame.PinDarkTheme();
            var card = new ToolCallCardView
            {
                DataContext = new ToolCallViewModel
                {
                    ToolName = "edit",
                    IconText = "E",
                    ArgsPreview = "{\"path\": \"src/App.cs\"}",
                    ResultPreview = "Running: reading src/App.cs",
                    Status = ToolCallState.Running,
                    Duration = TimeSpan.FromMilliseconds(200),
                },
            };
            var host = new StackPanel { Margin = new Thickness(12) };
            host.Children.Add(card);
            var window = GoldenFrame.CreateHostWindow(host, 380, 120);
            var frame = GoldenFrame.CaptureSettledFrame(window);
            window.Close();
            return frame;
        });

        GoldenFrame.VerifyFrame("toolcall", "running", png, sha);
        await Assert.That(sha).HasLength().EqualTo(64);
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    [SkipGoldenOnCi]
    public async Task Frames_Toast_Single()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var vm = new FramesToastHostViewModel();
        vm.Toasts.Add(new ToastNotification("Info: connection established.", ToastKind.Info));
        var (png, sha) = UI(() =>
        {
            GoldenFrame.PinDarkTheme();
            var window = GoldenFrame.CreateHostWindow(
                new ToastNotificationsView { DataContext = vm }, 420, 140);
            var frame = GoldenFrame.CaptureSettledFrame(window);
            window.Close();
            return frame;
        });

        GoldenFrame.VerifyFrame("toast", "single", png, sha);
        await Assert.That(sha).HasLength().EqualTo(64);
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    [SkipGoldenOnCi]
    public async Task Frames_Toast_Triple()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var vm = new FramesToastHostViewModel();
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

        GoldenFrame.VerifyFrame("toast", "triple", png, sha);
        await Assert.That(sha).HasLength().EqualTo(64);
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    [SkipGoldenOnCi]
    public async Task Frames_Typewriter_Plain()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (png, sha) = UI(() =>
        {
            GoldenFrame.PinDarkTheme();
            var text = new TypewriterStreamingText
            {
                Text = "All checks passed.",
                IsStreaming = false,
            };
            var host = new StackPanel { Margin = new Thickness(12) };
            host.Children.Add(text);
            var window = GoldenFrame.CreateHostWindow(host, 380, 90);
            var frame = GoldenFrame.CaptureSettledFrame(window);
            window.Close();
            return frame;
        });

        GoldenFrame.VerifyFrame("typewriter", "plain", png, sha);
        await Assert.That(sha).HasLength().EqualTo(64);
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    [SkipGoldenOnCi]
    public async Task Frames_Sparkline_Ramp()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (png, sha) = UI(() =>
        {
            GoldenFrame.PinDarkTheme();
            var sparkline = new Sparkline
            {
                Values = [2, 5, 3, 8, 4, 9, 6, 7],
                StrokeBrush = new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)),
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

        GoldenFrame.VerifyFrame("sparkline", "ramp", png, sha);
        await Assert.That(sha).HasLength().EqualTo(64);
    }

    [Test]
    [Category("E2E")]
    [Category("Golden")]
    [SkipGoldenOnCi]
    public async Task Frames_Sparkline_Flat()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (png, sha) = UI(() =>
        {
            GoldenFrame.PinDarkTheme();
            var sparkline = new Sparkline
            {
                Values = [5, 5, 5, 5, 5, 5, 5, 5],
                StrokeBrush = new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)),
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

        GoldenFrame.VerifyFrame("sparkline", "flat", png, sha);
        await Assert.That(sha).HasLength().EqualTo(64);
    }
}

/// <summary>Minimal toast host for the frames lane.</summary>
file sealed class FramesToastHostViewModel
{
    public ObservableCollection<ToastNotification> Toasts { get; } = new();
}
