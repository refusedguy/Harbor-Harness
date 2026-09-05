using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using CSharpFunctionalExtensions;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Harbor.TestKit;
using Microsoft.Extensions.Logging;
using TUnit.Core;

namespace Harbor.App.Avalonia.Tests;

public class AvaloniaWorkspaceCommandsTests
{
    private sealed class FakeSessionManager : ISessionManager
    {
        public Session? Active { get; set; }
        public SessionContext? GetContext(string sessionId) => null;
        public SessionStatus GetStatus(string sessionId) => SessionStatus.Idle;
        public void SetStatus(string sessionId, SessionStatus status) { }
        public void NotifyMessageCount(string sessionId, int count) { }
        public GitSessionInfo GetGitInfo(string sessionId) => new(null, false, 0, null);
        public void RefreshGitInfo(string sessionId, string directory) { }
        public Task EnsureDefaultSessionAsync() => Task.CompletedTask;
        public Task RebindFromCommonConfigAsync() => Task.CompletedTask;

        public Session? NewSessionResult { get; set; }
        public bool NewSessionCalled { get; private set; }

        public Task<Session?> NewSessionAsync(string? agentName = null, string? providerId = null, string? modelId = null, string? workingDirectory = null)
        {
            NewSessionCalled = true;
            return Task.FromResult(NewSessionResult);
        }

        public Task<bool> OpenSessionAsync(string sessionId) => Task.FromResult(true);

        public Session? BranchResult { get; set; }
        public bool BranchCalled { get; private set; }

        public Task<Session?> BranchActiveAsync()
        {
            BranchCalled = true;
            return Task.FromResult(BranchResult);
        }

        public Task<bool> DeleteSessionAsync(string sessionId) => Task.FromResult(true);
        public Task<bool> RenameSessionAsync(string sessionId, string newTitle) => Task.FromResult(true);

        public event Action<string, SessionStatus>? StatusChanged;
        public event Action<string, int>? MessageCountChanged;
    }

    private sealed class FakeDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, string okLabel = "OK", string cancelLabel = "Cancel", CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<string?> PromptAsync(string title, string message, string defaultValue = "", CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task AlertAsync(string title, string message, string okLabel = "OK", CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeToastService : IToastService
    {
        public event EventHandler<ToastNotification>? ToastAdded;
        public void Show(string message) => Show(message, ToastKind.Info);
        public void Show(string message, ToastKind kind = ToastKind.Info)
        {
            LastMessage = message;
            LastKind = kind;
        }
        public string? LastMessage { get; private set; }
        public ToastKind LastKind { get; private set; }
    }

    private sealed class FakeDispatcherAdapter : IDispatcherAdapter
    {
        public void Post(Action action) => action();
        public T Invoke<T>(Func<T> func) => func();
        public void Bind(UiStore store) { }
        public void Unbind(UiStore store) { }
        public event EventHandler<UiState>? StateChanged;
    }

    private sealed class FakeFilePicker : IFilePicker
    {
        public Task<IReadOnlyList<string>> PickFilesAsync(string title, bool allowMultiple = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(PickFilesResult ?? Array.Empty<string>());

        public Task<string?> PickSaveFileAsync(string title, string defaultFileName, CancellationToken cancellationToken = default)
            => Task.FromResult(PickSaveFileResult);

        public Task<string?> PickFolderAsync(string title = "Select Folder", CancellationToken cancellationToken = default)
            => Task.FromResult(PickFolderResult);

        public IReadOnlyList<string>? PickFilesResult { get; set; }
        public string? PickSaveFileResult { get; set; }
        public string? PickFolderResult { get; set; }
    }

    private sealed class FakeAgentRunner : IAgentRunner
    {
        public CancellationTokenSource AbortSource { get; } = new CancellationTokenSource();
        public Task<Result> PromptAsync(string text, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ResetAbortSource() => ResetAbortSourceCalled = true;
        public bool ResetAbortSourceCalled { get; private set; }
    }

    private sealed class FakeLogger<T> : ILogger<T>, ILogger
    {
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable BeginScope<TState>(TState state) => null!;
    }

    private static ChatViewModel CreateChatViewModel(IToastService? toasts = null, IAgentRunner? agentRunner = null)
    {
        var store = new UiStore();
        var effects = new TuiEffectHost(agentRunner ?? new FakeAgentRunner(), store);
        var sessionManager = new FakeSessionManager();
        var sessionStore = new FakeSessionStore();
        var dispatcher = new FakeDispatcherAdapter();
        var logger = new FakeLogger<ChatViewModel>();
        var toastService = toasts ?? new FakeToastService();
        var renderEngine = new UiRenderEngine(new DefaultUiProjector(), new AvaloniaUiViewport(), new ChatStreamingPresenter());

        return new ChatViewModel(store, effects, sessionManager, sessionStore, dispatcher, logger, toastService, renderEngine);
    }

    private static SessionListViewModel CreateSessionListViewModel(ISessionStore? store = null, ISessionManager? manager = null)
    {
        var sessionStore = store ?? new FakeSessionStore();
        var sessionManager = manager ?? new FakeSessionManager();
        var dialogs = new FakeDialogService();
        var logger = new FakeLogger<SessionListViewModel>();
        var toasts = new FakeToastService();
        var dispatcher = new FakeDispatcherAdapter();

        return new SessionListViewModel(sessionStore, sessionManager, dialogs, logger, toasts, dispatcher);
    }

    private static CodeEditorViewModel CreateCodeEditorViewModel(IToastService? toasts = null, AvaloniaFilePicker? picker = null)
    {
        var filePicker = picker ?? new AvaloniaFilePicker(new FakeLogger<AvaloniaFilePicker>());
        var logger = new FakeLogger<CodeEditorViewModel>();
        var toastService = toasts ?? new FakeToastService();
        var dispatcher = new FakeDispatcherAdapter();

        return new CodeEditorViewModel(filePicker, logger, toastService, dispatcher);
    }

    private static TuiEffectHost CreateTuiEffectHost(IAgentRunner? agentRunner = null)
    {
        var runner = agentRunner ?? new FakeAgentRunner();
        var store = new UiStore();
        return new TuiEffectHost(runner, store);
    }

    [Test]
    public async Task NewSession_DelegatesToSessionListViewModel()
    {
        var sessionStore = new FakeSessionStore();
        var sessionManager = new FakeSessionManager();
        var sessions = CreateSessionListViewModel(sessionStore, sessionManager);
        var chat = CreateChatViewModel();
        var codeEditor = CreateCodeEditorViewModel();
        var effects = CreateTuiEffectHost();

        var commands = new AvaloniaWorkspaceCommands(chat, sessions, codeEditor, effects);

        sessionManager.NewSessionResult = new Session(
            "test-id", "proj", "/tmp", "Test", "agent", "model", "provider",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SessionMetadata.Empty);

        commands.NewSession();

        await Task.Delay(50);

        await Assert.That(sessionManager.NewSessionCalled).IsTrue();
    }

    [Test]
    public async Task BranchSession_DelegatesToSessionListViewModel()
    {
        var sessionStore = new FakeSessionStore();
        var sessionManager = new FakeSessionManager();
        var sessions = CreateSessionListViewModel(sessionStore, sessionManager);
        var chat = CreateChatViewModel();
        var codeEditor = CreateCodeEditorViewModel();
        var effects = CreateTuiEffectHost();

        var commands = new AvaloniaWorkspaceCommands(chat, sessions, codeEditor, effects);

        sessionManager.BranchResult = new Session(
            "branch-id", "proj", "/tmp", "Branch", "agent", "model", "provider",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SessionMetadata.Empty);

        commands.BranchSession();

        await Task.Delay(50);

        await Assert.That(sessionManager.BranchCalled).IsTrue();
    }

    [Test]
    public async Task OpenFile_DelegatesToCodeEditorViewModel()
    {
        var toasts = new FakeToastService();
        var codeEditor = CreateCodeEditorViewModel(toasts);
        var chat = CreateChatViewModel();
        var sessions = CreateSessionListViewModel();
        var effects = CreateTuiEffectHost();

        var commands = new AvaloniaWorkspaceCommands(chat, sessions, codeEditor, effects);

        await commands.OpenFileAsync();

        await Assert.That(codeEditor.Tabs).IsEmpty();
    }

    [Test]
    public async Task SaveFile_DelegatesToCodeEditorViewModel()
    {
        var toasts = new FakeToastService();
        var codeEditor = CreateCodeEditorViewModel(toasts);
        var chat = CreateChatViewModel();
        var sessions = CreateSessionListViewModel();
        var effects = CreateTuiEffectHost();

        var commands = new AvaloniaWorkspaceCommands(chat, sessions, codeEditor, effects);

        var tempPath = Path.Combine(Path.GetTempPath(), $"harbor-test-{Guid.NewGuid()}.txt");
        codeEditor.ActiveTab = new EditorTabViewModel(tempPath, "test.txt", "txt", "hello world");

        await commands.SaveFileAsync();

        await Assert.That(toasts.LastMessage).Contains("Saved");
        await Assert.That(File.Exists(tempPath)).IsTrue();
        File.Delete(tempPath);
    }

    [Test]
    public async Task StopAgent_DelegatesToChatViewModel()
    {
        var agentRunner = new FakeAgentRunner();
        var toasts = new FakeToastService();
        var chat = CreateChatViewModel(toasts, agentRunner);
        var sessions = CreateSessionListViewModel();
        var codeEditor = CreateCodeEditorViewModel();
        var effects = CreateTuiEffectHost(agentRunner);

        var commands = new AvaloniaWorkspaceCommands(chat, sessions, codeEditor, effects);

        commands.StopAgent();

        await Task.Delay(50);

        await Assert.That(agentRunner.AbortSource.IsCancellationRequested).IsTrue();
        await Assert.That(toasts.LastMessage).Contains("Abort requested");
    }

    [Test]
    public async Task ClearChat_DelegatesToChatViewModel()
    {
        var chat = CreateChatViewModel();
        var sessions = CreateSessionListViewModel();
        var codeEditor = CreateCodeEditorViewModel();
        var effects = CreateTuiEffectHost();

        var commands = new AvaloniaWorkspaceCommands(chat, sessions, codeEditor, effects);

        commands.ClearChat();

        await Assert.That(chat.Lines).IsEmpty();
        await Assert.That(chat.ToolCalls).IsEmpty();
    }
}
