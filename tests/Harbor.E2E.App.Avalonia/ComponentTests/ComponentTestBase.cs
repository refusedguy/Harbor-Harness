using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.Views;

// The test project namespace Harbor.E2E.App.Avalonia shadows the production
// Harbor.App.Avalonia namespace, so we alias the production App class.
using HarborApp = global::Harbor.App.Avalonia.App;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Shared infrastructure for component-level E2E tests that live under
///     <c>ComponentTests/</c>.
/// </summary>
/// <remarks>
///     <para>
///         Each derived test class drives a single component (ChatView,
///         SessionList, Settings, Onboarding, CommandPalette, Toasts, StatusBar)
///         through 5+ states and captures a screenshot per state with the
///         <c>ct-</c> prefix (component-tests, distinct from the existing
///         <c>c-</c> prefix used by <c>ComponentStateE2ETests</c>).
///     </para>
///     <para>
///         <b>Screenshot directory:</b> <c>~/.harbor/test-screenshots-comp-ct/</c>
///         — separate from every other suite so a parallel run doesn't wipe it.
///     </para>
///     <para>
///         <b>Concurrency:</b> the driver mutates <c>$HOME</c> and shares the
///         process-wide Avalonia <c>Application</c> singleton, so derived
///         classes MUST be tagged <c>[NotInParallel]</c>.
///     </para>
/// </remarks>
public abstract class ComponentTestBase
{
    /// <summary>
    ///     Per-run screenshot directory. Wiped on first init so reviewers only
    ///     see the latest run. Distinct from <c>test-screenshots/</c> and
    ///     <c>test-screenshots-comp/</c> used by other suites.
    /// </summary>
    protected static readonly string ScreenshotDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".harbor",
        "test-screenshots-comp-ct");

    /// <summary>Per-class temp HOME so each test run starts with an empty <c>~/.harbor</c>.</summary>
    protected static readonly string TempHome = Path.Combine(
        Path.GetTempPath(),
        "harbor-avalonia-ct-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));

    private static HeadlessAvaloniaDriver? _driver;
    private static int _screenshotIndex;

    /// <summary>
    ///     Lazy-init the shared headless driver. The first derived class to
    ///     call this pays the init cost (Avalonia app + DI host build); every
    ///     later test reuses the singleton.
    /// </summary>
    protected static async Task<HeadlessAvaloniaDriver> GetDriverAsync()
    {
        if (_driver is not null)
        {
            return _driver;
        }

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
        return _driver;
    }

    /// <summary>Get the initialised driver, throwing clearly if setup didn't run.</summary>
    protected static HeadlessAvaloniaDriver Driver
        => _driver ?? throw new InvalidOperationException("GetDriverAsync() did not run.");

    /// <summary>The main Avalonia window from the driver.</summary>
    protected static Window MainWindow => Driver.MainWindow;

    /// <summary>Run an arbitrary delegate on the UI thread.</summary>
    protected static void UI(Action action) => Driver.OnUIThread(action);

    /// <summary>Run an arbitrary delegate on the UI thread and return its result.</summary>
    protected static T UI<T>(Func<T> fn) => Driver.OnUIThread(fn);

    /// <summary>Get the bound MainViewModel from the main window's DataContext.</summary>
    internal static MainViewModel Vm => UI(() =>
        (Driver.MainWindow.DataContext as MainViewModel)
        ?? throw new InvalidOperationException("MainViewModel not bound."));

    /// <summary>
    ///     Capture a screenshot with a sequential <c>ct-</c> prefix name.
    ///     Returns the absolute path to the saved PNG.
    /// </summary>
    /// <param name="logicalName">Logical name (no extension, no prefix). e.g. <c>chat-empty</c>.</param>
    /// <returns>Absolute path to the saved PNG.</returns>
    protected static async Task<string> CaptureAsync(string logicalName)
    {
        var idx = Interlocked.Increment(ref _screenshotIndex);
        var fileName = $"ct-{idx:00}-{logicalName}";
        var path = await Driver.ScreenshotAsync(fileName).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    ///     Capture a screenshot of the standalone OnboardingWindow (which is
    ///     a separate Avalonia <see cref="Window"/>, not a child of MainWindow).
    /// </summary>
    /// <param name="window">The onboarding window to capture.</param>
    /// <param name="logicalName">Logical name (no extension, no prefix).</param>
    /// <returns>Absolute path to the saved PNG.</returns>
    protected static async Task<string> CaptureOnboardingWindowAsync(Window window, string logicalName)
    {
        var idx = Interlocked.Increment(ref _screenshotIndex);
        var fileName = $"ct-{idx:00}-{logicalName}.png";
        var path = Path.Combine(ScreenshotDir, fileName);
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            var bitmap = window.CaptureRenderedFrame();
            if (bitmap is not null)
            {
                using var fs = File.Create(path);
                bitmap.Save(fs);
            }
        }).GetAwaiter().GetResult();
        await Task.Delay(50).ConfigureAwait(false);
        return path;
    }
}
