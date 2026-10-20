using System.Collections.Immutable;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.ViewModels;
using Harbor.TestKit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Records the exact invocation order of
///     <see cref="StoreSubscriberViewModel.OnStoreChanged" /> and
///     <see cref="StoreSubscriberViewModel.OnAfterSelectorsApplied" />.
/// </summary>
/// <remarks>
///     The counting base this used to carry its own copy of —
///     <c>TestableHookTrackingViewModel</c> — moved to
///     <c>Harbor.TestKit</c> in #1549 as
///     <see cref="CountingStoreSubscriberStub" />; the two doubles below are
///     the test-specific specialisations of it that stayed here.
/// </remarks>
public sealed class TrackingOrderViewModel : CountingStoreSubscriberStub
{
    public List<string> CallOrder { get; } = new();

    protected override void OnStoreChanged(UiState state)
    {
        CallOrder.Add("OnStoreChanged");
        base.OnStoreChanged(state);
    }

    protected override void OnAfterSelectorsApplied(UiState state)
    {
        CallOrder.Add("OnAfterSelectorsApplied");
        base.OnAfterSelectorsApplied(state);
    }
}

/// <summary>
///     Applies selectors inside <see cref="OnStoreChanged" /> then tracks
///     whether <see cref="OnAfterSelectorsApplied" /> runs afterwards.
/// </summary>
public sealed class SelectorTrackingViewModel : CountingStoreSubscriberStub
{
    public bool SelectorApplied { get; private set; }
    public bool AfterHookCalled { get; private set; }

    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
        base.OnStoreChanged(state);
    }

    protected override void OnAfterSelectorsApplied(UiState state)
    {
        AfterHookCalled = true;
        base.OnAfterSelectorsApplied(state);
    }

    public void RegisterStatusSelector()
    {
        Select(state => state.Chat.Status, v => SelectorApplied = true);
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
        var vm = new CountingStoreSubscriberStub();
        var state = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Status = "running"
            }
        };

        vm.SimulateStateChange(state);

        await Assert.That(vm.OnAfterSelectorsAppliedCount).IsEqualTo(1);
    }

    [Test]
    public async Task OnAfterSelectorsApplied_ReceivesCorrectState()
    {
        var vm = new CountingStoreSubscriberStub();
        var state = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Status = "streaming",
                IsAgentRunning = true,
                IsStreaming = true,
                Lines = ImmutableArray.Create(
                new ChatLine(ChatRole.User, "Hello"),
                new ChatLine(ChatRole.Assistant, "World"))
            }
        };

        vm.SimulateStateChange(state);

        await Assert.That(vm.OnAfterSelectorsAppliedStates).Count().IsEqualTo(1);
        await Assert.That(vm.OnAfterSelectorsAppliedStates[0].Chat.Status).IsEqualTo("streaming");
        await Assert.That(vm.OnAfterSelectorsAppliedStates[0].Chat.IsAgentRunning).IsTrue();
        await Assert.That(vm.OnAfterSelectorsAppliedStates[0].Chat.IsStreaming).IsTrue();
        await Assert.That(vm.OnAfterSelectorsAppliedStates[0].Chat.Lines.Length).IsEqualTo(2);
    }

    [Test]
    public async Task OnAfterSelectorsApplied_CalledAfterOnStoreChanged()
    {
        var vm = new TrackingOrderViewModel();
        var state = new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Status = "idle"
            }
        };

        vm.SimulateStateChange(state);

        await Assert.That(vm.CallOrder).IsEquivalentTo(new[] { "OnStoreChanged", "OnAfterSelectorsApplied" });
    }

    [Test]
    public async Task OnAfterSelectorsApplied_CalledOnEveryStateChange()
    {
        var vm = new CountingStoreSubscriberStub();

        vm.SimulateStateChange(new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Status = "idle"
            }
        });
        vm.SimulateStateChange(new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Status = "running"
            }
        });
        vm.SimulateStateChange(new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Status = "streaming"
            }
        });

        await Assert.That(vm.OnAfterSelectorsAppliedCount).IsEqualTo(3);
        await Assert.That(vm.OnAfterSelectorsAppliedStates.Select(s => s.Chat.Status))
            .IsEquivalentTo(new[] { "idle", "running", "streaming" });
    }

    [Test]
    public async Task OnAfterSelectorsApplied_CalledAfterSelectors_WhenUsingSelect()
    {
        var vm = new SelectorTrackingViewModel();
        vm.RegisterStatusSelector();

        vm.SimulateStateChange(new UiState
        {
            Chat = ChatDomainState.Empty with
            {
                Status = "running"
            }
        });

        await Assert.That(vm.SelectorApplied).IsTrue();
        await Assert.That(vm.AfterHookCalled).IsTrue();
    }
}
