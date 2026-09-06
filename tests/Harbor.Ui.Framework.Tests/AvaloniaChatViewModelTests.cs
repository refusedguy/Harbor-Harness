using System.Collections.Immutable;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Testable <see cref="StoreSubscriberViewModel" /> that tracks
///     <see cref="StoreSubscriberViewModel.OnStoreChanged" /> and
///     <see cref="StoreSubscriberViewModel.OnAfterSelectorsApplied" /> calls
///     for assertions.
/// </summary>
public sealed class TestableHookTrackingViewModel : StoreSubscriberViewModel
{
    public TestableHookTrackingViewModel()
        : base(new TestDispatcherAdapter(), NullLogger.Instance)
    {
    }

    public List<UiState> OnStoreChangedStates { get; } = new();
    public List<UiState> OnAfterSelectorsAppliedStates { get; } = new();
    public int OnStoreChangedCount { get; private set; }
    public int OnAfterSelectorsAppliedCount { get; private set; }

    protected override void OnStoreChanged(UiState state)
    {
        OnStoreChangedCount++;
        OnStoreChangedStates.Add(state);
    }

    protected override void OnAfterSelectorsApplied(UiState state)
    {
        OnAfterSelectorsAppliedCount++;
        OnAfterSelectorsAppliedStates.Add(state);
    }

    public void SimulateStateChange(UiState state)
    {
        ((TestDispatcherAdapter)Dispatcher).Raise(state);
    }
}

/// <summary>
///     Records the exact invocation order of
///     <see cref="StoreSubscriberViewModel.OnStoreChanged" /> and
///     <see cref="StoreSubscriberViewModel.OnAfterSelectorsApplied" />.
/// </summary>
public sealed class TrackingOrderViewModel : StoreSubscriberViewModel
{
    public List<string> CallOrder { get; } = new();

    public TrackingOrderViewModel()
        : base(new TestDispatcherAdapter(), NullLogger.Instance)
    {
    }

    protected override void OnStoreChanged(UiState state)
    {
        CallOrder.Add("OnStoreChanged");
    }

    protected override void OnAfterSelectorsApplied(UiState state)
    {
        CallOrder.Add("OnAfterSelectorsApplied");
    }

    public void SimulateStateChange(UiState state)
    {
        ((TestDispatcherAdapter)Dispatcher).Raise(state);
    }
}

/// <summary>
///     Applies selectors inside <see cref="OnStoreChanged" /> then tracks
///     whether <see cref="OnAfterSelectorsApplied" /> runs afterwards.
/// </summary>
public sealed class SelectorTrackingViewModel : StoreSubscriberViewModel
{
    public bool SelectorApplied { get; private set; }
    public bool AfterHookCalled { get; private set; }

    public SelectorTrackingViewModel()
        : base(new TestDispatcherAdapter(), NullLogger.Instance)
    {
    }

    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
    }

    protected override void OnAfterSelectorsApplied(UiState state)
    {
        AfterHookCalled = true;
    }

    public void RegisterStatusSelector()
    {
        Select(state => state.Status, v => SelectorApplied = true);
    }

    public void SimulateStateChange(UiState state)
    {
        ((TestDispatcherAdapter)Dispatcher).Raise(state);
    }
}

/// <summary>
///     Tests verifying that <see cref="StoreSubscriberViewModel.OnAfterSelectorsApplied" />
///     is invoked after state changes, with the correct state and in the correct order
///     relative to <see cref="StoreSubscriberViewModel.OnStoreChanged" />.
/// </summary>
public class AvaloniaChatViewModelHookTests
{
    [Test]
    public async Task OnAfterSelectorsApplied_IsCalled_WhenStateChanges()
    {
        var vm = new TestableHookTrackingViewModel();
        var state = new UiState { Status = "running" };

        vm.SimulateStateChange(state);

        await Assert.That(vm.OnAfterSelectorsAppliedCount).IsEqualTo(1);
    }

    [Test]
    public async Task OnAfterSelectorsApplied_ReceivesCorrectState()
    {
        var vm = new TestableHookTrackingViewModel();
        var state = new UiState
        {
            Status = "streaming",
            IsAgentRunning = true,
            IsStreaming = true,
            Lines = ImmutableArray.Create(
                new ChatLine(ChatRole.User, "Hello"),
                new ChatLine(ChatRole.Assistant, "World"))
        };

        vm.SimulateStateChange(state);

        await Assert.That(vm.OnAfterSelectorsAppliedStates).HasCount(1);
        await Assert.That(vm.OnAfterSelectorsAppliedStates[0].Status).IsEqualTo("streaming");
        await Assert.That(vm.OnAfterSelectorsAppliedStates[0].IsAgentRunning).IsTrue();
        await Assert.That(vm.OnAfterSelectorsAppliedStates[0].IsStreaming).IsTrue();
        await Assert.That(vm.OnAfterSelectorsAppliedStates[0].Lines.Length).IsEqualTo(2);
    }

    [Test]
    public async Task OnAfterSelectorsApplied_CalledAfterOnStoreChanged()
    {
        var vm = new TrackingOrderViewModel();
        var state = new UiState { Status = "idle" };

        vm.SimulateStateChange(state);

        await Assert.That(vm.CallOrder).IsEquivalentTo(new[] { "OnStoreChanged", "OnAfterSelectorsApplied" });
    }

    [Test]
    public async Task OnAfterSelectorsApplied_CalledOnEveryStateChange()
    {
        var vm = new TestableHookTrackingViewModel();

        vm.SimulateStateChange(new UiState { Status = "idle" });
        vm.SimulateStateChange(new UiState { Status = "running" });
        vm.SimulateStateChange(new UiState { Status = "streaming" });

        await Assert.That(vm.OnAfterSelectorsAppliedCount).IsEqualTo(3);
        await Assert.That(vm.OnAfterSelectorsAppliedStates.Select(s => s.Status))
            .IsEquivalentTo(new[] { "idle", "running", "streaming" });
    }

    [Test]
    public async Task OnAfterSelectorsApplied_CalledAfterSelectors_WhenUsingSelect()
    {
        var vm = new SelectorTrackingViewModel();
        vm.RegisterStatusSelector();

        vm.SimulateStateChange(new UiState { Status = "running" });

        await Assert.That(vm.SelectorApplied).IsTrue();
        await Assert.That(vm.AfterHookCalled).IsTrue();
    }
}
