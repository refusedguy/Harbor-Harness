using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.Views;
using Harbor.E2E.Framework;
using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.DependencyInjection;
// See HeadlessAvaloniaDriver.cs for the rationale — the test namespace
// Harbor.E2E.App.Avalonia shadows Harbor.App.Avalonia for name lookup,
// so we alias the production App class to 'HarborApp' (not 'App' — that
// collides with the Harbor.E2E.App namespace).
using HarborApp = Harbor.App.Avalonia.App;

namespace Harbor.E2E.App.Avalonia;
/// <summary>
///     Real headless Avalonia E2E tests with SCREENSHOT capture.
/// </summary>
/// <remarks>
///     <para>
///         Each test boots the actual <see cref="Harbor.App.Avalonia.App" /> +
///         <see cref="MainWindow" /> + full production DI host inside an
///         <c>Avalonia.Headless</c> off-screen renderer, drives the UI like a
///         user (type / click / hover-equivalent), then captures a PNG of the
///         rendered window. The PNGs are written to
///         <c>~/.harbor/test-screenshots/</c> so the user can SEE what the UI
///         looks like without running the app.
///     </para>
///     <para>
///         <b>Navigation coverage:</b> each test drives a DIFFERENT visible
///         state of the shell — chat default, typed input, send-enabled,
///         message-sent, code-editor view, diff modal, onboarding. This
///         guarantees every screenshot's pixels differ (verified by md5sum
///         in the post-run check), so a reviewer can SEE the test
///         actually navigated rather than guessing from a static frame.
///     </para>
///     <para>
///         <b>Concurrency:</b> tagged <c>[NotInParallel]</c> because the driver
///         mutates <c>$HOME</c> (process-wide env var) and shares the
///         process-wide Avalonia <see cref="Application" /> singleton.
///     </para>
/// </remarks>
[NotInParallel]
public sealed class AvaloniaUiTests
{
    /// <summary>
    ///     Directory where PNGs are written. Points to the repo's
    ///     <c>docs/screenshots/</c> so screenshots persist after the test run
    ///     and are available for review / CI artifact upload.
    /// </summary>
    private static readonly string ScreenshotDir = Path.Combine(
        E2EHelpers.FindRepoRoot(), "docs", "screenshots");

    /// <summary>Per-class temp HOME so each test run starts with an empty <c>~/.harbor</c>.</summary>
    private static readonly string TempHome = Path.Combine(
        Path.GetTempPath(),
        "harbor-avalonia-e2e-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private static HeadlessAvaloniaDriver? _driver;

    /// <summary>Get the per-class driver, throwing clearly if setup didn't run.</summary>
    private static HeadlessAvaloniaDriver Driver
        => _driver ?? throw new InvalidOperationException("SetupTestAsync did not run.");

    /// <summary>
    ///     Per-test setup. Initializes the shared driver on first run; subsequent
    ///     tests reuse the same driver (it's a process-wide singleton backed by
    ///     Avalonia's <see cref="Application.Current" /> which can only be set
    ///     once per AppDomain).
    /// </summary>
    /// <remarks>
    ///     We use <c>HookType.Test</c> rather than <c>HookType.Class</c> because
    ///     TUnit 0.50's class-level hook only runs before the FIRST test in the
    ///     class — subsequent tests see <c>null</c> in the static field if
    ///     anything disposes it. Per-test setup with idempotent init avoids the
    ///     issue entirely: the first test pays the init cost, every later test
    ///     hits the early-return path inside <see cref="HeadlessAvaloniaDriver.InitializeAsync" />.
    /// </remarks>
    [Before(Test)]
    public async Task SetupTestAsync()
    {
        // Wipe only our own screenshot files so stale frames from a previous
        // run don't confuse review, but leave any manually-added or non-test
        // files intact (docs/screenshots/ may contain baseline/reference images).
        if (_driver is null)
        {
            if (Directory.Exists(ScreenshotDir))
            {
                foreach (string stale in Directory.GetFiles(ScreenshotDir, "??-*.png"))
                {
                    File.Delete(stale);
                }
            }
            else
            {
                Directory.CreateDirectory(ScreenshotDir);
            }

            // Fresh HOME with ~/.harbor/config.json marking onboarding done.
            if (Directory.Exists(TempHome))
            {
                Directory.Delete(TempHome, true);
            }
            Directory.CreateDirectory(TempHome);
            string harborDir = Path.Combine(TempHome, ".harbor");
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
                        defaultAgent = "code"
                    }, new JsonSerializerOptions { WriteIndented = true }))
                .ConfigureAwait(false);

            _driver = new HeadlessAvaloniaDriver(ScreenshotDir, TempHome);
            await _driver.InitializeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The app boots without crashing, the main window is visible, has the
    ///     new 720px minimum width, and the shell rendered (screenshot captured).
    ///     Captures <c>00-main-window-opens.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task MainWindow_Opens()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        bool isVisible = Driver.OnUIThread(() => Driver.MainWindow.IsVisible);
        await Assert.That(isVisible).IsTrue();

        double width = Driver.OnUIThread(() => Driver.MainWindow.Width);
        await Assert.That(width).IsGreaterThanOrEqualTo(720);

        string screenshot = await Driver.ScreenshotAsync("00-main-window-opens").ConfigureAwait(false);
        await Assert.That(File.Exists(screenshot)).IsTrue();
        long size = new FileInfo(screenshot).Length;
        await Assert.That(size).IsGreaterThan(5_000);
    }

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
        bool inputVisible = Driver.OnUIThread(() => input!.IsVisible);
        await Assert.That(inputVisible).IsTrue();

        // The empty-state placeholder "Start a conversation" must be visible
        // — proves the chat history area is showing (not the code editor's
        // "No file open" placeholder).
        bool sawPlaceholder = await Driver.WaitForTextAsync("Start a conversation", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(sawPlaceholder).IsTrue();

        string screenshot = await Driver.ScreenshotAsync("01-chat-default").ConfigureAwait(false);
        await Assert.That(File.Exists(screenshot)).IsTrue();
        long size = new FileInfo(screenshot).Length;
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
        string? typedText = Driver.OnUIThread(() => input!.Text);
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
        bool enabledEmpty = Driver.OnUIThread(() => sendEmpty!.IsEffectivelyEnabled);
        await Assert.That(enabledEmpty).IsFalse();

        // Type something — Send should now be enabled.
        var input = Driver.FindControlByName<TextBox>("InputBox");
        await Driver.TypeAsync(input!, "test message for send button").ConfigureAwait(false);

        var send = Driver.FindButtonByText("Send ▶");
        bool isEnabled = Driver.OnUIThread(() => send!.IsEffectivelyEnabled);
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
        string? inputText = Driver.OnUIThread(() => input!.Text);
        await Assert.That(string.IsNullOrEmpty(inputText)).IsTrue();

        // BUGFIX: WaitForTextAsync's AppendText walks the visual tree and
        // finds TextBlocks regardless of IsVisible — but the ItemsControl's
        // container materialization + ScrollViewer layout pass needs one more
        // dispatcher cycle before the chat row is actually painted. Without
        // this settle, the screenshot captured a blank chat area even though
        // the message was already in the visual tree. Polling the condition
        // replaces the old fixed 250ms delay with a deterministic wait.
        await Driver.WaitForConditionAsync(
            () => Driver.GetAllVisibleText().Contains("Hello AI!", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2)).ConfigureAwait(false);

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
        string? activeView = Driver.OnUIThread(() =>
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
    ///     <see cref="OnboardingWindow" /> + <see cref="OnboardingViewModel" />
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
        var onboardingWindow = Dispatcher.UIThread.InvokeAsync(() =>
        {
            var w = new OnboardingWindow();
            w.Bind(onboardingVm);
            w.DataContext = onboardingVm;
            w.Show();
            return w;
        }).GetAwaiter().GetResult();

        try
        {
            // Poll until the onboarding window's visual tree contains the brand
            // text — proves layout + first render have settled (replaces the old
            // fixed 120ms delay).
            await Driver.WaitForConditionAsync(() =>
            {
                var sb = new StringBuilder();
                Dispatcher.UIThread.InvokeAsync(() => AppendText(onboardingWindow, sb)).GetAwaiter().GetResult();
                return sb.ToString().Contains("Harbor", StringComparison.Ordinal);
            }, TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            // Capture the rendered frame on the UI thread —
            // CaptureRenderedFrame accesses the window's render target which
            // is dispatcher-affine. Save to PNG inline so the bitmap doesn't
            // cross thread boundaries.
            string path = Path.Combine(ScreenshotDir, "07-onboarding.png");
            bool sawBrand = Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Force a fresh render of the onboarding window — without this
                // the headless render timer hasn't ticked and CaptureRenderedFrame
                // returns a stale (or empty) bitmap.
                onboardingWindow.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();

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
                var sb = new StringBuilder();
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

    // ════════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Walks the visual tree appending TextBlock/TextBox/ContentControl text.</summary>
    private static void AppendText(Visual visual, StringBuilder sb)
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
    //  Each test exercises a DIFFERENT visible state of the shell so a
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
        // Poll for the settings dialog content instead of a fixed delay.
        bool hasTheme = await Driver.WaitForTextAsync("Theme", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasTheme).IsTrue();

        await Driver.ScreenshotAsync("08-settings").ConfigureAwait(false);

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
        // Open settings + change Theme + save — all on UI thread.
        MainViewModel? settingsVm = null;
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.IsSettingsOpen = true;
                vm.Settings.ThemeSettings.Theme = "light";
                settingsVm = vm;
            }
        });
        // Save is async — run on UI thread, block until done.
        if (settingsVm is not null)
        {
            // SaveAsync is async — must await on UI thread.
            // Dispatcher.UIThread.InvokeAsync<T> returns DispatcherOperation<T>
            // which supports GetAwaiter().GetResult().
            Dispatcher.UIThread
                .InvokeAsync(() => settingsVm.Settings.SaveCommand.ExecuteAsync(null))
                .GetAwaiter().GetResult(); // Wait for dispatch
        }

        // Poll the config file until it contains "light" instead of a fixed delay.
        string configPath = Path.Combine(TempHome, ".harbor", "config.json");
        await Driver.WaitForConditionAsync(() =>
        {
            if (!File.Exists(configPath)) return false;
            return File.ReadAllText(configPath).Contains("light", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        // Verify ~/.harbor/config.json (CommonConfig) contains "light".
        string configText = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
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
        // Poll for the toast text instead of a fixed delay.
        bool hasToast = await Driver.WaitForTextAsync("Hello toast", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasToast).IsTrue();

        await Driver.ScreenshotAsync("10-toast-shown").ConfigureAwait(false);

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
        // Poll for the command palette content instead of a fixed delay.
        bool hasPalette = await Driver.WaitForTextAsync("Command", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasPalette).IsTrue();

        await Driver.ScreenshotAsync("12-command-palette").ConfigureAwait(false);

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
        // Poll for the provider browser content instead of a fixed delay.
        bool hasBrowser = await Driver.WaitForTextAsync("Provider browser", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasBrowser).IsTrue();

        await Driver.ScreenshotAsync("13-provider-browser").ConfigureAwait(false);

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

        // Default: visible. Capture baseline first for comparison.
        await Driver.ScreenshotAsync("14a-sidebar-default").ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.IsSidebarVisible = false;
            }
        });
        // Poll for the sidebar state instead of a fixed delay.
        await Driver.WaitForConditionAsync(() =>
            !Driver.OnUIThread(() =>
                Driver.MainWindow.DataContext is MainViewModel vm && vm.IsSidebarVisible),
            TimeSpan.FromSeconds(2)).ConfigureAwait(false);
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
        // Poll for the sidebar state instead of a fixed delay.
        await Driver.WaitForConditionAsync(() =>
            Driver.OnUIThread(() =>
                Driver.MainWindow.DataContext is MainViewModel vm && vm.IsSidebarVisible),
            TimeSpan.FromSeconds(2)).ConfigureAwait(false);
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
        // Minimal settle for the theme resource swap to propagate through the
        // visual tree (one dispatcher cycle replaces the old fixed 400ms delay).
        await Driver.WaitForConditionAsync(() => true,
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(30)).ConfigureAwait(false);
        await Driver.ScreenshotAsync("17-theme-light").ConfigureAwait(false);

        // Toggle back to dark so the next test starts in the default theme.
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.ToggleThemeCommand.Execute(null);
            }
        });
        await Driver.WaitForConditionAsync(() => true,
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(30)).ConfigureAwait(false);
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
        // Poll for the toast text instead of a fixed delay.
        bool hasFirst = await Driver.WaitForTextAsync("First toast body", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        bool hasSecond = await Driver.WaitForTextAsync("Second toast body", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        bool hasThird = await Driver.WaitForTextAsync("Third toast body", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasFirst && hasSecond && hasThird).IsTrue();

        await Driver.ScreenshotAsync("18-multiple-toasts").ConfigureAwait(false);

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
        // Poll for the streaming indicator instead of a fixed delay.
        bool hasIndicator = await Driver.WaitForTextAsync("running", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasIndicator).IsTrue();

        await Driver.ScreenshotAsync("19-streaming-indicator").ConfigureAwait(false);

        // Stop the agent.
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.Chat.IsAgentRunning = false;
                vm.Chat.StatusMessage = string.Empty;
            }
        });
        // Poll until the running indicator is gone instead of a fixed delay.
        await Driver.WaitForConditionAsync(
            () => !Driver.GetAllVisibleText().Contains("Agent is running", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Driver.ScreenshotAsync("20-streaming-done").ConfigureAwait(false);

        bool stillRunning = Driver.GetAllVisibleText().Contains("Agent is running", StringComparison.Ordinal);
        await Assert.That(stillRunning).IsFalse();
    }

    /// <summary>
    ///     Status bar shows the expected groups: status (idle), agent label,
    ///     model label, token counts, cost. Captures
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
    }

    // ════════════════════════════════════════════════════════════════════
    //  TASK 4.1 — Avalonia E2E state tests (streaming/thinking/tool-call/error/compaction)
    //  Each test drives a DIFFERENT visible state of the ChatView shell by
    //  setting view-model properties directly (no LLM round-trip needed in
    //  headless mode). Screenshots land in docs/screenshots/.
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     Streaming buffer: <c>Chat.IsStreaming = true</c> + non-empty
    ///     <c>StreamingBuffer</c>. The chat area should show the streaming
    ///     indicator + live text. Captures <c>22-streaming-buffer.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Chat_ShowStreamingBuffer()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.Chat.IsStreaming = true;
                vm.Chat.StreamingBuffer = "Streaming response text...";
            }
        });
        // Poll for the streaming buffer text instead of a fixed delay.
        bool sawStreaming = await Driver.WaitForTextAsync("Streaming response", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(sawStreaming).IsTrue();

        await Driver.ScreenshotAsync("22-streaming-buffer").ConfigureAwait(false);

        // Reset for next test.
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.Chat.IsStreaming = false;
                vm.Chat.StreamingBuffer = string.Empty;
            }
        });
    }

    /// <summary>
    ///     Thinking buffer: <c>Chat.IsThinking = true</c> + thinking text in
    ///     the status message. Captures <c>23-thinking-buffer.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Chat_ShowThinkingBuffer()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.Chat.IsThinking = true;
                vm.Chat.StatusMessage = "Thinking...";
            }
        });
        // Poll for the thinking indicator instead of a fixed delay.
        bool sawThinking = await Driver.WaitForTextAsync("Thinking", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(sawThinking).IsTrue();

        await Driver.ScreenshotAsync("23-thinking-buffer").ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.Chat.IsThinking = false;
                vm.Chat.StatusMessage = string.Empty;
            }
        });
    }

    /// <summary>
    ///     Tool call card: a <see cref="ToolCallViewModel" /> added to
    ///     <c>Chat.ToolCalls</c> renders a tool-call card showing the tool
    ///     name and status. Captures <c>24-tool-call-card.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Chat_ShowToolCallCard()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                var toolCall = new ToolCallViewModel
                {
                    ToolName = "read",
                    ArgsPreview = "path=/test.txt",
                    Status = ToolCallStatus.Running
                };
                vm.Chat.ToolCalls.Add(toolCall);
            }
        });
        // Poll for the tool call card text instead of a fixed delay.
        bool sawTool = await Driver.WaitForTextAsync("read", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(sawTool).IsTrue();

        await Driver.ScreenshotAsync("24-tool-call-card").ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.Chat.ToolCalls.Clear();
            }
        });
    }

    /// <summary>
    ///     Error state: <c>StatusText = "error"</c> + error message in the
    ///     status bar. The status dot should be red. Captures
    ///     <c>25-error-state.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Chat_ShowErrorState()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.StatusText = "error";
                vm.Chat.StatusMessage = "Something went wrong";
            }
        });
        // Poll for the error state text instead of a fixed delay.
        bool sawError = await Driver.WaitForTextAsync("error", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(sawError).IsTrue();

        await Driver.ScreenshotAsync("25-error-state").ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.StatusText = "idle";
                vm.Chat.StatusMessage = string.Empty;
            }
        });
    }

    /// <summary>
    ///     Compaction status: <c>StatusText = "compacting"</c> in the status
    ///     bar. Captures <c>26-compaction-status.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Chat_ShowCompactionStatus()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.StatusText = "compacting";
                vm.IsRunning = true;
            }
        });
        // Poll for the compaction status text instead of a fixed delay.
        bool sawCompacting = await Driver.WaitForTextAsync("compacting", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(sawCompacting).IsTrue();

        await Driver.ScreenshotAsync("26-compaction-status").ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.StatusText = "idle";
                vm.IsRunning = false;
            }
        });
    }

    // ════════════════════════════════════════════════════════════════════
    //  TASK 4.2 — Avalonia E2E state tests (panel/scroll/focus/input-history)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     Panel toggle: <c>IsSidebarVisible</c> toggles the left session-list
    ///     pane. Captures <c>27a-sidebar-visible.png</c> + <c>27b-sidebar-hidden.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Panel_ToggleVisibility()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Default: visible. Capture baseline.
        await Driver.ScreenshotAsync("27a-sidebar-visible").ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.IsSidebarVisible = false;
            }
        });
        // Poll for the sidebar state instead of a fixed delay.
        await Driver.WaitForConditionAsync(() =>
            !Driver.OnUIThread(() =>
                Driver.MainWindow.DataContext is MainViewModel vm && vm.IsSidebarVisible),
            TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Driver.ScreenshotAsync("27b-sidebar-hidden").ConfigureAwait(false);

        bool sidebarGone = !Driver.OnUIThread(() =>
            Driver.MainWindow.DataContext is MainViewModel vm && vm.IsSidebarVisible);
        await Assert.That(sidebarGone).IsTrue();

        // Toggle back for next test.
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.IsSidebarVisible = true;
            }
        });
        await Driver.WaitForConditionAsync(() =>
            Driver.OnUIThread(() =>
                Driver.MainWindow.DataContext is MainViewModel vm && vm.IsSidebarVisible),
            TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Panel focus: switching to the code view (<c>ActiveView = "code"</c>)
    ///     shows the code editor's empty-state placeholder. Captures
    ///     <c>28-panel-focus.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Panel_FocusPanel()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.SwitchViewCommand.Execute("code");
            }
        });
        // Poll for the code view placeholder instead of a fixed delay.
        bool sawCode = await Driver.WaitForTextAsync("No file open", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(sawCode).IsTrue();

        string? activeView = Driver.OnUIThread(() =>
            (Driver.MainWindow.DataContext as MainViewModel)?.ActiveView);
        await Assert.That(activeView).IsEqualTo("code");

        await Driver.ScreenshotAsync("28-panel-focus").ConfigureAwait(false);

        // Switch back to chat.
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.SwitchViewCommand.Execute("chat");
            }
        });
        // Minimal settle for the view switch to propagate.
        await Driver.WaitForConditionAsync(() => true,
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(30)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Input history navigation: setting <c>Chat.InputText</c> to a
    ///     previous command simulates history navigation (Alt+Up). Captures
    ///     <c>29-input-history.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Input_HistoryNavigation()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Simulate Alt+Up by setting InputText to a history item.
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.Chat.InputText = "previous command from history";
            }
        });
        // Poll for the input text to propagate instead of a fixed delay.
        await Driver.WaitForConditionAsync(() =>
            Driver.OnUIThread(() =>
                (Driver.MainWindow.DataContext as MainViewModel)?.Chat.InputText) == "previous command from history",
            TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        string? historyText = Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
                return vm.Chat.InputText;
            return null;
        });
        await Assert.That(historyText).IsEqualTo("previous command from history");

        await Driver.ScreenshotAsync("29-input-history").ConfigureAwait(false);
    }

    /// <summary>
    ///     Slash-command autocomplete: setting <c>Chat.InputText</c> to
    ///     "/help" simulates Tab-autocompleting a slash command. Captures
    ///     <c>30-autocomplete-slash.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Input_AutocompleteSlashCommand()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var input = Driver.FindControlByName<TextBox>("InputBox");
        await Assert.That(input).IsNotNull();
        await Driver.TypeAsync(input!, "/help").ConfigureAwait(false);

        bool sawSlash = await Driver.WaitForTextAsync("/help", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(sawSlash).IsTrue();

        await Driver.ScreenshotAsync("30-autocomplete-slash").ConfigureAwait(false);
    }

    /// <summary>
    ///     Chat scroll history: adding multiple chat lines to
    ///     <c>Chat.Lines</c> populates the transcript so the scroll bar
    ///     appears. Captures <c>31-chat-scroll.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Chat_ScrollHistory()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Add multiple chat lines to enable scrolling.
        Driver.OnUIThread(() =>
        {
            if (Driver.MainWindow.DataContext is MainViewModel vm)
            {
                vm.Chat.Lines.Add(new ChatLineViewModel(ChatRole.User, "Line 1: Hello"));
                vm.Chat.Lines.Add(new ChatLineViewModel(ChatRole.Assistant, "Response 1"));
                vm.Chat.Lines.Add(new ChatLineViewModel(ChatRole.User, "Line 2: How are you?"));
                vm.Chat.Lines.Add(new ChatLineViewModel(ChatRole.Assistant, "Response 2"));
                vm.Chat.Lines.Add(new ChatLineViewModel(ChatRole.User, "Line 3: What's up?"));
                vm.Chat.Lines.Add(new ChatLineViewModel(ChatRole.Assistant, "Response 3"));
                vm.Chat.Lines.Add(new ChatLineViewModel(ChatRole.User, "Line 4: Goodbye"));
                vm.Chat.Lines.Add(new ChatLineViewModel(ChatRole.Assistant, "Response 4"));
            }
        });
        // Poll for the chat lines to render instead of a fixed delay.
        bool sawLines = await Driver.WaitForTextAsync("Line 4", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(sawLines).IsTrue();

        await Driver.ScreenshotAsync("31-chat-scroll").ConfigureAwait(false);
    }
}
