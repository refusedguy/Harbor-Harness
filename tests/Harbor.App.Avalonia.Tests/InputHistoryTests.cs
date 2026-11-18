// InputHistoryTests.cs. Issue #2681, item G02.
//
// The Avalonia composer used to forget every prompt the moment it was sent.
// These tests pin the two halves of the fix: the pure buffer's navigation
// semantics, and the view-model wiring that actually feeds it on Send and
// applies the recalled text on Alt+Up / Alt+Down.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using Harbor.TestKit;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Harbor.App.Avalonia.Tests;

public class InputHistoryTests
{
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
            new NoSessionsManager(),
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
