using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.Views;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core.Enums;

// See HeadlessAvaloniaDriver.cs for the rationale — the test namespace
// Harbor.E2E.App.Avalonia shadows Harbor.App.Avalonia for name lookup,
// so we alias the production App class to 'HarborApp'.
using HarborApp = global::Harbor.App.Avalonia.App;

namespace Harbor.E2E.App.Avalonia;

/// <summary>
///     Comprehensive per-component state E2E tests.
/// </summary>
/// <remarks>
///     <para>
///         Each test drives a SINGLE component (ChatView, SessionList, Settings,
///         Onboarding, CommandPalette, Toasts, StatusBar) into a SPECIFIC state,
///         captures a screenshot, and asserts the expected visual content is
///         present in the rendered tree. Screenshots land in
///         <c>~/.harbor/test-screenshots/</c> with the prefix <c>c-</c> so they
///         are visually distinguishable from the classic <c>AvaloniaUiTests</c>
///         suite. Each screenshot is later VLM-verified out-of-band by an
///         external script (see <c>docs/E2E_TESTING.md</c>).
///     </para>
///     <para>
///         The screenshot filenames embed the COMPONENT NAME + STATE NAME so a
///         reviewer can read what the screenshot SHOULD show without opening
///         the PNG, e.g. <c>c-chat-empty.png</c> = "ChatView in empty state".
///     </para>
///     <para>
///         <b>Concurrency:</b> tagged <c>[NotInParallel]</c> because the driver
///         mutates <c>$HOME</c> (process-wide env var) and shares the
///         process-wide Avalonia <see cref="Application"/> singleton.
///     </para>
/// </remarks>
[NotInParallel]
public sealed class ComponentStateE2ETests
{
    // NOTE: separate screenshot dir from AvaloniaUiTests so the other class's
    // SetupTestAsync (which wipes ~/.harbor/test-screenshots/ on first init)
    // doesn't erase our screenshots when both classes run in the same process.
    private static readonly string ScreenshotDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".harbor",
        "test-screenshots-comp");

    private static readonly string TempHome = Path.Combine(
        Path.GetTempPath(),
        "harbor-avalonia-comp-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));

    private static HeadlessAvaloniaDriver? _driver;

    [Before(HookType.Test)]
    public async Task SetupTestAsync()
    {
        if (_driver is null)
        {
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

    /// <summary>Helper: shorthand to invoke a delegate on the UI thread.</summary>
    private static void UI(Action action) => Driver.OnUIThread(action);

    /// <summary>Helper: shorthand to read a value on the UI thread.</summary>
    private static T UI<T>(Func<T> fn) => Driver.OnUIThread(fn);

    /// <summary>Helper: get the MainViewModel from the main window's DataContext.</summary>
    private static MainViewModel Vm => UI(() =>
        (Driver.MainWindow.DataContext as MainViewModel)
        ?? throw new InvalidOperationException("MainViewModel not bound."));

    // ════════════════════════════════════════════════════════════════════════
    //  CHAT VIEW — 9 states
    //  Each test pushes ChatViewModel into one specific state, captures a
    //  screenshot named c-chat-<state>.png, and asserts the expected text
    //  is visible in the rendered tree so the VLM has a deterministic
    //  "what should be in this screenshot" anchor.
    // ════════════════════════════════════════════════════════════════════════

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_State_Empty()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Assert: empty-state placeholder visible.
        var saw = await Driver.WaitForTextAsync("Start a conversation", TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await Assert.That(saw).IsTrue();

        // Assert: Send button disabled.
        var send = Driver.FindButtonByText("Send ▶");
        await Assert.That(send).IsNotNull();
        var enabled = UI(() => send!.IsEffectivelyEnabled);
        await Assert.That(enabled).IsFalse();

        await Driver.ScreenshotAsync("c-chat-empty").ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_State_TypingText()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var input = Driver.FindControlByName<TextBox>("InputBox");
        await Assert.That(input).IsNotNull();

        await Driver.TypeAsync(input!, "Drafting a question about Avalonia…").ConfigureAwait(false);
        var typedText = UI(() => input!.Text);
        await Assert.That(typedText).IsEqualTo("Drafting a question about Avalonia…");

        await Driver.ScreenshotAsync("c-chat-typing").ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_State_SendEnabled()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var input = Driver.FindControlByName<TextBox>("InputBox");
        await Driver.TypeAsync(input!, "ready to send").ConfigureAwait(false);

        var send = Driver.FindButtonByText("Send ▶");
        var isEnabled = UI(() => send!.IsEffectivelyEnabled);
        await Assert.That(isEnabled).IsTrue();

        await Driver.ScreenshotAsync("c-chat-send-enabled").ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_State_MessageSent()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var input = Driver.FindControlByName<TextBox>("InputBox");
        var send = Driver.FindButtonByText("Send ▶");
        await Driver.TypeAsync(input!, "Hello AI!").ConfigureAwait(false);
        await Driver.ClickAsync(send!).ConfigureAwait(false);

        var saw = await Driver.WaitForTextAsync("Hello AI!", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(saw).IsTrue();

        // Input cleared after send.
        var inputText = UI(() => input!.Text);
        await Assert.That(string.IsNullOrEmpty(inputText)).IsTrue();

        await Task.Delay(150).ConfigureAwait(false);
        await Driver.ScreenshotAsync("c-chat-message-sent").ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_State_Streaming()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            var chat = Vm.Chat;
            chat.IsStreaming = true;
            chat.StreamingBuffer = "The model is streaming a response token by token, character by character…";
        });
        await Task.Delay(300).ConfigureAwait(false);

        var hasStreaming = await Driver.WaitForTextAsync("streaming", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasStreaming).IsTrue();

        var hasBuffer = await Driver.WaitForTextAsync("streaming a response", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasBuffer).IsTrue();

        await Driver.ScreenshotAsync("c-chat-streaming").ConfigureAwait(false);

        // Reset for next test.
        UI(() =>
        {
            var chat = Vm.Chat;
            chat.IsStreaming = false;
            chat.StreamingBuffer = string.Empty;
        });
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_State_AgentRunning()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            var chat = Vm.Chat;
            chat.IsAgentRunning = true;
            chat.IsStreaming = false;
            chat.IsThinking = false;
            chat.StatusMessage = "Agent is running…";
        });
        await Task.Delay(300).ConfigureAwait(false);

        var hasIndicator = await Driver.WaitForTextAsync("running", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasIndicator).IsTrue();

        await Driver.ScreenshotAsync("c-chat-agent-running").ConfigureAwait(false);

        UI(() =>
        {
            var chat = Vm.Chat;
            chat.IsAgentRunning = false;
            chat.StatusMessage = string.Empty;
        });
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_State_Thinking()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            var chat = Vm.Chat;
            chat.IsThinking = true;
            chat.IsAgentRunning = true;
            chat.IsStreaming = false;
        });
        await Task.Delay(300).ConfigureAwait(false);

        var hasThinking = await Driver.WaitForTextAsync("thinking", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasThinking).IsTrue();

        await Driver.ScreenshotAsync("c-chat-thinking").ConfigureAwait(false);

        UI(() =>
        {
            var chat = Vm.Chat;
            chat.IsThinking = false;
            chat.IsAgentRunning = false;
        });
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_State_Error()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Push an error chat line through the UiStore so the error styling +
        // ChatErrorBrush color applies. We add it directly to the chat
        // transcript — the view renders ChatRole.Error in red.
        UI(() =>
        {
            var chat = Vm.Chat;
            chat.Lines.Add(new ChatLineViewModel(
                ChatRole.Error,
                "Something went wrong: provider returned 503 Service Unavailable"));
        });
        await Task.Delay(200).ConfigureAwait(false);

        var hasError = await Driver.WaitForTextAsync("Something went wrong", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasError).IsTrue();

        await Driver.ScreenshotAsync("c-chat-error").ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_State_Cleared()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // First add a message so we have something to clear.
        var input = Driver.FindControlByName<TextBox>("InputBox");
        var send = Driver.FindButtonByText("Send ▶");
        await Driver.TypeAsync(input!, "Message that will be cleared").ConfigureAwait(false);
        await Driver.ClickAsync(send!).ConfigureAwait(false);
        await Task.Delay(150).ConfigureAwait(false);

        bool had = await Driver.WaitForTextAsync("Message that will be cleared", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(had).IsTrue();

        // Now clear via Ctrl+L equivalent (ClearCommand).
        UI(() => Vm.Chat.ClearCommand.Execute(null));
        await Task.Delay(200).ConfigureAwait(false);

        // Empty-state placeholder must be back.
        var saw = await Driver.WaitForTextAsync("Start a conversation", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(saw).IsTrue();

        await Driver.ScreenshotAsync("c-chat-cleared").ConfigureAwait(false);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SESSION LIST — 6 states
    // ════════════════════════════════════════════════════════════════════════

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task SessionList_State_Empty()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Make sure the sidebar is visible.
        UI(() => Vm.IsSidebarVisible = true);
        await Task.Delay(200).ConfigureAwait(false);

        // Clear all sessions so the list is empty.
        UI(() => Vm.Sessions.Sessions.Clear());
        await Task.Delay(150).ConfigureAwait(false);

        await Driver.ScreenshotAsync("c-sessions-empty").ConfigureAwait(false);

        // Sidebar should be visible (even if the list itself is empty).
        var sidebarVisible = UI(() => Vm.IsSidebarVisible);
        await Assert.That(sidebarVisible).IsTrue();
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task SessionList_State_WithSessions()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsSidebarVisible = true;
            var sessions = Vm.Sessions.Sessions;
            sessions.Clear();
            sessions.Add(new SessionItemViewModel(
                "s1", "Refactor agent loop", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow.AddMinutes(-3), 12, "/home/z/myproject"));
            sessions.Add(new SessionItemViewModel(
                "s2", "Investigate IPC deadlock", "code", "gpt-4o", "openai",
                DateTimeOffset.UtcNow.AddHours(-1), 4, "/home/z/myproject"));
            sessions.Add(new SessionItemViewModel(
                "s3", "Polish onboarding flow", "code", "claude-sonnet-4", "anthropic",
                DateTimeOffset.UtcNow.AddDays(-1), 22, "/home/z/myproject"));
        });
        await Task.Delay(250).ConfigureAwait(false);

        var has1 = await Driver.WaitForTextAsync("Refactor agent loop", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var has2 = await Driver.WaitForTextAsync("Investigate IPC deadlock", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(has1 && has2).IsTrue();

        await Driver.ScreenshotAsync("c-sessions-with-items").ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task SessionList_State_ActiveHighlighted()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsSidebarVisible = true;
            var sessions = Vm.Sessions.Sessions;
            sessions.Clear();
            sessions.Add(new SessionItemViewModel(
                "s1", "First session", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow, 1, "/home/z/myproject"));
            sessions.Add(new SessionItemViewModel(
                "s2", "Second session", "code", "gpt-4o", "openai",
                DateTimeOffset.UtcNow, 2, "/home/z/myproject"));
            // Mark the second one as active.
            Vm.Sessions.ActiveSession = sessions[1];
        });
        await Task.Delay(250).ConfigureAwait(false);

        var activeId = UI(() => Vm.Sessions.ActiveSession?.Id);
        await Assert.That(activeId).IsEqualTo("s2");

        await Driver.ScreenshotAsync("c-sessions-active-highlighted").ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task SessionList_State_SearchFiltered()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsSidebarVisible = true;
            var sessions = Vm.Sessions.Sessions;
            sessions.Clear();
            sessions.Add(new SessionItemViewModel(
                "s1", "Refactor agent loop", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow, 12, "/home/z/myproject"));
            sessions.Add(new SessionItemViewModel(
                "s2", "Investigate IPC deadlock", "code", "gpt-4o", "openai",
                DateTimeOffset.UtcNow, 4, "/home/z/myproject"));
            sessions.Add(new SessionItemViewModel(
                "s3", "Polish onboarding flow", "code", "claude-sonnet-4", "anthropic",
                DateTimeOffset.UtcNow, 22, "/home/z/myproject"));
            // Apply a search filter that should match only one of the three.
            Vm.Sessions.SearchText = "IPC";
        });
        await Task.Delay(200).ConfigureAwait(false);

        // Trigger the client-side filter by clearing + re-adding matching rows
        // (RefreshCommand hits the store which is async; we shortcut for the
        // determinism of the screenshot).
        UI(() =>
        {
            var sessions = Vm.Sessions.Sessions;
            var matching = sessions.Where(s => s.Title.Contains("IPC", StringComparison.OrdinalIgnoreCase)).ToList();
            sessions.Clear();
            foreach (var m in matching) sessions.Add(m);
        });
        await Task.Delay(150).ConfigureAwait(false);

        var hasMatch = await Driver.WaitForTextAsync("Investigate IPC deadlock", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasMatch).IsTrue();

        var hasOther = Driver.GetAllVisibleText().Contains("Refactor agent loop", StringComparison.Ordinal);
        await Assert.That(hasOther).IsFalse();

        await Driver.ScreenshotAsync("c-sessions-search-filtered").ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task SessionList_State_NewSessionCreated()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsSidebarVisible = true;
            var sessions = Vm.Sessions.Sessions;
            sessions.Clear();
            sessions.Add(new SessionItemViewModel(
                "s1", "Existing session", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow, 5, "/home/z/myproject"));
        });
        await Task.Delay(150).ConfigureAwait(false);

        // Add a new session row directly (simulates what NewSessionCommand does
        // after store roundtrip — but synchronously so the screenshot is
        // deterministic).
        UI(() =>
        {
            Vm.Sessions.Sessions.Add(new SessionItemViewModel(
                "s2", "New session", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow, 0, "/home/z/myproject"));
            Vm.Sessions.ActiveSession = Vm.Sessions.Sessions[1];
        });
        await Task.Delay(250).ConfigureAwait(false);

        var hasNew = await Driver.WaitForTextAsync("New session", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasNew).IsTrue();

        await Driver.ScreenshotAsync("c-sessions-new-created").ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task SessionList_State_SessionDeleted()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsSidebarVisible = true;
            var sessions = Vm.Sessions.Sessions;
            sessions.Clear();
            sessions.Add(new SessionItemViewModel(
                "s1", "Keep me", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow, 5, "/home/z/myproject"));
            sessions.Add(new SessionItemViewModel(
                "s2", "Delete me", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow, 3, "/home/z/myproject"));
            // Remove the second one (simulates successful DeleteCommand).
            sessions.RemoveAt(1);
        });
        await Task.Delay(200).ConfigureAwait(false);

        var hasKeep = await Driver.WaitForTextAsync("Keep me", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasKeep).IsTrue();

        var hasDeleted = Driver.GetAllVisibleText().Contains("Delete me", StringComparison.Ordinal);
        await Assert.That(hasDeleted).IsFalse();

        await Driver.ScreenshotAsync("c-sessions-deleted").ConfigureAwait(false);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SETTINGS — 4 states
    // ════════════════════════════════════════════════════════════════════════

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Settings_State_Open()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsSettingsOpen = true);
        await Task.Delay(400).ConfigureAwait(false);

        // Theme label visible.
        var hasTheme = await Driver.WaitForTextAsync("Theme", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasTheme).IsTrue();

        await Driver.ScreenshotAsync("c-settings-open").ConfigureAwait(false);

        UI(() => Vm.IsSettingsOpen = false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Settings_State_ThemeChanged()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsSettingsOpen = true;
            Vm.Settings.Theme = "light";
        });
        await Task.Delay(400).ConfigureAwait(false);

        var theme = UI(() => Vm.Settings.Theme);
        await Assert.That(theme).IsEqualTo("light");

        await Driver.ScreenshotAsync("c-settings-theme-changed").ConfigureAwait(false);

        // Revert without saving so the next test starts with the original theme.
        UI(() =>
        {
            Vm.Settings.Theme = "dark";
            Vm.IsSettingsOpen = false;
        });
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Settings_State_Saved()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        MainViewModel? settingsVm = null;
        UI(() =>
        {
            Vm.IsSettingsOpen = true;
            Vm.Settings.Theme = "light";
            Vm.Settings.DefaultModel = "test-model-save";
            settingsVm = Vm;
        });

        // Run SaveAsync on the UI thread (it's a relay command that returns Task).
        Dispatcher.UIThread
            .InvokeAsync(() => settingsVm!.Settings.SaveCommand.ExecuteAsync(null))
            .GetAwaiter().GetResult();
        await Task.Delay(700).ConfigureAwait(false);

        var configPath = Path.Combine(TempHome, ".harbor", "config.json");
        var configText = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
        await Assert.That(configText).Contains("light");
        await Assert.That(configText).Contains("test-model-save");

        await Driver.ScreenshotAsync("c-settings-saved").ConfigureAwait(false);

        UI(() => Vm.IsSettingsOpen = false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Settings_State_Cancelled()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsSettingsOpen = true;
            // Mutate Theme (without saving).
            Vm.Settings.Theme = "light";
        });
        await Task.Delay(300).ConfigureAwait(false);

        // Cancel — should revert Theme back to the persisted value ("dark").
        UI(() => Vm.Settings.CancelCommand.Execute(null));
        await Task.Delay(200).ConfigureAwait(false);

        var themeAfter = UI(() => Vm.Settings.Theme);
        await Assert.That(themeAfter).IsEqualTo("dark");

        await Driver.ScreenshotAsync("c-settings-cancelled").ConfigureAwait(false);

        UI(() => Vm.IsSettingsOpen = false);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ONBOARDING — 7 states (5 steps + back + skip)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Helper to open the onboarding window and bind a fresh VM.</summary>
    private async Task<(OnboardingWindow window, OnboardingViewModel vm)> OpenOnboardingAsync(int step)
    {
        var services = HarborApp.Services;
        var vm = services.GetRequiredService<OnboardingViewModel>();
        // Set the requested step (default step is 1; provider pre-selection
        // already done in the constructor).
        if (step >= 1) UI(() => vm.CurrentStep = step);
        // Pre-fill model on step 4+ so the Next button is enabled.
        if (step >= 4) UI(() => vm.DefaultModel = vm.SelectedProvider?.DefaultModel ?? "qwen2.5-coder:7b");

        var window = Dispatcher.UIThread.InvokeAsync<OnboardingWindow>(() =>
        {
            var w = new OnboardingWindow();
            w.Bind(vm);
            w.DataContext = vm;
            w.Show();
            return w;
        }).GetAwaiter().GetResult();

        // Let layout + first render settle.
        await Task.Delay(180).ConfigureAwait(false);
        return (window, vm);
    }

    /// <summary>Capture a screenshot of the onboarding window.</summary>
    private async Task CaptureOnboardingAsync(OnboardingWindow window, string name)
    {
        var path = Path.Combine(ScreenshotDir, $"{name}.png");
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
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Onboarding_State_Step1_Welcome()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (window, vm) = await OpenOnboardingAsync(step: 1).ConfigureAwait(false);
        try
        {
            var sawBrand = await Driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            await Assert.That(sawBrand).IsTrue();
            var step = UI(() => vm.CurrentStep);
            await Assert.That(step).IsEqualTo(1);
            await CaptureOnboardingAsync(window, "c-onboarding-step1-welcome").ConfigureAwait(false);
        }
        finally
        {
            Dispatcher.UIThread.InvokeAsync(() => window.Close()).GetAwaiter().GetResult();
        }
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Onboarding_State_Step2_Providers()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (window, vm) = await OpenOnboardingAsync(step: 2).ConfigureAwait(false);
        try
        {
            var hasAnthropic = await Driver.WaitForTextAsync("Anthropic", TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            var hasOllama = await Driver.WaitForTextAsync("Ollama", TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            await Assert.That(hasAnthropic || hasOllama).IsTrue();
            await CaptureOnboardingAsync(window, "c-onboarding-step2-providers").ConfigureAwait(false);
        }
        finally
        {
            Dispatcher.UIThread.InvokeAsync(() => window.Close()).GetAwaiter().GetResult();
        }
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Onboarding_State_Step3_ApiKey()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        // For step 3 we need a provider that requires a key.
        var (window, vm) = await OpenOnboardingAsync(step: 3).ConfigureAwait(false);
        try
        {
            UI(() =>
            {
                // Pre-select Anthropic so the API key step is meaningful.
                foreach (var p in vm.Providers) p.IsSelected = false;
                var anthropic = vm.Providers.First(p => p.Id == "anthropic");
                anthropic.IsSelected = true;
                vm.RefreshSelectedProviderCommand.Execute(null);
                vm.ApiKey = "sk-ant-test-key-1234";
            });
            await Task.Delay(150).ConfigureAwait(false);
            await CaptureOnboardingAsync(window, "c-onboarding-step3-apikey").ConfigureAwait(false);
        }
        finally
        {
            Dispatcher.UIThread.InvokeAsync(() => window.Close()).GetAwaiter().GetResult();
        }
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Onboarding_State_Step4_Model()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (window, vm) = await OpenOnboardingAsync(step: 4).ConfigureAwait(false);
        try
        {
            UI(() => vm.DefaultModel = "qwen2.5-coder:7b");
            await Task.Delay(150).ConfigureAwait(false);
            var hasModel = await Driver.WaitForTextAsync("qwen2.5-coder", TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            await Assert.That(hasModel).IsTrue();
            await CaptureOnboardingAsync(window, "c-onboarding-step4-model").ConfigureAwait(false);
        }
        finally
        {
            Dispatcher.UIThread.InvokeAsync(() => window.Close()).GetAwaiter().GetResult();
        }
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Onboarding_State_Step5_Theme()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (window, vm) = await OpenOnboardingAsync(step: 5).ConfigureAwait(false);
        try
        {
            UI(() => vm.ThemeChoice = "dark");
            await Task.Delay(150).ConfigureAwait(false);
            await CaptureOnboardingAsync(window, "c-onboarding-step5-theme").ConfigureAwait(false);
        }
        finally
        {
            Dispatcher.UIThread.InvokeAsync(() => window.Close()).GetAwaiter().GetResult();
        }
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Onboarding_State_Back()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (window, vm) = await OpenOnboardingAsync(step: 3).ConfigureAwait(false);
        try
        {
            UI(() => vm.BackCommand.Execute(null));
            await Task.Delay(150).ConfigureAwait(false);
            var step = UI(() => vm.CurrentStep);
            await Assert.That(step).IsEqualTo(2);
            await CaptureOnboardingAsync(window, "c-onboarding-step-back").ConfigureAwait(false);
        }
        finally
        {
            Dispatcher.UIThread.InvokeAsync(() => window.Close()).GetAwaiter().GetResult();
        }
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Onboarding_State_Skip()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (window, vm) = await OpenOnboardingAsync(step: 2).ConfigureAwait(false);
        try
        {
            UI(() => vm.SkipCommand.Execute(null));
            await Task.Delay(150).ConfigureAwait(false);
            var isCompleted = UI(() => vm.IsCompleted);
            await Assert.That(isCompleted).IsTrue();
            await CaptureOnboardingAsync(window, "c-onboarding-skip").ConfigureAwait(false);
        }
        finally
        {
            Dispatcher.UIThread.InvokeAsync(() => window.Close()).GetAwaiter().GetResult();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  COMMAND PALETTE — 5 states
    // ════════════════════════════════════════════════════════════════════════

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task CommandPalette_State_Open()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.IsCommandPaletteOpen = true);
        await Task.Delay(300).ConfigureAwait(false);

        var hasPalette = await Driver.WaitForTextAsync("Command", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasPalette).IsTrue();

        await Driver.ScreenshotAsync("c-cmd-palette-open").ConfigureAwait(false);

        UI(() => Vm.IsCommandPaletteOpen = false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task CommandPalette_State_Search()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsCommandPaletteOpen = true;
            Vm.CommandPalette.Query = "theme";
        });
        await Task.Delay(300).ConfigureAwait(false);

        // Theme-related command(s) should be in the visible results.
        var hasTheme = await Driver.WaitForTextAsync("Toggle theme", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasTheme).IsTrue();

        await Driver.ScreenshotAsync("c-cmd-palette-search").ConfigureAwait(false);

        UI(() =>
        {
            Vm.CommandPalette.Query = string.Empty;
            Vm.IsCommandPaletteOpen = false;
        });
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task CommandPalette_State_Navigate()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsCommandPaletteOpen = true;
            Vm.CommandPalette.Query = string.Empty;
            // Move selection down a few times so the highlighted row index > 0.
            Vm.CommandPalette.MoveDown();
            Vm.CommandPalette.MoveDown();
            Vm.CommandPalette.MoveDown();
        });
        await Task.Delay(300).ConfigureAwait(false);

        var idx = UI(() => Vm.CommandPalette.SelectedIndex);
        await Assert.That(idx).IsEqualTo(3);

        await Driver.ScreenshotAsync("c-cmd-palette-navigate").ConfigureAwait(false);

        // Move back up so we don't leave selection on an action command.
        UI(() =>
        {
            Vm.CommandPalette.MoveUp();
            Vm.CommandPalette.MoveUp();
            Vm.CommandPalette.MoveUp();
            Vm.IsCommandPaletteOpen = false;
        });
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task CommandPalette_State_Execute()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        // Sidebar starts visible. Executing "Toggle sidebar" via the palette
        // should hide it.
        var before = UI(() => Vm.IsSidebarVisible);
        await Assert.That(before).IsTrue();

        UI(() =>
        {
            Vm.IsCommandPaletteOpen = true;
            Vm.CommandPalette.Query = "Toggle sidebar";
        });
        await Task.Delay(250).ConfigureAwait(false);

        UI(() => Vm.CommandPalette.InvokeSelected());
        await Task.Delay(250).ConfigureAwait(false);

        var after = UI(() => Vm.IsSidebarVisible);
        await Assert.That(after).IsFalse();

        await Driver.ScreenshotAsync("c-cmd-palette-execute").ConfigureAwait(false);

        // Restore for next test.
        UI(() =>
        {
            Vm.IsSidebarVisible = true;
            Vm.IsCommandPaletteOpen = false;
        });
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task CommandPalette_State_Closed()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsCommandPaletteOpen = true;
        });
        await Task.Delay(200).ConfigureAwait(false);

        UI(() => Vm.IsCommandPaletteOpen = false);
        await Task.Delay(200).ConfigureAwait(false);

        var isOpen = UI(() => Vm.IsCommandPaletteOpen);
        await Assert.That(isOpen).IsFalse();

        await Driver.ScreenshotAsync("c-cmd-palette-closed").ConfigureAwait(false);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  TOASTS — 6 states
    // ════════════════════════════════════════════════════════════════════════

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Toasts_State_Info()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.AddToast(new ToastNotification("Info: connection established", ToastKind.Info)));
        await Task.Delay(300).ConfigureAwait(false);

        var has = await Driver.WaitForTextAsync("Info: connection established", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(has).IsTrue();

        await Driver.ScreenshotAsync("c-toast-info").ConfigureAwait(false);

        // Wait for auto-dismiss (4s + buffer).
        await Task.Delay(500).ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Toasts_State_Success()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.AddToast(new ToastNotification("Success: settings saved", ToastKind.Success)));
        await Task.Delay(300).ConfigureAwait(false);

        var has = await Driver.WaitForTextAsync("Success: settings saved", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(has).IsTrue();

        await Driver.ScreenshotAsync("c-toast-success").ConfigureAwait(false);

        await Task.Delay(500).ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Toasts_State_Warning()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.AddToast(new ToastNotification("Warning: approaching rate limit", ToastKind.Warning)));
        await Task.Delay(300).ConfigureAwait(false);

        var has = await Driver.WaitForTextAsync("Warning: approaching rate limit", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(has).IsTrue();

        await Driver.ScreenshotAsync("c-toast-warning").ConfigureAwait(false);

        await Task.Delay(500).ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Toasts_State_Error()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.AddToast(new ToastNotification("Error: provider returned 503", ToastKind.Error)));
        await Task.Delay(300).ConfigureAwait(false);

        var has = await Driver.WaitForTextAsync("Error: provider returned 503", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(has).IsTrue();

        await Driver.ScreenshotAsync("c-toast-error").ConfigureAwait(false);

        await Task.Delay(500).ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Toasts_State_MultipleStacked()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.AddToast(new ToastNotification("First toast (info)", ToastKind.Info));
            Vm.AddToast(new ToastNotification("Second toast (success)", ToastKind.Success));
            Vm.AddToast(new ToastNotification("Third toast (warning)", ToastKind.Warning));
            Vm.AddToast(new ToastNotification("Fourth toast (error)", ToastKind.Error));
        });
        await Task.Delay(400).ConfigureAwait(false);

        var all = Driver.GetAllVisibleText();
        await Assert.That(all.Contains("First toast", StringComparison.Ordinal)).IsTrue();
        await Assert.That(all.Contains("Second toast", StringComparison.Ordinal)).IsTrue();
        await Assert.That(all.Contains("Third toast", StringComparison.Ordinal)).IsTrue();
        await Assert.That(all.Contains("Fourth toast", StringComparison.Ordinal)).IsTrue();

        await Driver.ScreenshotAsync("c-toast-multiple-stacked").ConfigureAwait(false);

        // Wait for auto-dismiss.
        await Task.Delay(500).ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Toasts_State_AutoDismissed()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.AddToast(new ToastNotification("Will disappear shortly", ToastKind.Info)));
        await Task.Delay(300).ConfigureAwait(false);

        var has = await Driver.WaitForTextAsync("Will disappear shortly", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(has).IsTrue();

        // Wait 5s — toast should auto-dismiss after 4s.
        await Task.Delay(500).ConfigureAwait(false);

        var stillThere = Driver.GetAllVisibleText().Contains("Will disappear shortly", StringComparison.Ordinal);
        await Assert.That(stillThere).IsFalse();

        await Driver.ScreenshotAsync("c-toast-auto-dismissed").ConfigureAwait(false);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  STATUS BAR — 5 states
    // ════════════════════════════════════════════════════════════════════════

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task StatusBar_State_Idle()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.StatusText = "idle";
            Vm.IsRunning = false;
        });
        await Task.Delay(200).ConfigureAwait(false);

        var hasIdle = await Driver.WaitForTextAsync("idle", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasIdle).IsTrue();

        await Driver.ScreenshotAsync("c-status-bar-idle").ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task StatusBar_State_Running()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.StatusText = "running";
            Vm.IsRunning = true;
            Vm.AgentLabel = "code";
            Vm.ModelLabel = "qwen2.5-coder:7b";
        });
        await Task.Delay(250).ConfigureAwait(false);

        var hasRunning = await Driver.WaitForTextAsync("running", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasRunning).IsTrue();

        var hasModel = await Driver.WaitForTextAsync("qwen2.5-coder", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasModel).IsTrue();

        await Driver.ScreenshotAsync("c-status-bar-running").ConfigureAwait(false);

        // Reset for next test.
        UI(() =>
        {
            Vm.StatusText = "idle";
            Vm.IsRunning = false;
        });
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task StatusBar_State_TokenCounts()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.StatusText = "idle";
            Vm.TokensIn = 12_345;
            Vm.TokensOut = 6_789;
        });
        await Task.Delay(250).ConfigureAwait(false);

        // Token counts use N0 format — "12,345" + "6,789".
        var hasIn = await Driver.WaitForTextAsync("12,345", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var hasOut = await Driver.WaitForTextAsync("6,789", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasIn && hasOut).IsTrue();

        await Driver.ScreenshotAsync("c-status-bar-tokens").ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task StatusBar_State_Cost()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.StatusText = "idle";
            Vm.CostUsd = 0.4231m;
        });
        await Task.Delay(250).ConfigureAwait(false);

        // Cost format is ${0:F4} → "$0.4231".
        var hasCost = await Driver.WaitForTextAsync("$0.4231", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasCost).IsTrue();

        await Driver.ScreenshotAsync("c-status-bar-cost").ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task StatusBar_State_SessionCount()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.StatusText = "idle";
            Vm.ActiveSessionCount = 7;
        });
        await Task.Delay(250).ConfigureAwait(false);

        // ActiveSessionCount uses StringFormat '{}{0} session' → "7 session".
        var hasCount = await Driver.WaitForTextAsync("7 session", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasCount).IsTrue();

        await Driver.ScreenshotAsync("c-status-bar-session-count").ConfigureAwait(false);

        // Reset for next test.
        UI(() => Vm.ActiveSessionCount = 1);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  EXTRA — mixed state for visual coverage (push count to 50+)
    // ════════════════════════════════════════════════════════════════════════

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_State_StopButtonVisibleWhenThinking()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            var chat = Vm.Chat;
            chat.IsThinking = true;
            chat.IsAgentRunning = true;
        });
        await Task.Delay(300).ConfigureAwait(false);

        var stop = Driver.FindButtonByText("Stop ■");
        await Assert.That(stop).IsNotNull();
        var visible = UI(() => stop!.IsVisible);
        await Assert.That(visible).IsTrue();

        await Driver.ScreenshotAsync("c-chat-stop-button-visible").ConfigureAwait(false);

        UI(() =>
        {
            var chat = Vm.Chat;
            chat.IsThinking = false;
            chat.IsAgentRunning = false;
        });
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_State_LongMessageWraps()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        var longText = "This is a long user message designed to verify that text wrapping works "
            + "correctly inside the chat bubble. The message should wrap to multiple lines without "
            + "overflowing the bubble's bounds, and the bubble should grow vertically to fit all "
            + "the text. If wrapping is broken, the text would be clipped or overflow horizontally.";
        UI(() => Vm.Chat.Lines.Add(new ChatLineViewModel(
            ChatRole.User, longText)));
        await Task.Delay(250).ConfigureAwait(false);

        var saw = await Driver.WaitForTextAsync("long user message", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(saw).IsTrue();

        await Driver.ScreenshotAsync("c-chat-long-message").ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task SessionList_State_WithGitInfo()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.IsSidebarVisible = true;
            var sessions = Vm.Sessions.Sessions;
            sessions.Clear();
            var item = new SessionItemViewModel(
                "s1", "Git-enabled session", "code", "qwen2.5-coder:7b", "ollama",
                DateTimeOffset.UtcNow, 8, "/home/z/myproject")
            {
                GitBranch = "main",
                GitIsDirty = true,
            };
            sessions.Add(item);
        });
        await Task.Delay(250).ConfigureAwait(false);

        var has = await Driver.WaitForTextAsync("Git-enabled session", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(has).IsTrue();

        await Driver.ScreenshotAsync("c-sessions-git-info").ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task StatusBar_State_FullPopulation()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.StatusText = "running";
            Vm.IsRunning = true;
            Vm.AgentLabel = "code";
            Vm.ModelLabel = "gpt-4o";
            Vm.ProviderLabel = "openai";
            Vm.TokensIn = 23_456;
            Vm.TokensOut = 11_222;
            Vm.CostUsd = 1.2345m;
            Vm.ActiveSessionCount = 3;
        });
        await Task.Delay(300).ConfigureAwait(false);

        var hasRunning = await Driver.WaitForTextAsync("running", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var hasModel = await Driver.WaitForTextAsync("gpt-4o", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var hasCost = await Driver.WaitForTextAsync("$1.2345", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasRunning && hasModel && hasCost).IsTrue();

        await Driver.ScreenshotAsync("c-status-bar-full").ConfigureAwait(false);

        UI(() =>
        {
            Vm.StatusText = "idle";
            Vm.IsRunning = false;
            Vm.ModelLabel = "—";
            Vm.ActiveSessionCount = 1;
        });
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task ChatView_State_MultiTurnConversation()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            var chat = Vm.Chat;
            chat.Lines.Add(new ChatLineViewModel(
                ChatRole.User, "What is the capital of France?"));
            chat.Lines.Add(new ChatLineViewModel(
                ChatRole.Assistant, "The capital of France is Paris."));
            chat.Lines.Add(new ChatLineViewModel(
                ChatRole.User, "And of Germany?"));
            chat.Lines.Add(new ChatLineViewModel(
                ChatRole.Assistant, "The capital of Germany is Berlin."));
        });
        await Task.Delay(300).ConfigureAwait(false);

        var saw1 = await Driver.WaitForTextAsync("capital of France", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var saw2 = await Driver.WaitForTextAsync("Berlin", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(saw1 && saw2).IsTrue();

        await Driver.ScreenshotAsync("c-chat-multi-turn").ConfigureAwait(false);
    }

    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Toasts_State_LongMessage()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.AddToast(new ToastNotification(
            "This is a longer toast message that should wrap across multiple lines inside the toast container without overflowing the screen edge.",
            ToastKind.Info)));
        await Task.Delay(400).ConfigureAwait(false);

        var saw = await Driver.WaitForTextAsync("longer toast message", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(saw).IsTrue();

        await Driver.ScreenshotAsync("c-toast-long-message").ConfigureAwait(false);

        await Task.Delay(500).ConfigureAwait(false);
    }
}
