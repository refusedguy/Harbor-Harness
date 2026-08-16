using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Immutable;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Test double for <see cref="IDispatcherAdapter" /> — no-ops so tests stay synchronous.
/// </summary>
internal sealed class TestDispatcherAdapter : IDispatcherAdapter
{
    public event EventHandler<UiState>? StateChanged;

    public void Post(Action action) => action();

    public T Invoke<T>(Func<T> func) => func();

    public void Bind(UiStore store)
    {
    }

    public void Unbind(UiStore store)
    {
    }
}

/// <summary>
///     Concrete <see cref="ChatViewModelBase" /> exposing protected members for testing.
/// </summary>
public class TestChatViewModel : ChatViewModelBase
{
    public TestChatViewModel(IDispatcherAdapter dispatcher, ILogger logger)
        : base(dispatcher, logger)
    {
    }

    public int OnStoreChangedCallCount { get; private set; }

    protected override void OnStoreChanged(UiState state)
    {
        OnStoreChangedCallCount++;
        base.OnStoreChanged(state);
    }

    public void TriggerStoreChanged(UiState state) => OnStoreChanged(state);

    public void RegisterSelector<T>(
        Func<UiState, T> read,
        Action<T> apply,
        IEqualityComparer<T>? cmp = null)
        => Select(read, apply, cmp);
}

/// <summary>
///     Tests for <see cref="ChatViewModelBase" /> selector projections.
/// </summary>
public class ChatViewModelBaseTests
{
    [Test]
    public async Task Constructor_DeclaresExpectedSelectors()
    {
        var dispatcher = new TestDispatcherAdapter();
        var vm = new TestChatViewModel(dispatcher, NullLogger.Instance);

        var state = new UiState
        {
            IsStreaming = true,
            IsAgentRunning = true,
            Active = new ActiveMessage("hello", string.Empty),
            Input = new InputModel("world", ImmutableArray<string>.Empty, -1)
        };

        vm.ApplySelectors(state);

        await Assert.That(vm.IsStreaming).IsTrue();
        await Assert.That(vm.IsAgentRunning).IsTrue();
        await Assert.That(vm.StreamingBuffer).IsEqualTo("hello");
        await Assert.That(vm.InputText).IsEqualTo("world");
        await Assert.That(vm.IsThinking).IsFalse();
        await Assert.That(vm.StatusMessage).IsEqualTo("Streaming response…");
    }

    [Test]
    public async Task ApplySelectors_UpdatesProperties_WhenStateChanges()
    {
        var dispatcher = new TestDispatcherAdapter();
        var vm = new TestChatViewModel(dispatcher, NullLogger.Instance);

        vm.ApplySelectors(new UiState { IsAgentRunning = false, IsStreaming = false });
        await Assert.That(vm.IsAgentRunning).IsFalse();
        await Assert.That(vm.StatusMessage).IsEqualTo("Idle");

        vm.ApplySelectors(new UiState { IsAgentRunning = true, IsStreaming = false });
        await Assert.That(vm.IsAgentRunning).IsTrue();
        await Assert.That(vm.StatusMessage).IsEqualTo("Agent is running…");
    }

    [Test]
    public async Task DistinctUntilChanged_SkipsDuplicateValues()
    {
        var dispatcher = new TestDispatcherAdapter();
        var vm = new TestChatViewModel(dispatcher, NullLogger.Instance);

        int applyCount = 0;
        vm.RegisterSelector(s => s.IsStreaming, v => applyCount++);

        vm.ApplySelectors(new UiState { IsStreaming = true });
        vm.ApplySelectors(new UiState { IsStreaming = true });

        await Assert.That(applyCount).IsEqualTo(1);
    }

    [Test]
    public async Task DistinctUntilChanged_AppliesOnChangedValue()
    {
        var dispatcher = new TestDispatcherAdapter();
        var vm = new TestChatViewModel(dispatcher, NullLogger.Instance);

        int applyCount = 0;
        bool lastValue = false;
        vm.RegisterSelector(s => s.IsStreaming, v =>
        {
            applyCount++;
            lastValue = v;
        });

        vm.ApplySelectors(new UiState { IsStreaming = false });
        vm.ApplySelectors(new UiState { IsStreaming = true });
        vm.ApplySelectors(new UiState { IsStreaming = true });

        await Assert.That(applyCount).IsEqualTo(2);
        await Assert.That(lastValue).IsTrue();
    }

    [Test]
    public async Task OnStoreChanged_CallsApplySelectors()
    {
        var dispatcher = new TestDispatcherAdapter();
        var vm = new TestChatViewModel(dispatcher, NullLogger.Instance);

        var state = new UiState
        {
            IsStreaming = true,
            IsAgentRunning = true,
            Active = new ActiveMessage("test", string.Empty),
            Input = new InputModel("input", ImmutableArray<string>.Empty, -1)
        };

        vm.TriggerStoreChanged(state);

        await Assert.That(vm.OnStoreChangedCallCount).IsEqualTo(1);
        await Assert.That(vm.IsStreaming).IsTrue();
        await Assert.That(vm.StreamingBuffer).IsEqualTo("test");
        await Assert.That(vm.InputText).IsEqualTo("input");
    }

    [Test]
    public async Task StateChanged_Event_TriggersSelectors()
    {
        var dispatcher = new TestDispatcherAdapter();
        var vm = new TestChatViewModel(dispatcher, NullLogger.Instance);

        var state = new UiState { IsStreaming = true, IsAgentRunning = false };
        dispatcher.StateChanged?.Invoke(dispatcher, state);

        await Assert.That(vm.IsStreaming).IsTrue();
        await Assert.That(vm.OnStoreChangedCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Selectors_ProjectDerivedValues()
    {
        var dispatcher = new TestDispatcherAdapter();
        var vm = new TestChatViewModel(dispatcher, NullLogger.Instance);

        vm.ApplySelectors(new UiState { IsAgentRunning = true, IsStreaming = false });
        await Assert.That(vm.IsThinking).IsTrue();
        await Assert.That(vm.StatusMessage).IsEqualTo("Agent is running…");

        vm.ApplySelectors(new UiState { IsAgentRunning = true, IsStreaming = true });
        await Assert.That(vm.IsThinking).IsFalse();
        await Assert.That(vm.StatusMessage).IsEqualTo("Streaming response…");

        vm.ApplySelectors(new UiState { IsAgentRunning = false, IsStreaming = false });
        await Assert.That(vm.IsThinking).IsFalse();
        await Assert.That(vm.StatusMessage).IsEqualTo("Idle");
    }

    [Test]
    public async Task StreamingBuffer_UsesTextBufferOrEmpty()
    {
        var dispatcher = new TestDispatcherAdapter();
        var vm = new TestChatViewModel(dispatcher, NullLogger.Instance);

        vm.ApplySelectors(new UiState { Active = new ActiveMessage("buffer", string.Empty) });
        await Assert.That(vm.StreamingBuffer).IsEqualTo("buffer");

        vm.ApplySelectors(new UiState { Active = ActiveMessage.Empty });
        await Assert.That(vm.StreamingBuffer).IsEqualTo(string.Empty);
    }
}
