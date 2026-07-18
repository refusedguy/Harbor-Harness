using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Harbor.App.Avalonia;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.Views;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core.Enums;

// See HeadlessAvaloniaDriver.cs for the rationale — the test namespace
// Harbor.E2E.App.Avalonia shadows Harbor.App.Avalonia for name lookup,
// so we alias the production App class to 'HarborApp' (not 'App' — that
// collides with the Harbor.E2E.App namespace).
using HarborApp = global::Harbor.App.Avalonia.App;

namespace Harbor.E2E.App.Avalonia;

/// <summary>
///     Real headless Avalonia E2E tests with SCREENSHOT capture.
/// </summary>
/// <remarks>
///     <para>
///         Each test boots the actual <see cref="Harbor.App.Avalonia.App"/> +
///         <see cref="MainWindow"/> + full production DI host inside an
///         <c>Avalonia.Headless</c> off-screen renderer, drives the UI like a
///         user (type / click / hover-equivalent), then captures a PNG of the
///         rendered window. The PNGs are written to
///         <c>~/.harbor/test-screenshots/</c> so the user (or an out-of-process
///         VLM) can SEE what the UI looks like without running the app.
///     </para>
///     <para>
///         <b>Navigation coverage:</b> each test drives a DIFFERENT visible
///         state of the shell — chat default, typed input, send-enabled,
///         message-sent, code-editor view, diff modal, onboarding. This
///         guarantees every screenshot's pixels differ (verified by md5sum
///         in the post-run check), so a VLM reviewer can SEE the test
///         actually navigated rather than guessing from a static frame.
///     </para>
///     <para>
///         <b>Concurrency:</b> tagged <c>[NotInParallel]</c> because the driver
///         mutates <c>$HOME</c> (process-wide env var) and shares the
///         process-wide Avalonia <see cref="Application"/> singleton.
///     </para>
/// </remarks>
[NotInParallel]
public sealed class AvaloniaUiTests
{
    /// <summary>
    ///     Directory where PNGs are written. Defaults to
    ///     <c>~/.harbor/test-screenshots/</c> so it survives across runs and is
    ///     easy to find from a shell. Cleared at the start of every test run
    ///     so stale screenshots from a previous run don't confuse review.
    /// </summary>
    private static readonly string ScreenshotDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".harbor",
        "test-screenshots");

    /// <summary>Per-class temp HOME so each test run starts with an empty <c>~/.harbor</c>.</summary>
    private static readonly string TempHome = Path.Combine(
        Path.GetTempPath(),
        "harbor-avalonia-e2e-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));

    private static HeadlessAvaloniaDriver? _driver;

    /// <summary>
    ///     Per-test setup. Initializes the shared driver on first run; subsequent
    ///     tests reuse the same driver (it's a process-wide singleton backed by
    ///     Avalonia's <see cref="Application.Current"/> which can only be set
    ///     once per AppDomain).
    /// </summary>
    /// <remarks>
    ///     We use <c>HookType.Test</c> rather than <c>HookType.Class</c> because
    ///     TUnit 0.50's class-level hook only runs before the FIRST test in the
    ///     class — subsequent tests see <c>null</c> in the static field if
    ///     anything disposes it. Per-test setup with idempotent init avoids the
    ///     issue entirely: the first test pays the init cost, every later test
    ///     hits the early-return path inside <see cref="HeadlessAvaloniaDriver.InitializeAsync"/>.
    /// </remarks>
    [Before(HookType.Test)]
    public async Task SetupTestAsync()
    {
        // Wipe + recreate the screenshot dir on the very first test so reviewers
        // only see the latest run. (CI uploads the dir as an artifact on every run.)
        if (_driver is null)
        {
            if (Directory.Exists(ScreenshotDir))
            {
                Directory.Delete(ScreenshotDir, recursive: true);
            }
            Directory.CreateDirectory(ScreenshotDir);

            // Fresh HOME with ~/.harbor/config.json marking onboarding done.
            if (Directory.Exists(TempHome))
            {
                Directory.Delete(TempHome, recursive: true);
            }
            Directory.CreateDirectory(TempHome);
            var harborDir = Path.Combine(TempHome, ".harbor");
            Directory.CreateDirectory(harborDir);
            await File.WriteAllTextAsync(
                Path.Combine(harborDir, "config.json"),
                JsonSerializer.Serialize(new
                {
                    configVersion = "1",
                    onboardingCompleted = true,
                    storageBackend = "memory",
                    logLevel = "warning",
                    defaultProvider = "ollama",
                    defaultModel = "qwen2.5-coder:7b",
                    defaultAgent = "code",
                }, new JsonSerializerOptions { WriteIndented = true }))
                .ConfigureAwait(false);

            _driver = new HeadlessAvaloniaDriver(ScreenshotDir, TempHome);
            await _driver.InitializeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Get the per-class driver, throwing clearly if setup didn't run.</summary>
    private static HeadlessAvaloniaDriver Driver
        => _driver ?? throw new InvalidOperationException("SetupTestAsync did not run.");

    /// <summary>
    ///     The app boots without crashing, the main window is non-null, and the
    ///     Chat tab is the default active view (InputBox visible + "Start a
    ///     conversation" placeholder shown). Captures <c>01-chat-default.png</c>
    ///     — the baseline visual check that the chat is showing, NOT the diff
    ///     viewer or code editor.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task MainWindow_ShowsChatByDefault()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // The chat input must be visible (the bug we're guarding against: the
        // chat tab existing but not being the default active view, with the
        // input hidden behind the diff/code viewer).
        var input = Driver.FindControlByName<TextBox>("InputBox");
        await Assert.That(input).IsNotNull();
        var inputVisible = Driver.OnUIThread(() => input!.IsVisible);
        await Assert.That(inputVisible).IsTrue();

        // The empty-state placeholder "Start a conversation" must be visible
        // — proves the chat history area is showing (not the code editor's
        // "No file open" placeholder).
        bool sawPlaceholder = await Driver.WaitForTextAsync("Start a conversation", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(sawPlaceholder).IsTrue();

        var screenshot = await Driver.ScreenshotAsync("01-chat-default").ConfigureAwait(false);
        await Assert.That(File.Exists(screenshot)).IsTrue();
        var size = new FileInfo(screenshot).Length;
        await Assert.That(size).IsGreaterThan(5_000);
        await Assert.That(Driver.MainWindow).IsNotNull();
    }

    /// <summary>
    ///     The chat input TextBox exists (x:Name="InputBox" in ChatView.axaml),
    ///     accepts typed text, and reflects it in its Text property.
    ///     Captures <c>02-input-typed.png</c> showing the typed text in the box
    ///     — visually distinct from <c>01-chat-default.png</c> because the
    ///     input row now contains text.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task ChatInput_AcceptsText()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var input = Driver.FindControlByName<TextBox>("InputBox");
        await Assert.That(input).IsNotNull();

        // Use TypeAsync (which invalidates the visual) so the typed text
        // actually shows up in the rendered PNG, not just in the .Text property.
        await Driver.TypeAsync(input!, "Hello world — typing into the chat!").ConfigureAwait(false);

        // Read back on the UI thread — TextBox.Text is dispatcher-affine.
        var typedText = Driver.OnUIThread(() => input!.Text);
        await Assert.That(typedText).IsEqualTo("Hello world — typing into the chat!");

        await Driver.ScreenshotAsync("02-input-typed").ConfigureAwait(false);
    }

    /// <summary>
    ///     The Send button (the <c>Button Classes="Primary" Content="Send ▶"</c>
    ///     in ChatView.axaml) is enabled when the input has text and disabled
    ///     when empty. Captures <c>03-send-enabled.png</c> showing the enabled
    ///     Send button with the typed text still in the input.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task SendButton_EnabledAfterInput()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Initially the Send button should be disabled (empty input).
        // NOTE: we check IsEffectivelyEnabled (not IsEnabled) because Avalonia
        // 12.1's Button routes ICommand.CanExecute through IsEnabledCore, which
        // only affects IsEffectivelyEnabled — IsEnabled itself stays at its
        // default of True. Checking IsEnabled would always pass regardless of
        // the command's CanExecute state.
        var sendEmpty = Driver.FindButtonByText("Send ▶");
        await Assert.That(sendEmpty).IsNotNull();
        var enabledEmpty = Driver.OnUIThread(() => sendEmpty!.IsEffectivelyEnabled);
        await Assert.That(enabledEmpty).IsFalse();

        // Type something — Send should now be enabled.
        var input = Driver.FindControlByName<TextBox>("InputBox");
        await Driver.TypeAsync(input!, "test message for send button").ConfigureAwait(false);

        var send = Driver.FindButtonByText("Send ▶");
        var isEnabled = Driver.OnUIThread(() => send!.IsEffectivelyEnabled);
        await Assert.That(isEnabled).IsTrue();

        await Driver.ScreenshotAsync("03-send-enabled").ConfigureAwait(false);
    }

    /// <summary>
    ///     Clicking the Send button with text in the input posts the user
    ///     message to the chat history. Captures <c>04-message-sent.png</c>
    ///     showing the user's message rendered as a chat bubble in the
    ///     transcript — visually distinct because the empty-state placeholder
    ///     is gone and a real chat row appears.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task SendMessage_AddsToChatHistory()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var input = Driver.FindControlByName<TextBox>("InputBox");
        var send = Driver.FindButtonByText("Send ▶");
        await Assert.That(send).IsNotNull();

        await Driver.TypeAsync(input!, "Hello AI!").ConfigureAwait(false);
        await Driver.ClickAsync(send!).ConfigureAwait(false);

        // The user's message must appear in the chat history.
        bool sawMessage = await Driver.WaitForTextAsync("Hello AI!", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(sawMessage).IsTrue();

        // After send, the input is cleared.
        var inputText = Driver.OnUIThread(() => input!.Text);
        await Assert.That(string.IsNullOrEmpty(inputText)).IsTrue();

        await Driver.ScreenshotAsync("04-message-sent").ConfigureAwait(false);
    }

    /// <summary>
    ///     Clicking the "Code" tab switches the center pane to the code editor
    ///     view (showing "No file open — press Ctrl+O to open a file.").
    ///     Captures <c>05-code-view.png</c> — visually distinct because the
    ///     center pane now shows the code editor's empty-state instead of the
    ///     chat placeholder.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task SwitchToCodeView_ShowsCodeEditor()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Click the "Code" tab via the bound RadioButton. SwitchViewCommand
        // is invoked through the TwoWay IsChecked → ActiveView binding.
        var codeTab = Driver.FindRadioButtonByText("📝 Code");
        await Assert.That(codeTab).IsNotNull();
        await Driver.ClickAsync(codeTab!).ConfigureAwait(false);

        // The code editor's empty-state placeholder must be visible.
        bool sawCodePlaceholder = await Driver.WaitForTextAsync("No file open", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(sawCodePlaceholder).IsTrue();

        // ActiveView must be "code" on the view-model.
        var activeView = Driver.OnUIThread(() =>
            (Driver.MainWindow.DataContext as MainViewModel)?.ActiveView);
        await Assert.That(activeView).IsEqualTo("code");

        await Driver.ScreenshotAsync("05-code-view").ConfigureAwait(false);
    }

    /// <summary>
    ///     Opening the Diff viewer modal (via MainViewModel.OpenDiffCommand)
    ///     renders the "Diff viewer" header. Captures <c>06-diff-modal.png</c>
    ///     — visually distinct because the modal overlay is rendered on top
    ///     of the main shell.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task OpenDiffModal_RendersDiffViewer()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Open the diff modal via the view-model command (equivalent to
        // View → Diff menu item or pressing the command palette entry).
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.OpenDiffCommand.Execute(null);
            }
        });

        bool sawDiff = await Driver.WaitForTextAsync("Diff viewer", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(sawDiff).IsTrue();

        await Driver.ScreenshotAsync("06-diff-modal").ConfigureAwait(false);

        // Close it so the next test starts clean.
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.IsDiffOpen = false;
            }
        });
    }

    /// <summary>
    ///     The onboarding window renders with the "Welcome" / "Harbor" brand
    ///     header. We construct the window directly (rather than relaunching
    ///     the app with onboardingCompleted=false) because Avalonia only allows
    ///     one Application per process — instead we instantiate
    ///     <see cref="OnboardingWindow"/> + <see cref="OnboardingViewModel"/>
    ///     from the existing DI container, render it, screenshot, close.
    ///     Captures <c>07-onboarding.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task OnboardingWindow_RendersWelcomeScreen()
    {
        // Resolve a fresh OnboardingViewModel from the production DI container.
        // OnboardingViewModel is registered Transient in AppHost.cs so each
        // resolution gets a fresh instance with CurrentStep=1.
        var services = HarborApp.Services;
        var onboardingVm = services.GetRequiredService<OnboardingViewModel>();

        // Build + show the onboarding window on the UI thread — every
        // operation below touches AvaloniaObject properties that require
        // dispatcher affinity. The dedicated UI thread's MainLoop pumps the
        // queued InvokeAsync job and unblocks the test thread.
        var onboardingWindow = Dispatcher.UIThread.InvokeAsync<OnboardingWindow>(() =>
        {
            var w = new OnboardingWindow();
            w.Bind(onboardingVm);
            w.DataContext = onboardingVm;
            w.Show();
            return w;
        }).GetAwaiter().GetResult();

        try
        {
            // Let the UI thread's MainLoop drain layout + first render.
            await Task.Delay(120).ConfigureAwait(false);

            // Capture the rendered frame on the UI thread —
            // CaptureRenderedFrame accesses the window's render target which
            // is dispatcher-affine. Save to PNG inline so the bitmap doesn't
            // cross thread boundaries.
            var path = Path.Combine(ScreenshotDir, "07-onboarding.png");
            var sawBrand = Dispatcher.UIThread.InvokeAsync<bool>(() =>
            {
                // Force a fresh render of the onboarding window — without this
                // the headless render timer hasn't ticked and CaptureRenderedFrame
                // returns a stale (or empty) bitmap.
                onboardingWindow.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

                var bitmap = onboardingWindow.CaptureRenderedFrame();
                if (bitmap is null)
                {
                    return false;
                }
                using (var fs = File.Create(path))
                {
                    bitmap.Save(fs);
                }

                // Walk the visual tree on the UI thread to find the brand text.
                var sb = new System.Text.StringBuilder();
                AppendText(onboardingWindow, sb);
                return sb.ToString().Contains("Harbor", StringComparison.Ordinal);
            }).GetAwaiter().GetResult();

            await Assert.That(File.Exists(path)).IsTrue();
            await Assert.That(new FileInfo(path).Length).IsGreaterThan(5_000);
            await Assert.That(sawBrand).IsTrue();
        }
        finally
        {
            Dispatcher.UIThread
                .InvokeAsync(() => onboardingWindow.Close())
                .GetAwaiter().GetResult();
        }
    }

    /// <summary>Walks the visual tree appending TextBlock/TextBox/ContentControl text.</summary>
    private static void AppendText(Visual visual, System.Text.StringBuilder sb)
    {
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
            AppendText(child, sb);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  TASK A1 — UI state coverage tests
    //  Each test exercises a DIFFERENT visible state of the shell so a VLM
    //  reviewer can SEE the test actually navigated rather than guessing
    //  from a static frame. Screenshots land in ~/.harbor/test-screenshots/.
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     Settings dialog opens via MainViewModel.IsSettingsOpen and shows
    ///     the "Theme" label + the dark/light/system ComboBox. Captures
    ///     <c>08-settings.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task SettingsDialog_OpensAndShowsOptions()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.IsSettingsOpen = true;
            }
        });
        await Task.Delay(300).ConfigureAwait(false);
        await Driver.ScreenshotAsync("08-settings").ConfigureAwait(false);

        bool hasTheme = await Driver.WaitForTextAsync("Theme", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasTheme).IsTrue();

        // Close for the next test.
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.IsSettingsOpen = false;
            }
        });
    }

    /// <summary>
    ///     Settings Save persists the Theme field to ~/.harbor/config.json
    ///     (CommonConfig). Captures <c>09-settings-saved.png</c>. Verifies
    ///     the on-disk file contains the chosen value so we know Save
    ///     actually wrote something — not just set an in-memory property.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task SettingsDialog_SavePersistsConfig()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Open settings, change Theme to "light", save.
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.IsSettingsOpen = true;
                vm.Settings.Theme = "light";
                _ = vm.Settings.SaveCommand.ExecuteAsync(null);
            }
        });
        await Task.Delay(400).ConfigureAwait(false);
        await Driver.ScreenshotAsync("09-settings-saved").ConfigureAwait(false);

        // Verify ~/.harbor/config.json (CommonConfig) contains "light".
        var configPath = Path.Combine(TempHome, ".harbor", "config.json");
        var configText = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
        await Assert.That(configText).Contains("light");

        // Close the dialog so the next test starts clean.
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.IsSettingsOpen = false;
            }
        });
    }

    /// <summary>
    ///     Pushing a toast through MainViewModel.AddToast makes the toast
    ///     message visible in the bottom-right toast container. Captures
    ///     <c>10-toast-shown.png</c> then waits 5s for auto-dismiss and
    ///     captures <c>11-toast-dismissed.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task ToastNotification_DisplaysAndDismisses()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.AddToast(new ToastNotification("Hello toast", ToastKind.Info));
            }
        });
        await Task.Delay(300).ConfigureAwait(false);
        await Driver.ScreenshotAsync("10-toast-shown").ConfigureAwait(false);

        bool hasToast = await Driver.WaitForTextAsync("Hello toast", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasToast).IsTrue();

        // Auto-dismiss fires after 4s — wait 5s to be safe, then verify
        // the toast text is gone from the visual tree.
        await Task.Delay(5_000).ConfigureAwait(false);
        await Driver.ScreenshotAsync("11-toast-dismissed").ConfigureAwait(false);

        bool stillThere = Driver.GetAllVisibleText().Contains("Hello toast", StringComparison.Ordinal);
        await Assert.That(stillThere).IsFalse();
    }

    /// <summary>
    ///     Command palette opens (Ctrl+P equivalent: vm.IsCommandPaletteOpen=true)
    ///     and shows the "Command palette" header. Captures <c>12-command-palette.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task CommandPalette_OpensWithCtrlP()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.IsCommandPaletteOpen = true;
            }
        });
        await Task.Delay(300).ConfigureAwait(false);
        await Driver.ScreenshotAsync("12-command-palette").ConfigureAwait(false);

        bool hasPalette = await Driver.WaitForTextAsync("Command", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasPalette).IsTrue();

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.IsCommandPaletteOpen = false;
            }
        });
    }

    /// <summary>
    ///     Provider browser modal opens and shows the "Provider browser" header.
    ///     Captures <c>13-provider-browser.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task ProviderBrowser_Opens()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.IsProviderBrowserOpen = true;
            }
        });
        await Task.Delay(300).ConfigureAwait(false);
        await Driver.ScreenshotAsync("13-provider-browser").ConfigureAwait(false);

        bool hasBrowser = await Driver.WaitForTextAsync("Provider browser", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasBrowser).IsTrue();

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.IsProviderBrowserOpen = false;
            }
        });
    }

    /// <summary>
    ///     Sidebar toggle hides and re-shows the left session-list pane.
    ///     Captures <c>14-sidebar-hidden.png</c> + <c>15-sidebar-shown.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Sidebar_ToggleHidesAndShows()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Default: visible. Capture baseline first so a VLM can compare.
        await Driver.ScreenshotAsync("14a-sidebar-default").ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.IsSidebarVisible = false;
            }
        });
        await Task.Delay(200).ConfigureAwait(false);
        await Driver.ScreenshotAsync("14-sidebar-hidden").ConfigureAwait(false);

        bool sidebarGone = !Driver.OnUIThread(() =>
            Driver.MainWindow.DataContext is MainViewModel vm && vm.IsSidebarVisible);
        await Assert.That(sidebarGone).IsTrue();

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.IsSidebarVisible = true;
            }
        });
        await Task.Delay(200).ConfigureAwait(false);
        await Driver.ScreenshotAsync("15-sidebar-shown").ConfigureAwait(false);

        bool sidebarBack = Driver.OnUIThread(() =>
            Driver.MainWindow.DataContext is MainViewModel vm && vm.IsSidebarVisible);
        await Assert.That(sidebarBack).IsTrue();
    }

    /// <summary>
    ///     Theme toggle (Ctrl+Shift+T equivalent) flips between dark and light.
    ///     Captures <c>16-theme-dark.png</c> + <c>17-theme-light.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Theme_ToggleChangesAppearance()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Default = dark. Capture baseline.
        await Driver.ScreenshotAsync("16-theme-dark").ConfigureAwait(false);

        // Toggle to light.
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.ToggleThemeCommand.Execute(null);
            }
        });
        await Task.Delay(400).ConfigureAwait(false);
        await Driver.ScreenshotAsync("17-theme-light").ConfigureAwait(false);

        // Toggle back to dark so the next test starts in the default theme.
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.ToggleThemeCommand.Execute(null);
            }
        });
        await Task.Delay(200).ConfigureAwait(false);
    }

    /// <summary>
    ///     Multiple toasts pushed in rapid succession stack vertically and
    ///     are all visible at once. Captures <c>18-multiple-toasts.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task MultipleToasts_StackVertically()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.AddToast(new ToastNotification("First toast body", ToastKind.Info));
                vm.AddToast(new ToastNotification("Second toast body", ToastKind.Success));
                vm.AddToast(new ToastNotification("Third toast body", ToastKind.Warning));
            }
        });
        await Task.Delay(300).ConfigureAwait(false);
        await Driver.ScreenshotAsync("18-multiple-toasts").ConfigureAwait(false);

        bool hasFirst = await Driver.WaitForTextAsync("First toast body", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        bool hasSecond = await Driver.WaitForTextAsync("Second toast body", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        bool hasThird = await Driver.WaitForTextAsync("Third toast body", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasFirst && hasSecond && hasThird).IsTrue();

        // Wait for auto-dismiss so the next test starts clean.
        await Task.Delay(5_000).ConfigureAwait(false);
    }

    /// <summary>
    ///     When the agent loop is running, ChatViewModel.IsAgentRunning=true
    ///     and StatusMessage="Agent is running…" → the chat area shows the
    ///     agent-running banner. Captures <c>19-streaming-indicator.png</c>
    ///     and <c>20-streaming-done.png</c> after IsAgentRunning flips back.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Chat_ShowStreamingIndicator()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.Chat.IsAgentRunning = true;
                vm.Chat.StatusMessage = "Agent is running…";
            }
        });
        await Task.Delay(400).ConfigureAwait(false);
        await Driver.ScreenshotAsync("19-streaming-indicator").ConfigureAwait(false);

        bool hasIndicator = await Driver.WaitForTextAsync("running", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasIndicator).IsTrue();

        // Stop the agent.
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.Chat.IsAgentRunning = false;
                vm.Chat.StatusMessage = string.Empty;
            }
        });
        await Task.Delay(300).ConfigureAwait(false);
        await Driver.ScreenshotAsync("20-streaming-done").ConfigureAwait(false);

        bool stillRunning = Driver.GetAllVisibleText().Contains("Agent is running", StringComparison.Ordinal);
        await Assert.That(stillRunning).IsFalse();
    }

    /// <summary>
    ///     Status bar shows the expected groups: status (idle), agent label,
    ///     model label, token counts, cost, session count. Captures
    ///     <c>21-status-bar-full.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task StatusBar_ShowsAllGroups()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        await Driver.ScreenshotAsync("21-status-bar-full").ConfigureAwait(false);

        // Status text defaults to "idle" — proves the status group is rendered.
        bool hasIdle = await Driver.WaitForTextAsync("idle", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasIdle).IsTrue();

        // "session" appears in the ActiveSessionCount StringFormat ('{0} session').
        bool hasSession = await Driver.WaitForTextAsync("session", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasSession).IsTrue();
    }
}
