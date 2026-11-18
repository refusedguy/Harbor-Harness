// InputHistoryTests.cs. Issue #2681, item G02.
//
// The Avalonia composer used to forget every prompt the moment it was sent.
// These tests pin the two halves of the fix: the pure buffer's navigation
// semantics, and the view-model wiring that actually feeds it on Send and
// applies the recalled text on Alt+Up / Alt+Down.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.State;
using Harbor.TestKit;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Harbor.App.Avalonia.Tests;

public class InputHistoryTests
{
    private sealed class FakeSessionManager : ISessionManager
    {
        public Session? Active { get; set; }
        public SessionContext? ActiveContext { get; set; }
        public SessionContext? GetContext(string sessionId) => null;
        public SessionStatus GetStatus(string sessionId) => SessionStatus.Idle;
        public void SetStatus(string sessionId, SessionStatus status) { }
        public void NotifyMessageCount(string sessionId, int count) { }
        public GitSessionInfo GetGitInfo(string sessionId) => new(null, false, 0, null);
        public void RefreshGitInfo(string sessionId, string directory) { }
        public Task EnsureDefaultSessionAsync() => Task.CompletedTask;
        public Task RebindFromCommonConfigAsync() => Task.CompletedTask;
        public Task<Result<Session>> NewSessionAsync(string? agentName = null, string? providerId = null,
            string? modelId = null, string? workingDirectory = null) => Task.FromResult(Result.Failure<Session>("unused"));
        public Task<bool> OpenSessionAsync(string sessionId) => Task.FromResult(true);
        public Task<bool> OpenPanelSessionAsync(string sessionId) => OpenSessionAsync(sessionId);
        public Task<Result<Session>> BranchActiveAsync() => Task.FromResult(Result.Failure<Session>("unused"));
        public Task<bool> DeleteSessionAsync(string sessionId) => Task.FromResult(true);
        public Task<bool> RenameSessionAsync(string sessionId, string newTitle) => Task.FromResult(true);
        public event Action<string, SessionStatus>? StatusChanged;
        public event Action<string, int>? MessageCountChanged;
        public string? GetDirectory(string sessionId) => null;
        public string? GetStatusText(string sessionId) => null;
        public string? GetBranch(string sessionId) => null;
        public bool GetIsDirty(string sessionId) => false;
        public bool? GetIsSubagent(string sessionId) => null;
    }

    private sealed class FakeAgentRunner : IAgentRunner
    {
        public CancellationToken AbortToken => CancellationToken.None;
        public void RequestAbort() { }
        public Task<Result> PromptAsync(string text, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ResetAbortSource() { }
    }

    private sealed class FakeLogger<T> : ILogger<T>, ILogger
    {
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { }
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable BeginScope<TState>(TState state) => null!;
    }

    private static ChatViewModel CreateVm()
    {
        var store = new UiStore();
        return new ChatViewModel(
            store,
            new TuiEffectHost(new FakeAgentRunner(), store),
            new FakeSessionManager(),
            new FakeSessionStore(),
            new TestDispatcherAdapter(),
            new FakeLogger<ChatViewModel>(),
            new SilentToastService(),
            new UiRenderEngine(new DefaultUiProjector(), new AvaloniaUiViewport(), new ChatStreamingPresenter()));
    }

    [Test]
    public async Task Add_StoresTrimmedPrompts_AndSuppressesBlanksAndRepeats()
    {
        var history = new InputHistoryBuffer();

        history.Add("   ");
        history.Add("  first\n");
        history.Add("second");
        history.Add("second");

        await Assert.That(history.Entries).IsEquivalentTo(new[] { "first", "second" });
        await Assert.That(history.Cursor).IsEqualTo(-1);
    }

    [Test]
    public async Task UpWalksNewestFirst_AndDownRestoresTheDraft()
    {
        var history = new InputHistoryBuffer();
        history.Add("old");
        history.Add("new");

        await Assert.That(history.TryNavigateUp("half typed", out string first)).IsTrue();
        await Assert.That(first).IsEqualTo("new");
        await Assert.That(history.TryNavigateUp(first, out string second)).IsTrue();
        await Assert.That(second).IsEqualTo("old");
        await Assert.That(history.TryNavigateUp(second, out string clamped)).IsTrue();
        await Assert.That(clamped).IsEqualTo("old");

        await Assert.That(history.TryNavigateDown(out string newer)).IsTrue();
        await Assert.That(newer).IsEqualTo("new");
        await Assert.That(history.TryNavigateDown(out string draft)).IsTrue();
        await Assert.That(draft).IsEqualTo("half typed");
        await Assert.That(history.Cursor).IsEqualTo(-1);
    }

    [Test]
    public async Task HistoryKeys_AreNoOps_WhenNothingWasSent()
    {
        var history = new InputHistoryBuffer();

        await Assert.That(history.TryNavigateUp("unsent", out string up)).IsFalse();
        await Assert.That(up).IsEqualTo("unsent");
        await Assert.That(history.TryNavigateDown(out string down)).IsFalse();
        await Assert.That(down).IsEmpty();
    }

    [Test]
    public async Task Composer_SendRecordsPrompt_AndNavigationReplacesInputText()
    {
        var vm = CreateVm();

        vm.InputText = "first prompt";
        vm.SendCommand.Execute(null);
        await Assert.That(vm.InputHistory).IsEquivalentTo(new[] { "first prompt" });
        await Assert.That(vm.InputText).IsEmpty();

        vm.InputText = "half typed";
        vm.NavigateInputHistoryUp();
        await Assert.That(vm.InputText).IsEqualTo("first prompt");

        vm.NavigateInputHistoryDown();
        await Assert.That(vm.InputText).IsEqualTo("half typed");
    }
}
