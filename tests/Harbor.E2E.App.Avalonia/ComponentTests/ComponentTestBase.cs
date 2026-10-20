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
///         classes MUST be tagged <c>[NotInParallel]</c>. The project runs as
///         several PROCESSES in CI (see <c>e2e-shards.json</c>): the key keeps
///         the classes of one shard in turn inside that shard's process, and
///         the process boundary is what lets two shards run side by side at
///         all.
///     </para>
/// </remarks>
public abstract class ComponentTestBase
{
    /// <summary>
    ///     Per-run screenshot directory. Wiped on first init so reviewers only
    ///     see the latest run. Distinct from <c>test-screenshots/</c> and
    ///     <c>test-screenshots-comp/</c> used by other suites.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         In a sharded run (CI runs this project as several processes -
    ///         see <c>e2e-shards.json</c>) the root is this process's OWN
    ///         directory, and each class takes a sub-directory of it. That
    ///         matters because the per-class directory is DELETED on init:
    ///         with a shared root, one process's init would remove the frames
    ///         another process had just written. See <see cref="E2eShardPaths" />.
    ///     </para>
    /// </remarks>
    protected static readonly string ScreenshotDir = E2eShardPaths.Screenshots(
        "component-ct",
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".harbor",
            "test-screenshots-comp-ct"));

    /// <summary>Root folder holding one sub-directory per test class.</summary>
    protected static readonly string ScreenshotRoot = ScreenshotDir;

    private static readonly Dictionary<string, HeadlessAvaloniaDriver> _drivers = new();
    private static HeadlessAvaloniaDriver? _current;
    private static string _currentShotDir = ScreenshotDir;
    private static string _currentTempHome = string.Empty;
    private static int _screenshotIndex;

    /// <summary>
    ///     A11 (sprint 5): PER-CLASS driver isolation. Every derived class gets
    ///     its own Avalonia host, DI container, temp HOME and session store —
    ///     previously all classes shared one driver, so Settings writes and
    ///     SessionList seeds leaked across classes making those suites
    ///     order-dependent. Classes must be [NotInParallel]; sequential init
    ///     overwrites Application.Current safely.
    /// </summary>
    /// <param name="ownerKey">Stable short class key (e.g. "Settings").</param>
    protected static async Task<HeadlessAvaloniaDriver> GetDriverAsync(string ownerKey)
    {
        if (_drivers.TryGetValue(ownerKey, out var existing))
        {
            _current = existing;
            _currentShotDir = Path.Combine(ScreenshotRoot, ownerKey);
            return existing;
        }

        string shotDir = Path.Combine(ScreenshotRoot, ownerKey);
        if (Directory.Exists(shotDir))
        {
            Directory.Delete(shotDir, recursive: true);
        }
        Directory.CreateDirectory(shotDir);

        // The shard suffix keeps this class's temp HOME distinct per PROCESS.
        // Inside one process the owner key alone is enough (each class has its
        // own key), but two processes that ran the same class would otherwise
        // share - and delete - one temp HOME.
        string tempHome = Path.Combine(
            Path.GetTempPath(),
            $"harbor-avalonia-ct-{ownerKey.ToLowerInvariant()}{E2eShardPaths.ProcessSuffix}");
        if (Directory.Exists(tempHome))
        {
            Directory.Delete(tempHome, recursive: true);
        }
        Directory.CreateDirectory(tempHome);
        var harborDir = Path.Combine(tempHome, ".harbor");
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

        // A11: pin the process-wide harbor home BEFORE host build so this
        // class's stores read/write ITS config, not the first class's.
        Harbor.Desktop.Abstractions.Configuration.CommonConfig
            .OverrideHarborHomeForTests(harborDir);

        var driver = new HeadlessAvaloniaDriver(shotDir, tempHome);
        await driver.InitializeAsync().ConfigureAwait(false);
        _drivers[ownerKey] = driver;
        _current = driver;
        _currentShotDir = shotDir;
        _currentTempHome = tempHome;
        return driver;
    }

    /// <summary>The CURRENT class's driver (set by GetDriverAsync in [Before]).</summary>
    protected static HeadlessAvaloniaDriver Driver
        => _current ?? throw new InvalidOperationException("GetDriverAsync(key) did not run.");

    /// <summary>The current class's screenshot directory.</summary>
    protected static string CurrentShotDir => _currentShotDir;

    /// <summary>The current class's isolated temp HOME (config.json lives here).</summary>
    protected static string TempHome => _currentTempHome;

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
        var path = Path.Combine(_currentShotDir, fileName);
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
        await Driver.WaitForConditionAsync(() => true, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20))
            .ConfigureAwait(false);
        return path;
    }
}
