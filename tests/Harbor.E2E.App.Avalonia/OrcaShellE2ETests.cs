using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Harbor.App.Avalonia.ViewModels.Shell;
using Harbor.App.Avalonia.Views.Shell;
using Microsoft.Extensions.DependencyInjection;
// See HeadlessAvaloniaDriver.cs for the rationale — the test namespace
// Harbor.E2E.App.Avalonia shadows Harbor.App.Avalonia for name lookup,
// so we alias the production App class to 'HarborApp'.
using HarborApp = Harbor.App.Avalonia.App;

namespace Harbor.E2E.App.Avalonia;
/// <summary>
///     E2E tests for the experimental Orca-inspired shell (Task S2).
/// </summary>
/// <remarks>
///     <para>
///         Verifies that the Orca shell ACTUALLY WORKS — loads the HarborDark
///         theme (amber accent + neutral black), shows the dense session rail,
///         composer, status bar, and that every interactive feature (chat input,
///         send, new session, settings, command palette, theme) keeps working
///         under the Orca shell root.
///     </para>
///     <para>
///         <b>Test isolation:</b> the Orca shell is toggled on by setting
///         <see cref="HarborApp.ShellMode" /> = <c>"orca"</c> and swapping the
///         MainWindow's <c>Content</c> + <c>DataContext</c> to
///         <see cref="OrcaShellView" /> + <see cref="OrcaShellViewModel" />.
///         The original classic-mode state is saved before each test and
///         restored in a <c>finally</c> block so subsequent classic tests
///         (in <see cref="AvaloniaUiTests" />) are unaffected regardless of
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
        "harbor-avalonia-orca-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private static HeadlessAvaloniaDriver? _driver;

    private static HeadlessAvaloniaDriver Driver
        => _driver ?? throw new InvalidOperationException("SetupTestAsync did not run.");

    /// <summary>
    ///     Per-test setup. Initializes the shared driver on first run; subsequent
    ///     tests reuse the same driver (it's a process-wide singleton backed by
    ///     Avalonia's <c>Application.Current</c> which can only be set once per
    ///     AppDomain). Also writes a fresh <c>~/.harbor/config.json</c> marking
    ///     onboarding done so the main window (not the wizard) shows.
    /// </summary>
    [Before(Test)]
    public async Task SetupTestAsync()
    {
        if (_driver is null)
        {
            // NOTE: deliberately do NOT wipe the screenshot dir here — the
            // classic AvaloniaUiTests class also writes into the same dir, and
            // wiping would erase its screenshots if the Orca tests run second
            // (or vice versa). The dir is created if missing.
            Directory.CreateDirectory(ScreenshotDir);

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
    ///     Swap the MainWindow to the Orca shell. Captures the original classic
    ///     state so <see cref="RestoreClassic" /> can put it back. No-op if the
    ///     window is already in Orca mode (e.g. a previous test in this class
    ///     already swapped).
    /// </summary>
    /// <returns>A tuple of (originalContent, originalDataContext, originalShellMode).</returns>
    private (Control? Content, object? DataContext, string ShellMode) SwapToOrca()
    {
        Control? origContent = null;
        object? origDataContext = null;
        string origShellMode = HarborApp.ShellMode;

        Driver.OnUIThread(() =>
        {
            var mw = Driver.MainWindow;
            origContent = (Control?)mw.Content;
            origDataContext = mw.DataContext;
        });

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

        return (origContent, origDataContext, origShellMode);
    }

    /// <summary>Restore the classic shell state captured by <see cref="SwapToOrca" />.</summary>
    /// <param name="state">The tuple returned by <see cref="SwapToOrca" />.</param>
    private void RestoreClassic((Control? Content, object? DataContext, string ShellMode) state)
    {
        HarborApp.ShellMode = state.ShellMode;
        Driver.OnUIThread(() =>
        {
            var mw = Driver.MainWindow;
            if (state.Content is not null) mw.Content = state.Content;
            if (state.DataContext is not null) mw.DataContext = state.DataContext;
        });
    }

    // ════════════════════════════════════════════════════════════════════
    // Test 1: Orca shell loads
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     The Orca shell loads with the brand "Harbor" visible in the left
    ///     rail and captures <c>orca-01-default.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task OrcaShell_Loads()
    {
        await Driver.InitializeAsync().ConfigureAwait(false);
        var state = SwapToOrca();
        try
        {
            bool hasHarbor = await Driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await Assert.That(hasHarbor).IsTrue();

            string screenshot = await Driver.ScreenshotAsync("orca-01-default").ConfigureAwait(false);
            await Assert.That(File.Exists(screenshot)).IsTrue();
            long size = new FileInfo(screenshot).Length;
            await Assert.That(size).IsGreaterThan(5_000);
        }
        finally
        {
            RestoreClassic(state);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Test 2: Left rail visible
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     The Orca left rail is visible with the "Search sessions…" placeholder.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task OrcaShell_LeftRailVisible()
    {
        await Driver.InitializeAsync().ConfigureAwait(false);
        var state = SwapToOrca();
        try
        {
            bool hasSearch = await Driver.WaitForTextAsync("Search sessions", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await Assert.That(hasSearch).IsTrue();

            await Driver.ScreenshotAsync("orca-02-left-rail").ConfigureAwait(false);
        }
        finally
        {
            RestoreClassic(state);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Test 3: Composer visible
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     The Orca composer (input box + Send button) is visible at the bottom
    ///     of the main area. The placeholder text "Message Harbor…" must appear.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task OrcaShell_ComposerVisible()
    {
        await Driver.InitializeAsync().ConfigureAwait(false);
        var state = SwapToOrca();
        try
        {
            bool hasPlaceholder = await Driver.WaitForTextAsync("Message Harbor", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await Assert.That(hasPlaceholder).IsTrue();

            // Helper line below the input.
            bool hasHelper = await Driver.WaitForTextAsync("Enter send", TimeSpan.FromSeconds(3))
                .ConfigureAwait(false);
            await Assert.That(hasHelper).IsTrue();

            var send = Driver.FindButtonByText("Send");
            await Assert.That(send).IsNotNull();

            await Driver.ScreenshotAsync("orca-03-composer").ConfigureAwait(false);
        }
        finally
        {
            RestoreClassic(state);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Test 4: Status bar visible
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     The Orca status bar is visible with the idle status text and
    ///     the model label. Captures <c>orca-04-status-bar.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task OrcaShell_StatusBarVisible()
    {
        await Driver.InitializeAsync().ConfigureAwait(false);
        var state = SwapToOrca();
        try
        {
            // The status bar shows the idle status text. Either "idle" or
            // "running" should appear; on a fresh app the agent isn't running.
            bool hasIdle = await Driver.WaitForTextAsync("idle", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await Assert.That(hasIdle).IsTrue();

            // Session count group should always be present.
            bool hasSession = await Driver.WaitForTextAsync("session", TimeSpan.FromSeconds(3))
                .ConfigureAwait(false);
            await Assert.That(hasSession).IsTrue();

            await Driver.ScreenshotAsync("orca-04-status-bar").ConfigureAwait(false);
        }
        finally
        {
            RestoreClassic(state);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Test 5: Chat input works
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     Typing into the Orca composer's input box updates its Text property
    ///     and the bound ChatViewModel.InputText. Captures <c>orca-05-input-typed.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task OrcaShell_ChatInputWorks()
    {
        await Driver.InitializeAsync().ConfigureAwait(false);
        var state = SwapToOrca();
        try
        {
            // Wait for the composer to load + auto-focus the input.
            await Driver.WaitForTextAsync("Message Harbor", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            var input = Driver.FindControlByName<TextBox>("InputBox");
            await Assert.That(input).IsNotNull();

            await Driver.TypeAsync(input!, "Hello from Orca shell!").ConfigureAwait(false);

            string? typedText = Driver.OnUIThread(() => input!.Text);
            await Assert.That(typedText).IsEqualTo("Hello from Orca shell!");

            await Driver.ScreenshotAsync("orca-05-input-typed").ConfigureAwait(false);
        }
        finally
        {
            RestoreClassic(state);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Test 6: Send message works
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     Clicking the Orca composer's Send button adds the user message to
    ///     the chat history. Captures <c>orca-06-message-sent.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task OrcaShell_SendMessageWorks()
    {
        await Driver.InitializeAsync().ConfigureAwait(false);
        var state = SwapToOrca();
        try
        {
            await Driver.WaitForTextAsync("Message Harbor", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            var input = Driver.FindControlByName<TextBox>("InputBox");
            await Assert.That(input).IsNotNull();
            await Driver.TypeAsync(input!, "Orca send test — hello!").ConfigureAwait(false);

            var send = Driver.FindButtonByText("Send");
            await Assert.That(send).IsNotNull();
            await Driver.ClickAsync(send!).ConfigureAwait(false);

            // After Send, the typed text appears in the chat transcript.
            bool sawMessage = await Driver.WaitForTextAsync("Orca send test", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await Assert.That(sawMessage).IsTrue();

            await Driver.ScreenshotAsync("orca-06-message-sent").ConfigureAwait(false);
        }
        finally
        {
            RestoreClassic(state);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Test 7: New session works
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     Clicking the "+" new-session button in the left rail header creates
    ///     a new session. The rail's ListBox item count must increase.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task OrcaShell_NewSessionWorks()
    {
        await Driver.InitializeAsync().ConfigureAwait(false);
        var state = SwapToOrca();
        try
        {
            await Driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            // Snapshot the session count before clicking +.
            int before = Driver.OnUIThread(() =>
            {
                var orcaVm = Driver.MainWindow.DataContext as OrcaShellViewModel;
                return orcaVm?.Sessions.FilteredSessions.Count ?? -1;
            });

            // Click the "+" button in the rail header (the only button with
            // Content="+").
            var plus = Driver.FindButtonByText("+");
            await Assert.That(plus).IsNotNull();
            await Driver.ClickAsync(plus!).ConfigureAwait(false);

            // Give the SessionManager.NewSessionAsync continuation a moment to
            // run + the CollectionChanged → ReprojectAll → FilteredSessions.Add
            // chain to land.
            await Task.Delay(200).ConfigureAwait(false);

            int after = Driver.OnUIThread(() =>
            {
                var orcaVm = Driver.MainWindow.DataContext as OrcaShellViewModel;
                return orcaVm?.Sessions.FilteredSessions.Count ?? -1;
            });

            await Assert.That(after).IsGreaterThan(before);

            await Driver.ScreenshotAsync("orca-07-new-session").ConfigureAwait(false);
        }
        finally
        {
            RestoreClassic(state);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Test 8: Settings work
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     Opening the Settings modal (via Ctrl+P → no, via the MainViewModel
    ///     command directly) shows the SettingsView overlay on top of the Orca
    ///     shell. Captures <c>orca-08-settings.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task OrcaShell_SettingsWork()
    {
        await Driver.InitializeAsync().ConfigureAwait(false);
        var state = SwapToOrca();
        try
        {
            await Driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            // Trigger the Settings modal via the underlying MainViewModel
            // command. MainWindow.OnKeyDown forwards Ctrl+, to OpenSettings
            // in classic mode; in Orca mode the keydown handler extracts
            // MainViewModel via the OrcaShellViewModel.Main path.
            Driver.OnUIThread(() =>
            {
                var orcaVm = Driver.MainWindow.DataContext as OrcaShellViewModel;
                orcaVm?.Main.OpenSettingsCommand.Execute(null);
            });

            // The SettingsView header text "Settings" must appear.
            bool sawSettings = await Driver.WaitForTextAsync("Settings", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await Assert.That(sawSettings).IsTrue();

            await Driver.ScreenshotAsync("orca-08-settings").ConfigureAwait(false);

            // Close it.
            Driver.OnUIThread(() =>
            {
                var orcaVm = Driver.MainWindow.DataContext as OrcaShellViewModel;
                if (orcaVm is not null) orcaVm.Main.IsSettingsOpen = false;
            });
        }
        finally
        {
            RestoreClassic(state);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Test 9: Command palette works
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     Opening the Command Palette modal shows the palette overlay with
    ///     its search input. Captures <c>orca-09-command-palette.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task OrcaShell_CommandPaletteWorks()
    {
        await Driver.InitializeAsync().ConfigureAwait(false);
        var state = SwapToOrca();
        try
        {
            await Driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Driver.OnUIThread(() =>
            {
                var orcaVm = Driver.MainWindow.DataContext as OrcaShellViewModel;
                orcaVm?.Main.OpenCommandPaletteCommand.Execute(null);
            });

            // The palette header text "Command palette" must appear.
            bool sawPalette = await Driver.WaitForTextAsync("Command palette", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await Assert.That(sawPalette).IsTrue();

            await Driver.ScreenshotAsync("orca-09-command-palette").ConfigureAwait(false);

            // Close it.
            Driver.OnUIThread(() =>
            {
                var orcaVm = Driver.MainWindow.DataContext as OrcaShellViewModel;
                if (orcaVm is not null) orcaVm.Main.IsCommandPaletteOpen = false;
            });
        }
        finally
        {
            RestoreClassic(state);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Test 10: Theme is dark
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     The Orca shell uses the HarborDark theme: amber accent (#F5A623) +
    ///     neutral black (#0D0D0F). Verifies the HarborDark resource tokens
    ///     are resolvable from the Application resources.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task OrcaShell_ThemeIsDark()
    {
        await Driver.InitializeAsync().ConfigureAwait(false);
        var state = SwapToOrca();
        try
        {
            await Driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            // Verify the HarborDark tokens are present in the app resources.
            // Use FindResource (not Resources[key]) — the tokens live in a
            // MergedDictionaries entry (HarborDark.axaml), which Resources[key]
            // doesn't recurse into.
            bool hasAccent = Driver.OnUIThread(() =>
                Application.Current?.FindResource("AccentPrimaryBrush") is not null);
            await Assert.That(hasAccent).IsTrue();

            bool hasBgApp = Driver.OnUIThread(() =>
                Application.Current?.FindResource("BgAppBrush") is not null);
            await Assert.That(hasBgApp).IsTrue();

            bool hasBgRail = Driver.OnUIThread(() =>
                Application.Current?.FindResource("BgRailBrush") is not null);
            await Assert.That(hasBgRail).IsTrue();

            bool hasStateRunning = Driver.OnUIThread(() =>
                Application.Current?.FindResource("StateRunningBrush") is not null);
            await Assert.That(hasStateRunning).IsTrue();

            await Driver.ScreenshotAsync("orca-10-theme-dark").ConfigureAwait(false);
        }
        finally
        {
            RestoreClassic(state);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Test 11: Chat / Code mode switch works
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     Clicking the "Code" radio button in the session header bar switches
    ///     the main area to the code editor view (and back to "Chat" restores
    ///     the chat view). Captures <c>orca-11-code-mode.png</c>.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task OrcaShell_ModeSwitchWorks()
    {
        await Driver.InitializeAsync().ConfigureAwait(false);
        var state = SwapToOrca();
        try
        {
            await Driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            // Default mode is Chat — the empty-state placeholder proves it.
            bool hasPlaceholder = await Driver.WaitForTextAsync("Start a conversation", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await Assert.That(hasPlaceholder).IsTrue();

            // Click the "Code" tab.
            var codeTab = Driver.FindRadioButtonByText("Code");
            await Assert.That(codeTab).IsNotNull();
            await Driver.ClickAsync(codeTab!).ConfigureAwait(false);

            // Code editor placeholder.
            bool sawCode = await Driver.WaitForTextAsync("No file", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await Assert.That(sawCode).IsTrue();

            await Driver.ScreenshotAsync("orca-11-code-mode").ConfigureAwait(false);

            // Switch back to Chat.
            var chatTab = Driver.FindRadioButtonByText("Chat");
            await Assert.That(chatTab).IsNotNull();
            await Driver.ClickAsync(chatTab!).ConfigureAwait(false);

            bool backToChat = await Driver.WaitForTextAsync("Start a conversation", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await Assert.That(backToChat).IsTrue();
        }
        finally
        {
            RestoreClassic(state);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Test 12: Ctrl+P keyboard shortcut works in Orca shell
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    ///     The Ctrl+P keyboard shortcut opens the command palette in Orca shell
    ///     mode — verifies <see cref="MainWindow.OnKeyDown" /> extracts the
    ///     MainViewModel from the OrcaShellViewModel wrapper.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task OrcaShell_CtrlPOpensCommandPalette()
    {
        await Driver.InitializeAsync().ConfigureAwait(false);
        var state = SwapToOrca();
        try
        {
            await Driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            // Focus the window + send Ctrl+P via the MainWindow.OnKeyDown path.
            Driver.OnUIThread(() =>
            {
                var mw = Driver.MainWindow;
                mw.Focus();
                var args = new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.P,
                    KeyModifiers = KeyModifiers.Control
                };
                mw.RaiseEvent(args);
            });

            bool sawPalette = await Driver.WaitForTextAsync("Command palette", TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await Assert.That(sawPalette).IsTrue();

            await Driver.ScreenshotAsync("orca-12-ctrl-p").ConfigureAwait(false);

            // Close it.
            Driver.OnUIThread(() =>
            {
                var orcaVm = Driver.MainWindow.DataContext as OrcaShellViewModel;
                if (orcaVm is not null) orcaVm.Main.IsCommandPaletteOpen = false;
            });
        }
        finally
        {
            RestoreClassic(state);
        }
    }
}
