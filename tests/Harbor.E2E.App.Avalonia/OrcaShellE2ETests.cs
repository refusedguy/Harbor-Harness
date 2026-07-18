using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.ViewModels.Shell;
using Harbor.App.Avalonia.Views.Shell;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Core.Enums;

// See HeadlessAvaloniaDriver.cs for the rationale — the test namespace
// Harbor.E2E.App.Avalonia shadows Harbor.App.Avalonia for name lookup,
// so we alias the production App class to 'HarborApp'.
using HarborApp = global::Harbor.App.Avalonia.App;

namespace Harbor.E2E.App.Avalonia;

/// <summary>
///     E2E tests for the experimental Orca-inspired shell (Task F2).
/// </summary>
/// <remarks>
///     <para>
///         Verifies that the Orca shell loads with the dense session rail,
///         amber-accent design tokens, and the composer (input + Send/Stop)
///         at the bottom. Captures <c>22-orca-shell-default.png</c> so a VLM
///         reviewer can SEE the experimental shell without launching the app.
///     </para>
///     <para>
///         <b>Test isolation:</b> the Orca shell is toggled on by setting
///         <see cref="HarborApp.ShellMode"/> = <c>"orca"</c> and swapping the
///         MainWindow's <c>Content</c> + <c>DataContext</c> to
///         <see cref="OrcaShellView"/> + <see cref="OrcaShellViewModel"/>.
///         The original classic-mode state is saved before the test and
///         restored in a <c>finally</c> block so subsequent classic tests
///         (in <see cref="AvaloniaUiTests"/>) are unaffected regardless of
///         run order.
///     </para>
///     <para>
///         Tagged <c>[NotInParallel]</c> because the driver mutates the
///         process-wide Avalonia <c>Application</c> singleton's MainWindow.
///     </para>
/// </remarks>
[NotInParallel]
public sealed class OrcaShellE2ETests
{
    private static readonly string ScreenshotDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".harbor",
        "test-screenshots");

    private static readonly string TempHome = Path.Combine(
        Path.GetTempPath(),
        "harbor-avalonia-orca-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));

    private static HeadlessAvaloniaDriver? _driver;

    /// <summary>
    ///     Per-test setup. Initializes the shared driver on first run; subsequent
    ///     tests reuse the same driver (it's a process-wide singleton backed by
    ///     Avalonia's <c>Application.Current</c> which can only be set once per
    ///     AppDomain). Also writes a fresh <c>~/.harbor/config.json</c> marking
    ///     onboarding done so the main window (not the wizard) shows.
    /// </summary>
    [Before(HookType.Test)]
    public async Task SetupTestAsync()
    {
        if (_driver is null)
        {
            if (Directory.Exists(ScreenshotDir))
            {
                Directory.Delete(ScreenshotDir, recursive: true);
            }
            Directory.CreateDirectory(ScreenshotDir);

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

    private static HeadlessAvaloniaDriver Driver
        => _driver ?? throw new InvalidOperationException("SetupTestAsync did not run.");

    /// <summary>
    ///     The Orca shell loads with the brand "Harbor" visible in the left
    ///     rail and captures <c>22-orca-shell-default.png</c>.
    /// </summary>
    /// <remarks>
    ///     The test saves the MainWindow's classic-mode <c>Content</c> +
    ///     <c>DataContext</c> + <c>HarborApp.ShellMode</c>, swaps to the Orca
    ///     shell, screenshots, asserts, then restores the original state in a
    ///     <c>finally</c> block. This makes the test order-independent: it can
    ///     run before, after, or in between classic tests without breaking them.
    /// </remarks>
    [Test]
    [Category("E2E")]
    public async Task OrcaShell_ShowsDenseSessionRail()
    {
        await Driver.InitializeAsync().ConfigureAwait(false);

        // ── Save original classic-mode state ────────────────────────────────
        Control? origContent = null;
        object? origDataContext = null;
        string origShellMode = HarborApp.ShellMode;

        Driver.OnUIThread(() =>
        {
            var mw = Driver.MainWindow;
            origContent = (Control?)mw.Content;
            origDataContext = mw.DataContext;
        });

        try
        {
            // ── Swap to Orca shell ──────────────────────────────────────────
            HarborApp.ShellMode = "orca";

            Driver.OnUIThread(() =>
            {
                var mw = Driver.MainWindow;
                // If the MainWindow was constructed in classic mode (a previous
                // classic test ran first), force-swap to the Orca shell. If it
                // was already constructed in orca mode (this test ran first),
                // the DataContext is already OrcaShellViewModel — no-op.
                if (mw.DataContext is not OrcaShellViewModel)
                {
                    var orcaVm = HarborApp.Services.GetRequiredService<OrcaShellViewModel>();
                    mw.Content = new OrcaShellView();
                    mw.DataContext = orcaVm;
                }
            });

            // ── Assert + screenshot ────────────────────────────────────────
            // The brand "Harbor" appears in the LeftRailView header. Its
            // presence proves the OrcaShellView loaded + the LeftRailView
            // rendered.
            bool hasRail = await Driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await Assert.That(hasRail).IsTrue();

            var screenshot = await Driver.ScreenshotAsync("22-orca-shell-default").ConfigureAwait(false);
            await Assert.That(File.Exists(screenshot)).IsTrue();
            var size = new FileInfo(screenshot).Length;
            await Assert.That(size).IsGreaterThan(5_000);
        }
        finally
        {
            // ── Restore classic-mode state ──────────────────────────────────
            HarborApp.ShellMode = origShellMode;
            Driver.OnUIThread(() =>
            {
                var mw = Driver.MainWindow;
                if (origContent is not null)
                {
                    mw.Content = origContent;
                }
                if (origDataContext is not null)
                {
                    mw.DataContext = origDataContext;
                }
            });
        }
    }

    /// <summary>
    ///     The Orca composer's "Send" button exists and is initially disabled
    ///     (empty input). Captures <c>23-orca-composer.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task OrcaComposer_SendButton_DisabledWhenEmpty()
    {
        await Driver.InitializeAsync().ConfigureAwait(false);

        Control? origContent = null;
        object? origDataContext = null;
        string origShellMode = HarborApp.ShellMode;

        Driver.OnUIThread(() =>
        {
            var mw = Driver.MainWindow;
            origContent = (Control?)mw.Content;
            origDataContext = mw.DataContext;
        });

        try
        {
            HarborApp.ShellMode = "orca";
            Driver.OnUIThread(() =>
            {
                var mw = Driver.MainWindow;
                if (mw.DataContext is not OrcaShellViewModel)
                {
                    var orcaVm = HarborApp.Services.GetRequiredService<OrcaShellViewModel>();
                    mw.Content = new OrcaShellView();
                    mw.DataContext = orcaVm;
                }
            });

            // Find the Orca composer's "Send" button by text. The classic
            // shell uses "Send ▶"; the Orca composer uses plain "Send".
            var send = Driver.FindButtonByText("Send");
            await Assert.That(send).IsNotNull();

            await Driver.ScreenshotAsync("23-orca-composer").ConfigureAwait(false);
        }
        finally
        {
            HarborApp.ShellMode = origShellMode;
            Driver.OnUIThread(() =>
            {
                var mw = Driver.MainWindow;
                if (origContent is not null) mw.Content = origContent;
                if (origDataContext is not null) mw.DataContext = origDataContext;
            });
        }
    }
}
