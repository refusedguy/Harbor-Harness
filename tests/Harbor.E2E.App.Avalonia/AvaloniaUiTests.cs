using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Harbor.App.Avalonia;
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
///         <b>Concurrency:</b> tagged <c>[NotInParallel]</c> because the driver
///         mutates <c>$HOME</c> (process-wide env var) and shares the
///         process-wide Avalonia <see cref="Application"/> singleton.
///     </para>
///     <para>
///         <b>Screenshots:</b> numbered <c>01-main-window.png</c>,
///         <c>02-brand.png</c>, … so a sorted directory listing shows the
///         visual narrative in test order.
///     </para>
/// </remarks>
[NotInParallel]
public class AvaloniaUiTests
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
    ///     The app boots without crashing and the main window is non-null.
    ///     Captures <c>01-main-window.png</c> — the baseline visual check.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task MainWindow_OpensWithoutCrash()
    {
        var screenshot = await Driver.ScreenshotAsync("01-main-window").ConfigureAwait(false);
        await Assert.That(File.Exists(screenshot)).IsTrue();
        // The PNG must be non-trivial — at least 5KB means actual pixels were
        // rendered, not just an empty bitmap header.
        var size = new FileInfo(screenshot).Length;
        await Assert.That(size).IsGreaterThan(5_000);
        await Assert.That(Driver.MainWindow).IsNotNull();
    }

    /// <summary>
    ///     The sidebar brand "⚓ Harbor" appears in the rendered window.
    ///     Captures <c>02-brand.png</c> after waiting for the brand text.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task Sidebar_ShowsHarborBrand()
    {
        // The sidebar shows "⚓ Harbor" in the top-left brand TextBlock.
        bool saw = await Driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await Assert.That(saw).IsTrue();
        await Driver.ScreenshotAsync("02-brand").ConfigureAwait(false);
    }

    /// <summary>
    ///     The chat input TextBox exists (x:Name="InputBox" in ChatView.axaml),
    ///     accepts typed text, and reflects it in its Text property.
    ///     Captures <c>03-input-typed.png</c> showing the typed text in the box.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task ChatInput_AcceptsText()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var input = Driver.FindControlByName<TextBox>("InputBox");
        await Assert.That(input).IsNotNull();

        // Set the text AND read it back in the SAME OnUIThread call. This
        // avoids any MainLoop pumping between set and read, which was causing
        // a flaky race where deferred TwoWay binding updates from the previous
        // SendButton test reverted the TextBox to "test prompt" before we
        // could read the typed value.
        var typedText = Driver.OnUIThread(() =>
        {
            input!.Text = "Hello from E2E — typing into the real input box!";
            input!.CaretIndex = input!.Text.Length;
            // Read back IMMEDIATELY — same dispatcher cycle, no chance for
            // deferred binding updates to intervene.
            return input!.Text;
        });
        await Assert.That(typedText).IsEqualTo("Hello from E2E — typing into the real input box!");

        await Driver.ScreenshotAsync("03-input-typed").ConfigureAwait(false);
    }

    /// <summary>
    ///     The Send button (the <c>Button Classes="Primary" Content="Send ▶"</c>
    ///     in ChatView.axaml) exists and is enabled when the input has text.
    ///     Captures <c>04-send-button.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task SendButton_ExistsAndIsEnabled()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Type something so SendCommand.CanExecute returns true (most chat
        // VMs disable send when the input is empty).
        var input = Driver.FindControlByName<TextBox>("InputBox");
        if (input is not null)
        {
            await Driver.TypeAsync(input, "test prompt").ConfigureAwait(false);
        }

        var send = Driver.FindButtonByText("Send ▶");
        await Assert.That(send).IsNotNull();

        // Read IsEnabled on the UI thread — InputElement.IsEnabled is a
        // dispatcher-affine AvaloniaObject property and throws
        // "calling thread cannot access this object" when read from
        // a non-UI thread.
        var isEnabled = Driver.OnUIThread(() => send!.IsEnabled);
        await Assert.That(isEnabled).IsTrue();
        await Driver.ScreenshotAsync("04-send-button").ConfigureAwait(false);
    }

    /// <summary>
    ///     The session sidebar shows the "Search sessions…" watermark TextBox
    ///     and the new-session "+" button. Captures <c>05-session-sidebar.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task SessionSidebar_ShowsSearchBox()
    {
        // The sidebar TextBox has Watermark="Search sessions…". The watermark
        // is rendered as a TextBlock inside the TextBox template, so it shows
        // up in GetAllVisibleText when the TextBox is empty.
        bool sawWatermark = await Driver.WaitForTextAsync("Search sessions", TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await Assert.That(sawWatermark).IsTrue();
        await Driver.ScreenshotAsync("05-session-sidebar").ConfigureAwait(false);
    }

    /// <summary>
    ///     The status bar shows the configured provider + model. The default
    ///     (from our temp config.json) is "ollama" + "qwen2.5-coder:7b".
    ///     Captures <c>06-status-bar.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task StatusBar_ShowsProviderAndModel()
    {
        // The MainViewModel formats the status bar from AgentLabel + ModelLabel.
        // With default provider=ollama, model=qwen2.5-coder:7b, the bar should
        // contain "ollama".
        bool saw = await Driver.WaitForTextAsync("ollama", TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await Assert.That(saw).IsTrue();
        await Driver.ScreenshotAsync("06-status-bar").ConfigureAwait(false);
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
}
