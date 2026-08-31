using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Harbor.App.Avalonia;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.Views;
using Microsoft.Extensions.Hosting;
// The test project lives in the Harbor.E2E.App.Avalonia namespace, which
// shadows Harbor.App.Avalonia for unqualified name lookup. Alias the
// production App class to 'HarborApp' (not 'App' — that collides with the
// Harbor.E2E.App NAMESPACE that the C# compiler finds when walking out from
// Harbor.E2E.App.Avalonia, and namespace lookup wins over using-aliases).
using HarborApp = Harbor.App.Avalonia.App;

namespace Harbor.E2E.App.Avalonia;
/// <summary>
///     Real in-process headless driver for the Harbor Avalonia desktop app.
/// </summary>
/// <remarks>
///     <para>
///         Builds the full app — <see cref="App" /> + <see cref="MainWindow" /> +
///         the production DI host (<c>AppHost.BuildAsync</c>) — on a dedicated
///         UI pump thread using <c>Avalonia.Headless</c>'s off-screen software
///         renderer. No real display is required: the window is rendered to an
///         in-memory bitmap that we save as PNG for visual inspection.
///     </para>
///     <para>
///         <b>Why a dedicated UI thread:</b> TUnit executes each test method
///         on a threadpool thread, but Avalonia's <see cref="Application" /> +
///         <see cref="Dispatcher" /> are bound to ONE thread for the lifetime
///         of the process. Binding the dispatcher to whatever threadpool
///         thread runs the first test (the previous design) caused every
///         subsequent test to throw
///         <c>
///             "calling thread cannot access this object because a different
///             thread owns it"
///         </c>
///         when they touched the visual tree.
///     </para>
///     <para>
///         The fix is to start a dedicated background thread that:
///         <list type="number">
///             <item>Binds <see cref="Dispatcher.UIThread" /> to itself.</item>
///             <item>
///                 Enters <see cref="Dispatcher.MainLoop(CancellationToken)" />
///                 so it continuously drains the dispatcher job queue.
///             </item>
///         </list>
///         Test threads then marshal all UI access through
///         <see cref="DispatcherExtensions.InvokeAsync{T}(Dispatcher, Func{T})" />
///         — the job runs on the UI thread, the test thread awaits the result.
///         No manual <c>RunJobs</c> pumping needed because MainLoop is always
///         running on the UI thread.
///     </para>
///     <para>
///         <b>Why this exists:</b> the user can't run the Avalonia app manually
///         inside the build sandbox. By capturing PNGs of every screen the app
///         renders, a human reviewing the artifacts in CI can SEE what the UI
///         looks like and flag visual regressions (broken layout, missing theme,
///         wrong font, etc.) without a manual launch.
///     </para>
///     <para>
///         <b>Process-wide singleton:</b> Avalonia's <see cref="Application.Current" />
///         is a static singleton. Once <c>AppBuilder.Configure&lt;App&gt;()</c>
///         runs, no second <see cref="Application" /> instance can be created in
///         the same AppDomain. The driver guards initialization with a lock so
///         the first test class to use it sets up the app; later instances
///         reuse the same app. Tests must run sequentially within a class —
///         tag the class with <c>[NotInParallel]</c>.
///     </para>
///     <para>
///         <b>HOME isolation:</b> each driver instance points <c>HOME</c> at a
///         fresh temp dir before calling <c>AppHost.BuildAsync</c>, so
///         <c>~/.harbor/</c> is empty. The caller writes a
///         <c>config.json</c> with <c>onboardingCompleted: true</c> first if
///         they want the main window; omit it to exercise the onboarding flow.
///     </para>
/// </remarks>
public sealed class HeadlessAvaloniaDriver : IAsyncDisposable
{
    private static readonly object InitLock = new();
    private static bool _appInitialized;
    private static IClassicDesktopStyleApplicationLifetime? _lifetime;
    private static IHost? _host;

    // ── Dedicated UI pump thread ──────────────────────────────────────────
    // Avalonia's Dispatcher is a thread-affine singleton: once bound to a
    // thread, every UI access from any other thread throws. TUnit runs each
    // test method on a fresh threadpool thread, so we CAN'T let the first
    // test thread become the dispatcher thread (subsequent tests would fail).
    //
    // The dedicated UI thread:
    //   1. Touches Dispatcher.UIThread first, binding the dispatcher to itself.
    //   2. Signals _dispatcherReady so test threads know marshaling is safe.
    //   3. Enters MainLoop(ct) — a blocking call that continuously drains the
    //      dispatcher job queue until the token is cancelled.
    //
    // Test threads marshal UI work via Dispatcher.UIThread.InvokeAsync(...);
    // the job lands in the queue, MainLoop on the UI thread picks it up and
    // runs it, the Task returned by InvokeAsync completes, the test thread
    // resumes. No manual RunJobs pumping anywhere in the driver.
    private static readonly CancellationTokenSource UiCts = new();
    private static readonly TaskCompletionSource DispatcherReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _originalHome;

    private readonly string _screenshotDir;
    private readonly string _tempHome;

    static HeadlessAvaloniaDriver()
    {
        var uiThread = new Thread(UIThreadMain)
        {
            IsBackground = true,
            Name = "HarborAvaloniaUIThread"
        };
        uiThread.Start();
    }

    /// <summary>
    ///     Create a driver that writes screenshots to <paramref name="screenshotDir" />
    ///     and uses <paramref name="tempHome" /> as <c>$HOME</c> for the duration
    ///     of this driver's lifetime.
    /// </summary>
    /// <param name="screenshotDir">Directory to write PNG screenshots into. Created if missing.</param>
    /// <param name="tempHome">Absolute path to use as <c>$HOME</c>. Created if missing.</param>
    public HeadlessAvaloniaDriver(string screenshotDir, string tempHome)
    {
        _screenshotDir = screenshotDir;
        _tempHome = tempHome;
        Directory.CreateDirectory(_screenshotDir);
        Directory.CreateDirectory(_tempHome);
        Directory.CreateDirectory(Path.Combine(_tempHome, ".harbor"));

        // Capture the current HOME so we can restore it on dispose. Switching
        // HOME mid-process is process-wide (env vars aren't thread-local) so
        // tests using this driver MUST be marked [NotInParallel].
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", _tempHome);
    }

    /// <summary>
    ///     The main window exposed by the desktop lifetime. Available after
    ///     <see cref="InitializeAsync" /> has completed.
    /// </summary>
    /// <remarks>
    ///     Accessing this property from a non-UI thread reads
    ///     <see cref="IClassicDesktopStyleApplicationLifetime.MainWindow" />
    ///     which is just a managed field — no AvaloniaObject property access,
    ///     so it's safe from any thread. Code that walks the visual tree
    ///     from <see cref="MainWindow" /> must still marshal to the UI thread
    ///     via <see cref="Dispatcher.UIThread" />.
    /// </remarks>
    public MainWindow MainWindow
    {
        get
        {
            if (_lifetime?.MainWindow is not MainWindow mw)
                throw new InvalidOperationException(
                    "HeadlessAvaloniaDriver.MainWindow called before InitializeAsync() " +
                    "or after DisposeAsync().");
            return mw;
        }
    }

    /// <summary>The underlying DI host (so tests can resolve services if needed).</summary>
    public IHost Host
        => _host ?? throw new InvalidOperationException("Driver not initialized.");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Hide the window so it doesn't keep rendering frames in the background.
        try
        {
            if (_lifetime?.MainWindow is { } mw)
            {
                OnUIThread(() => mw.Hide());
            }
        }
        catch
        {
            // Ignore — best-effort cleanup.
        }

        // Stop the DI host so background services (agent loop, IPC, session
        // store) are torn down cleanly. The Avalonia Application itself is a
        // process-wide singleton and is NOT shut down here — subsequent tests
        // in the same session reuse it.
        if (_host is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _host.StopAsync(cts.Token).ConfigureAwait(false);
                _host.Dispose();
            }
            catch
            {
                // Best-effort — don't fail the test because cleanup threw.
            }
            _host = null;
        }

        // Restore HOME. The next driver instance (e.g. in the next test class)
        // will set its own HOME before calling InitializeAsync.
        if (_originalHome is not null)
        {
            Environment.SetEnvironmentVariable("HOME", _originalHome);
        }
    }

    private static void UIThreadMain()
    {
        try
        {
            // Bind the Avalonia dispatcher to THIS thread BEFORE any other
            // code touches it. If a test thread touched Dispatcher.UIThread
            // first, the dispatcher would bind to the threadpool and the UI
            // thread here couldn't rebind it.
            _ = Dispatcher.UIThread.Thread;
            DispatcherReady.SetResult();

            // Run the main loop until the process exits or the token is
            // cancelled. This continuously drains the dispatcher job queue
            // so InvokeAsync calls from test threads complete promptly.
            Dispatcher.UIThread.MainLoop(UiCts.Token);
        }
        catch (Exception ex)
        {
            DispatcherReady.TrySetException(ex);
        }
    }

    /// <summary>
    ///     Build the Avalonia app + Harbor DI host. Idempotent across instances
    ///     (the underlying Avalonia Application is a process-wide singleton —
    ///     only the first call actually constructs it; later calls reuse it).
    /// </summary>
    public async Task InitializeAsync()
    {
        // Wait for the UI thread to have bound the dispatcher — without this,
        // InvokeAsync below would post to a dispatcher that doesn't exist yet
        // and deadlock waiting for a MainLoop that will never start.
        await DispatcherReady.Task.ConfigureAwait(false);

        // Note on the .GetAwaiter().GetResult() pattern below:
        // Dispatcher.UIThread.InvokeAsync<T>(Func<T>) returns DispatcherOperation<T>,
        // not Task<T>. DispatcherOperation<T> has GetAwaiter() (returns TaskAwaiter)
        // but NOT ConfigureAwait — so `await op.ConfigureAwait(false)` won't compile.
        // Blocking via GetAwaiter().GetResult() is safe because the UI thread's
        // MainLoop is pumping the queue independently; the test thread blocks
        // only until the UI thread finishes the queued job.
        lock (InitLock)
        {
            if (_appInitialized)
            {
                return;
            }

            // Build the Harbor DI host FIRST. App.OnFrameworkInitializationCompleted
            // (which runs during SetupWithLifetime) needs App.Services to be
            // populated so it can resolve MainViewModel + AvaloniaConfig + ThemeService.
            // We mirror the AfterSetup callback from Program.cs.
            //
            // AppHost.BuildAsync is async but uses .ConfigureAwait(false) on every
            // await, so its continuations land on threadpool threads — no
            // SynchronizationContext needed. GetAwaiter().GetResult() blocks the
            // test thread until the host is built; the UI thread is already
            // running its MainLoop in parallel so it doesn't deadlock.
            _host = AppHost.BuildAsync(args: Array.Empty<string>()).GetAwaiter().GetResult();
            HarborApp.Services = _host.Services;
            HarborApp.Host = _host;

            // Build the Avalonia app on the UI thread. AppBuilder.SetupWithLifetime
            // creates the Application instance and installs the platform — both of
            // which require the calling thread to be the dispatcher thread (which
            // the UI thread is, by construction). We block on the result so the
            // _appInitialized flag flip happens-after the Avalonia app is ready.
            //
            // UseHeadlessDrawing=false (the default) tells the headless platform
            // to delegate rendering to Avalonia.Skia, producing a real bitmap we
            // can save. UseSkia() must be called explicitly when
            // UseHeadlessDrawing=false — otherwise the headless platform errors
            // out with "No rendering system configured. Consider calling UseSkia()."
            OnUIThread(() =>
            {
                _lifetime = new ClassicDesktopStyleApplicationLifetime();

                // Force dark theme BEFORE the app initializes so
                // OnFrameworkInitializationCompleted applies it deterministically
                // regardless of the test host's OS theme (often Light/Default).
                HarborApp.ThemeMode = "dark";

                AppBuilder.Configure<HarborApp>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions
                    {
                        UseHeadlessDrawing = false
                    })
                    .UseSkia()
                    .WithInterFont()
                    .SetupWithLifetime(_lifetime);
            });

            _appInitialized = true;
        }

        // Show the main window + let layout/render settle. Show() must run on
        // the UI thread; the dedicated UI thread's MainLoop pumps the queued
        // work automatically — no manual RunJobs needed.
        await ShowMainWindowAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Show the main window (if not already visible) and let the UI thread's
    ///     MainLoop pump layout + binding + first render to completion. Safe to
    ///     call repeatedly.
    /// </summary>
    public async Task ShowMainWindowAsync()
    {
        OnUIThread(() =>
        {
            var window = MainWindow;
            if (!window.IsVisible)
            {
                window.Show();
            }
            // Flush layout + force a render tick so the first frame is rendered
            // and CaptureRenderedFrame returns real pixels (not the empty default).
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        });

        // Give the UI thread's MainLoop a moment to drain layout + render
        // jobs before the test reads back state. 80ms is enough on a
        // developer laptop; bump if screenshots come out blank on slow CI.
        await Task.Delay(80).ConfigureAwait(false);
    }

    /// <summary>
    ///     Take a PNG screenshot of the main window and save it to
    ///     <c>{ScreenshotDir}/{name}.png</c>. Returns the absolute path.
    /// </summary>
    /// <param name="name">Filename without extension. Inline-index for sort order (e.g. <c>01-main-window</c>).</param>
    /// <returns>Absolute path to the saved PNG.</returns>
    public async Task<string> ScreenshotAsync(string name)
    {
        await ShowMainWindowAsync().ConfigureAwait(false);

        // ── Force a fresh render pass ──────────────────────────────────────
        // CaptureRenderedFrame returns the LAST rendered bitmap. The headless
        // render timer doesn't fire automatically (no 60Hz vsync), so we tick
        // it explicitly. ForceRenderTimerTick fires the timer synchronously.
        //
        // NOTE: we deliberately do NOT call Dispatcher.UIThread.RunJobs() here
        // — doing so pumps deferred TwoWay binding updates (VM→TextBox) that
        // can revert text typed by TypeAsync in the same test, causing
        // flaky "Expected 'Hello...' but found 'test prompt'" failures.
        // The UI thread's MainLoop pumps jobs continuously; ForceRenderTimerTick
        // is enough to drive a render pass without disturbing the binding state.
        //
        // BUGFIX (Task S1): ItemsControl container materialization needs
        // multiple layout+render cycles to actually paint chat rows. The
        // previous single UpdateLayout + 3 ticks captured a frame where
        // the TextBlock was in the visual tree (so WaitForTextAsync passed)
        // but not yet painted (so the screenshot was blank). We now interleave
        // UpdateLayout + ForceRenderTimerTick three times — each cycle drains
        // one batch of layout/render work so containers are fully realised
        // before the final capture.
        string path = OnUIThread(() =>
        {
            var window = MainWindow;
            for (int i = 0; i < 3; i++)
            {
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            }

            var bitmap = window.CaptureRenderedFrame();
            if (bitmap is null)
            {
                throw new InvalidOperationException(
                    $"window.CaptureRenderedFrame() returned null for '{name}'. The headless " +
                    "software renderer failed to produce a frame — usually means the " +
                    "window wasn't shown or layout didn't run.");
            }

            string p = Path.Combine(_screenshotDir, $"{name}.png");
            using var fs = File.Create(p);
            bitmap.Save(fs);
            return p;
        });

        return path;
    }

    /// <summary>
    ///     Find a control by <c>x:Name</c> (set in XAML via <c>x:Name="..."</c>).
    ///     Returns null if no control with that name exists in the main window.
    /// </summary>
    /// <remarks>
    ///     Synchronous because tests use it inline (<c>var input = Driver.FindControlByName&lt;TextBox&gt;("InputBox");</c>).
    ///     Blocks the test thread on <see cref="Dispatcher.UIThread.InvokeAsync{T}(Func{T})" />'s
    ///     awaiter — the UI thread's MainLoop executes the lookup and unblocks the caller.
    ///     <para>
    ///         <b>Visual tree walk:</b> <see cref="ControlExtensions.FindControl{T}" /> only
    ///         searches the immediate <c>NameScope</c> of the control it's called on — it
    ///         does NOT recurse into child <c>UserControl</c>s. Since <c>InputBox</c> lives
    ///         inside <c>ChatView.axaml</c> (a <c>UserControl</c> embedded in <c>MainWindow</c>),
    ///         we walk the visual tree depth-first and match by <see cref="StyledElement.Name" />.
    ///     </para>
    /// </remarks>
    public T? FindControlByName<T>(string name) where T : Control
    {
        return OnUIThread<T?>(() => FindByName<T>(MainWindow, name));
    }

    /// <summary>
    ///     Run an arbitrary delegate on the UI thread and return its result.
    ///     Use this from tests that need to read dispatcher-affine properties
    ///     (e.g. <c>Button.IsEnabled</c>, <c>TextBox.Text</c>) so the access
    ///     happens on the UI thread instead of the test thread.
    /// </summary>
    /// <typeparam name="T">The return type.</typeparam>
    /// <param name="func">The delegate to execute on the UI thread.</param>
    /// <returns>The delegate's return value.</returns>
    public T OnUIThread<T>(Func<T> func)
    {
        // Reentrancy guard: when already ON the dispatcher thread (e.g. the
        // ComponentTestBase.Vm helper called inside another UI(() => …) block),
        // InvokeAsync(...).GetResult() would block the UI thread on a queued
        // job that can never run — the MainLoop is busy inside THIS call.
        // Execute inline instead, matching WPF Dispatcher.Invoke semantics.
        if (Dispatcher.UIThread.CheckAccess())
        {
            return func();
        }

        return Dispatcher.UIThread
            .InvokeAsync<T>(func)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    ///     Run an arbitrary void delegate on the UI thread. Use this from
    ///     tests that need to mutate dispatcher-affine state (e.g. invoke a
    ///     view-model command) without returning a value.
    /// </summary>
    /// <param name="action">The delegate to execute on the UI thread.</param>
    public void OnUIThread(Action action)
    {
        // Same reentrancy guard as the generic overload — see above.
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread
            .InvokeAsync(action)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    ///     Find the first <see cref="RadioButton" /> whose
    ///     <see cref="ContentControl.Content" /> stringifies to
    ///     <paramref name="text" /> (case-sensitive, trimmed). Used to locate
    ///     the "💬 Chat" / "📝 Code" tab toggle buttons in the main window's
    ///     tab strip.
    /// </summary>
    public RadioButton? FindRadioButtonByText(string text)
    {
        return OnUIThread<RadioButton?>(() =>
            FindFirst<RadioButton>(MainWindow, b =>
                b.Content is { } c &&
                string.Equals(c.ToString()?.Trim(), text, StringComparison.Ordinal)));
    }

    /// <summary>
    ///     Find the first <see cref="Button" /> whose <see cref="ContentControl.Content" />
    ///     stringifies to <paramref name="text" /> (case-sensitive, trimmed).
    ///     Used to locate the "Send ▶" button which has no x:Name in ChatView.axaml.
    /// </summary>
    public Button? FindButtonByText(string text)
    {
        return OnUIThread<Button?>(() =>
            FindFirst<Button>(MainWindow, b =>
                b.Content is { } c &&
                string.Equals(c.ToString()?.Trim(), text, StringComparison.Ordinal)));
    }

    /// <summary>
    ///     Type text into a <see cref="TextBox" /> by setting its <see cref="TextBox.Text" />
    ///     and advancing the caret. This is the simplest input simulation that
    ///     exercises the binding pipeline (TextBox.Text → ViewModel.InputText).
    /// </summary>
    public async Task TypeAsync(TextBox target, string text)
    {
        OnUIThread(() =>
        {
            target.Text = text;
            target.CaretIndex = text.Length;
            // Explicitly invalidate the TextBox's visual — in headless mode,
            // the text formatter's cached text runs don't always get marked
            // dirty when Text changes, so the next render pass paints the OLD
            // text (or nothing). InvalidateVisual forces the TextBox to be
            // re-painted on the next render tick.
            target.InvalidateVisual();
            // NOTE: do NOT call Dispatcher.UIThread.RunJobs() here — it causes
            // re-entrancy issues with the TwoWay binding and can revert the
            // TextBox to a stale VM value. The MainLoop pumps jobs continuously.
        });

        // Two-way bindings with UpdateSourceTrigger=PropertyChanged fire
        // synchronously, but the ViewModel may schedule async work on the
        // dispatcher — give the UI thread's MainLoop a moment to drain.
        await Task.Delay(20).ConfigureAwait(false);
    }

    /// <summary>
    ///     Click a button by invoking its <see cref="Button.Command" /> if it
    ///     has one; otherwise raise the <see cref="Button.Click" /> routed event.
    ///     Either path triggers the bound view-model action.
    /// </summary>
    /// <remarks>
    ///     For <see cref="RadioButton" /> (which has no Command by default and
    ///     toggles <c>IsChecked</c> via the <c>OnClick</c> virtual rather than
    ///     the routed <c>ClickEvent</c>), we set <c>IsChecked = true</c>
    ///     directly. This is the path of least surprise in headless mode —
    ///     raising the routed Click event doesn't always invoke OnClick in
    ///     the headless renderer, leaving the radio button's state unchanged.
    /// </remarks>
    public async Task ClickAsync(Button button)
    {
        OnUIThread(() =>
        {
            if (button is RadioButton rb)
            {
                // Setting IsChecked=true fires the TwoWay binding that pushes
                // the ConverterParameter back to the source (e.g. ActiveView).
                rb.IsChecked = true;
                return;
            }

            if (button.Command is { } cmd && cmd.CanExecute(null))
            {
                cmd.Execute(null);
            }
            else
            {
                // No command — simulate the routed Click event directly.
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            }
        });

        await Task.Delay(30).ConfigureAwait(false);
    }

    /// <summary>
    ///     Poll the visible visual tree until <paramref name="text" /> appears
    ///     in any TextBlock / TextBox / ContentControl. Returns true if found
    ///     before <paramref name="timeout" /> elapses.
    /// </summary>
    public async Task<bool> WaitForTextAsync(string text, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            string current = GetAllVisibleText();
            if (current.Contains(text, StringComparison.Ordinal))
            {
                return true;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    ///     Poll until <paramref name="text" /> appears in
    ///     <see cref="GetRenderedText" /> — i.e. it is genuinely VISIBLE, not
    ///     merely attached to the visual tree inside a collapsed subtree (C1).
    /// </summary>
    public async Task<bool> WaitForRenderedTextAsync(string text, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            string current = GetRenderedText();
            if (current.Contains(text, StringComparison.Ordinal))
            {
                return true;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    ///     Poll a SEPARATE window's visual tree (not MainWindow) until
    ///     <paramref name="text" /> appears. Needed for tests that open their
    ///     own window (onboarding wizard) — <see cref="WaitForTextAsync" />
    ///     only walks MainWindow.
    /// </summary>
    public async Task<bool> WaitForTextInWindowAsync(
        Window window,
        string text,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            string current = OnUIThread(() =>
            {
                var sb = new StringBuilder();
                AppendText(window, sb);
                return sb.ToString();
            });
            if (current.Contains(text, StringComparison.Ordinal))
            {
                return true;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    ///     Poll a condition until it returns <see langword="true" /> or the
    ///     timeout elapses. Replaces arbitrary <c>Task.Delay</c> calls for UI
    ///     settling with deterministic polling.
    /// </summary>
    /// <param name="condition">
    ///     A delegate evaluated on each poll. Typically reads a view-model
    ///     property or checks the visual tree via <see cref="OnUIThread{T}" />.
    /// </param>
    /// <param name="timeout">Maximum time to wait. Defaults to 5 seconds.</param>
    /// <param name="pollInterval">Time between polls. Defaults to 50 ms.</param>
    /// <returns><see langword="true" /> if the condition was met before the timeout.</returns>
    public async Task<bool> WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(interval).ConfigureAwait(false);
        }
        return condition();
    }

    /// <summary>
    ///     Concatenate every TextBlock.Text, TextBox.Text, and ContentControl.Content
    ///     (when string) visible in the main window's visual tree. Used for
    ///     text-based assertions + <see cref="WaitForTextAsync" /> polling.
    /// </summary>
    /// <remarks>
    ///     Synchronous so it can be called inline from tests and from
    ///     <see cref="WaitForTextAsync" />. Marshals to the UI thread via
    ///     <see cref="Dispatcher.UIThread.InvokeAsync{T}(Func{T})" />.GetResult().
    /// </remarks>
    public string GetAllVisibleText()
    {
        return OnUIThread(() =>
        {
            var sb = new StringBuilder();
            AppendText(MainWindow, sb);
            return sb.ToString();
        });
    }

    /// <summary>
    ///     Reset per-test UI state: clear the chat input box, close any open
    ///     modals (command palette, settings, diff, token-usage, provider browser),
    ///     and switch back to the chat view. Called between tests in the same
    ///     class so each test starts from a known UI state.
    /// </summary>
    public async Task ResetStateAsync()
    {
        OnUIThread(() =>
        {
            // Use the visual-tree-walking FindByName, not FindControl — the
            // InputBox lives inside the ChatView UserControl, which FindControl
            // can't see into. Without this, ResetStateAsync silently no-ops and
            // leftover text from a previous test bleeds into the next one.
            var input = FindByName<TextBox>(MainWindow, "InputBox");
            if (input is not null)
            {
                input.Text = string.Empty;
            }

            // ALSO clear the ViewModel.InputText directly. The TextBox↔VM
            // TwoWay binding is eventually-consistent across dispatcher cycles
            // — clearing just TextBox.Text queues a binding update that may
            // not propagate to the VM before the next TypeAsync runs. If the
            // VM still holds the previous test's "test prompt", a deferred
            // VM→TextBox propagation can revert the TextBox after we type.
            // Clearing both ends eliminates the race.
            if (MainWindow.DataContext is MainViewModel vm)
            {
                vm.Chat.InputText = string.Empty;
                vm.IsCommandPaletteOpen = false;
                vm.IsSettingsOpen = false;
                vm.IsProviderBrowserOpen = false;
                vm.IsDiffOpen = false;
                vm.IsTokenUsageOpen = false;
                // Clear any leftover toasts from previous tests. Without this,
                // a "Settings saved" toast from a prior test bleeds into the
                // next test's screenshot (e.g. 02-input-typed showed a stale
                // toast even though that test never opened Settings).
                vm.Toasts.Clear();
                // Reset the command palette itself. Opening the palette does
                // NOT re-run Refilter unless Query actually CHANGES, so a
                // SelectedIndex left by a previous test (the Enter test sets
                // an explicit =1) survives into the next test's open
                // assertion — an ordering-dependent failure that only shows
                // up under full-suite test scheduling. Setting Query re-filters
                // to the full command list and re-selects row 0 (dispatcher-
                // posted); the direct SelectedIndex=0 covers the already-empty
                // case where OnQueryChanged never fires.
                vm.CommandPalette.Query = string.Empty;
                vm.CommandPalette.SelectedIndex = 0;
                // Clear the chat transcript + reset the UiStore so the next
                // test starts from a clean status (idle, no agent running).
                // Without this, a previous test that triggered the agent loop
                // (e.g. SendMessage_AddsToChatHistory) leaves StatusText="running"
                // for every subsequent test.
                try { vm.Chat.ClearCommand.Execute(null); }
                catch { }
                vm.SwitchViewCommand.Execute("chat");
            }
        });

        await Task.Delay(20).ConfigureAwait(false);
    }

    /// <summary>Depth-first walk of the visual tree, collecting text from text-bearing controls.</summary>
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

    /// <summary>
    ///     Visibility-honest variant of <see cref="GetAllVisibleText" /> (C1):
    ///     a control with <c>IsVisible=false</c> stays ATTACHED to the visual
    ///     tree in Avalonia — it is collapsed, not detached — so the legacy
    ///     unfiltered walk feeds hidden text into assertions. This walk stops
    ///     at every invisible subtree, so a positive match means the text is
    ///     ACTUALLY rendered. Used by tests that assert visible UI states
    ///     (streaming banner, status-bar counter); legacy tests keep using the
    ///     unfiltered probe.
    /// </summary>
    public string GetRenderedText()
    {
        return OnUIThread(() =>
        {
            var sb = new StringBuilder();
            AppendRenderedText(MainWindow, sb, parentVisible: true);
            return sb.ToString();
        });
    }

    private static void AppendRenderedText(Visual visual, StringBuilder sb, bool parentVisible)
    {
        if (!parentVisible || !visual.IsVisible)
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
            AppendRenderedText(child, sb, parentVisible: true);
        }
    }

    /// <summary>Depth-first search for the first control matching <paramref name="predicate" />.</summary>
    private static T? FindFirst<T>(Visual root, Func<T, bool> predicate) where T : Control
    {
        if (root is T match && predicate(match))
        {
            return match;
        }
        foreach (var child in root.GetVisualChildren())
        {
            if (FindFirst(child, predicate) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>
    ///     Depth-first walk of the visual tree looking for a control whose
    ///     <see cref="StyledElement.Name" /> matches <paramref name="name" />.
    ///     Unlike <see cref="ControlExtensions.FindControl{T}" />, this recurses
    ///     into child <c>UserControl</c>s (which have their own NameScope that
    ///     <c>FindControl</c> doesn't traverse).
    /// </summary>
    private static T? FindByName<T>(Visual root, string name) where T : Control
    {
        if (root is T match && string.Equals(match.Name, name, StringComparison.Ordinal))
        {
            return match;
        }
        foreach (var child in root.GetVisualChildren())
        {
            if (FindByName<T>(child, name) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}
