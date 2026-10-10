using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Harbor.App.Avalonia.Views.Components;
using Harbor.App.Avalonia.Views.Controls;
using Harbor.App.Avalonia.Views.Overlays;
using Harbor.App.Avalonia.Views.Shell;
using Harbor.Ui.Framework;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core.Enums;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Standalone-control component E2E tests — surfaces that do not live in
///     the MainWindow tree (or only appear nested inside other overlays), so
///     each test hosts the control in its own window: ComposerView,
///     ToolCallCardView (expand + status pills), EmptyState, ModalHostView,
///     and the ProviderModelPicker inside Settings.
/// </summary>
[NotInParallel("e2e-framework")]
public sealed class StandaloneControlsTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync("Standalone").ConfigureAwait(false);

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
    ///     ComposerView inflates against the live chat VM with the helper
    ///     line and the Send button.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Composer_Inflates_ShowsHelperAndSend()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var chat = UI(() => Vm.Chat);
        var window = ShowHost(new ComposerView { DataContext = chat });
        try
        {
            var hasHelper = await Driver.WaitForTextInWindowAsync(
                window, "Enter send", TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(hasHelper).IsTrue();

            var hasSend = await Driver.WaitForTextInWindowAsync(
                window, "Send", TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await Assert.That(hasSend).IsTrue();
        }
        finally
        {
            CloseHost(window);
        }

        await CaptureAsync("standalone-composer").ConfigureAwait(false);
    }

    /// <summary>
    ///     While the agent runs, the composer swaps Send for Stop and shows
    ///     the status message; restoring idle swaps back.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Composer_Running_SwapsSendForStop()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var chat = UI(() => Vm.Chat);
        var window = ShowHost(new ComposerView { DataContext = chat });
        try
        {
            UI(() =>
            {
                chat.IsAgentRunning = true;
                chat.StatusMessage = "Agent is running…";
            });

            var hasStop = await Driver.WaitForConditionAsync(
                () => UI(() => RenderedTextOf(window).Contains("Stop", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(hasStop).IsTrue();

            var sendHidden = await Driver.WaitForConditionAsync(
                () => UI(() => !RenderedTextOf(window).Contains("Send", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(sendHidden).IsTrue();

            UI(() =>
            {
                chat.IsAgentRunning = false;
                chat.StatusMessage = string.Empty;
            });

            var sendBack = await Driver.WaitForConditionAsync(
                () => UI(() => RenderedTextOf(window).Contains("Send", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(sendBack).IsTrue();
        }
        finally
        {
            UI(() =>
            {
                chat.IsAgentRunning = false;
                chat.StatusMessage = string.Empty;
            });
            CloseHost(window);
        }

        await CaptureAsync("standalone-composer-running").ConfigureAwait(false);
    }

    /// <summary>
    ///     ToolCallCard expands: args/result previews are hidden while
    ///     collapsed and render after expanding.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ToolCallCard_Expand_ShowsArgsAndResult()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        const string argsText = "{\"path\": \"src/App.cs\"}";
        const string resultText = "Reading src/App.cs";
        var cardVm = UI(() => new ToolCallViewModel
        {
            ToolName = "read",
            IconText = "R",
            ArgsPreview = argsText,
            ResultPreview = resultText,
            Status = ToolCallState.Running,
            IsExpanded = false,
        });
        var window = ShowHost(new ToolCallCardView { DataContext = cardVm }, 420, 160);
        try
        {
            var hasName = await Driver.WaitForTextInWindowAsync(
                window, "read", TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(hasName).IsTrue();

            var pill = UI(() => cardVm.StatusPill);
            await Assert.That(pill).IsEqualTo("running");

            var hiddenWhileCollapsed = UI(() =>
                !RenderedTextOf(window).Contains(argsText, StringComparison.Ordinal));
            await Assert.That(hiddenWhileCollapsed).IsTrue();

            UI(() => cardVm.IsExpanded = true);

            var argsShown = await Driver.WaitForConditionAsync(
                () => UI(() => RenderedTextOf(window).Contains(argsText, StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(argsShown).IsTrue();

            var resultShown = await Driver.WaitForConditionAsync(
                () => UI(() => RenderedTextOf(window).Contains(resultText, StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(resultShown).IsTrue();
        }
        finally
        {
            CloseHost(window);
        }

        await CaptureAsync("standalone-toolcall-expand").ConfigureAwait(false);
    }

    /// <summary>
    ///     ToolCallCard status transitions render the pill + duration:
    ///     success ("ok" + ms) then error ("err").
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ToolCallCard_Status_TransitionsRenderPillAndDuration()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var cardVm = UI(() => new ToolCallViewModel
        {
            ToolName = "edit",
            IconText = "E",
            ArgsPreview = "{\"path\": \"src/App.cs\"}",
            ResultPreview = "Applying hunk",
            Status = ToolCallState.Running,
            IsExpanded = false,
        });
        var window = ShowHost(new ToolCallCardView { DataContext = cardVm }, 420, 160);
        try
        {
            var shown = await Driver.WaitForTextInWindowAsync(
                window, "edit", TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(shown).IsTrue();

            UI(() => cardVm.Complete(ToolCallState.Success, "Applied 1 hunk +12 -3", TimeSpan.FromMilliseconds(340)));

            var hasOk = await Driver.WaitForConditionAsync(
                () => UI(() => RenderedTextOf(window).Contains("ok", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(hasOk).IsTrue();

            var hasDuration = await Driver.WaitForConditionAsync(
                () => UI(() => RenderedTextOf(window).Contains("340ms", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(hasDuration).IsTrue();

            UI(() => cardVm.Complete(ToolCallState.Error, "Exit 1: file not found", TimeSpan.FromMilliseconds(120)));

            var hasErr = await Driver.WaitForConditionAsync(
                () => UI(() => RenderedTextOf(window).Contains("err", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(hasErr).IsTrue();
        }
        finally
        {
            CloseHost(window);
        }

        await CaptureAsync("standalone-toolcall-status").ConfigureAwait(false);
    }

    /// <summary>
    ///     EmptyState renders a custom title + subtitle.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task EmptyState_Inflates_ShowsTitleAndSubtitle()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var window = ShowHost(new EmptyState
        {
            Title = "No sessions yet",
            Subtitle = "Create a session to begin.",
        });
        try
        {
            var hasTitle = await Driver.WaitForTextInWindowAsync(
                window, "No sessions yet", TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(hasTitle).IsTrue();

            var hasSubtitle = await Driver.WaitForTextInWindowAsync(
                window, "Create a session to begin.", TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await Assert.That(hasSubtitle).IsTrue();
        }
        finally
        {
            CloseHost(window);
        }

        await CaptureAsync("standalone-emptystate").ConfigureAwait(false);
    }

    /// <summary>
    ///     ModalHostView binds its scrim to the shell overlay state: with no
    ///     overlay the scrim exists but stays hidden.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ModalHost_Inflates_ScrimHiddenWithoutOverlay()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var shell = UI(() => Vm);
        var window = ShowHost(new ModalHostView { DataContext = shell }, 520, 200);
        try
        {
            var scrim = UI(() => window.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(b => b.Name == "PART_Scrim"));
            await Assert.That(scrim).IsNotNull();

            var overlay = UI(() => shell.HasOverlay);
            await Assert.That(overlay).IsFalse();

            var scrimHidden = UI(() => scrim!.IsVisible == false);
            await Assert.That(scrimHidden).IsTrue();
        }
        finally
        {
            CloseHost(window);
        }

        await CaptureAsync("standalone-modalhost").ConfigureAwait(false);
    }

    /// <summary>
    ///     ProviderModelPicker inside Settings shows the search box and the
    ///     TwoWay binding pushes VM text into the view.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ProviderModelPicker_InSettings_BindsSearchText()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsSettingsOpen = true);
        var settingsOpen = await Driver.WaitForConditionAsync(
            () => UI(() => Vm.IsSettingsOpen), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Assert.That(settingsOpen).IsTrue();

        try
        {
            var found = await Driver.WaitForConditionAsync(
                () => UI(() => Driver.MainWindow.GetVisualDescendants()
                    .OfType<TextBox>()
                    .Any(t => t.PlaceholderText == "Search models or providers…")),
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Assert.That(found).IsTrue();

            var picker = UI(() => Vm.Settings.Picker);
            await Assert.That(picker).IsNotNull();

            UI(() => picker!.SearchText = "qo9-model-filter");
            var pushed = await Driver.WaitForConditionAsync(
                () => UI(() => Driver.MainWindow.GetVisualDescendants()
                    .OfType<TextBox>()
                    .Any(t => t.PlaceholderText == "Search models or providers…"
                        && t.Text == "qo9-model-filter")),
                TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Assert.That(pushed).IsTrue();

            UI(() => picker!.SearchText = string.Empty);
        }
        finally
        {
            UI(() => Vm.IsSettingsOpen = false);
        }

        await CaptureAsync("standalone-picker-settings").ConfigureAwait(false);
    }
}
