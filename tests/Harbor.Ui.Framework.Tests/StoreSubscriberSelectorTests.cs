using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Concrete <see cref="StoreSubscriberViewModel" /> exposing protected members for testing.
/// </summary>
public sealed class TestableStoreSubscriberViewModel : StoreSubscriberViewModel
{
    public TestableStoreSubscriberViewModel()
        : base(new TestDispatcherAdapter(), NullLogger.Instance)
    {
    }

    protected override void OnStoreChanged(UiState state)
    {
    }

    public void RegisterSelector<T>(Func<UiState, T> read, Action<T> apply, IEqualityComparer<T>? cmp = null)
        => Select(read, apply, cmp);

    public new void ApplySelectors(UiState state) => base.ApplySelectors(state);

    public new void ResetSelectors() => base.ResetSelectors();
}

/// <summary>
///     Tests for <see cref="StoreSubscriberViewModel" /> selector DistinctUntilChanged behaviour.
/// </summary>
public class StoreSubscriberSelectorTests
{
    [Test]
    public async Task ApplySelectors_InvokesApplyForEachRegisteredSelector()
    {
        var vm = new TestableStoreSubscriberViewModel();
        int countA = 0;
        int countB = 0;

        vm.RegisterSelector(s => s.Ui.ScrollOffset, v => countA++);
        vm.RegisterSelector(s => s.Chat.Status, v => countB++);

        vm.ApplySelectors(new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 5 }, Chat = ChatDomainState.Empty with { Status = "running" } });

        await Assert.That(countA).IsEqualTo(1);
        await Assert.That(countB).IsEqualTo(1);
    }

    [Test]
    public async Task DistinctUntilChanged_SkipsDuplicateValues()
    {
        var vm = new TestableStoreSubscriberViewModel();
        int count = 0;

        vm.RegisterSelector(s => s.Ui.ScrollOffset, v => count++);

        vm.ApplySelectors(new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 5 } });
        vm.ApplySelectors(new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 5 } });

        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task DistinctUntilChanged_AppliesOnChangedValue()
    {
        var vm = new TestableStoreSubscriberViewModel();
        int count = 0;
        int lastValue = -1;

        vm.RegisterSelector(s => s.Ui.ScrollOffset, v =>
        {
            count++;
            lastValue = v;
        });

        vm.ApplySelectors(new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 5 } });
        vm.ApplySelectors(new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 10 } });
        vm.ApplySelectors(new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 10 } });

        await Assert.That(count).IsEqualTo(2);
        await Assert.That(lastValue).IsEqualTo(10);
    }

    [Test]
    public async Task ResetSelectors_ClearsCache_NextApplyAlwaysFires()
    {
        var vm = new TestableStoreSubscriberViewModel();
        int count = 0;

        vm.RegisterSelector(s => s.Ui.ScrollOffset, v => count++);

        vm.ApplySelectors(new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 5 } });
        vm.ResetSelectors();
        vm.ApplySelectors(new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 5 } });

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task Select_AddsSelector_ApplyWorks()
    {
        var vm = new TestableStoreSubscriberViewModel();
        string? lastStatus = null;

        vm.RegisterSelector(s => s.Chat.Status, v => lastStatus = v);

        vm.ApplySelectors(new UiState { Chat = ChatDomainState.Empty with { Status = "idle" } });

        await Assert.That(lastStatus).IsEqualTo("idle");
    }

    [Test]
    public async Task CustomEqualityComparer_Respected()
    {
        var vm = new TestableStoreSubscriberViewModel();
        int count = 0;

        vm.RegisterSelector(
            s => s.Chat.Status,
            v => count++,
            StringComparer.OrdinalIgnoreCase);

        vm.ApplySelectors(new UiState { Chat = ChatDomainState.Empty with { Status = "Running" } });
        vm.ApplySelectors(new UiState { Chat = ChatDomainState.Empty with { Status = "running" } });

        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task MultipleSelectors_IndependentCaches()
    {
        var vm = new TestableStoreSubscriberViewModel();
        int scrollCount = 0;
        int statusCount = 0;

        vm.RegisterSelector(s => s.Ui.ScrollOffset, v => scrollCount++);
        vm.RegisterSelector(s => s.Chat.Status, v => statusCount++);

        vm.ApplySelectors(new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 5 }, Chat = ChatDomainState.Empty with { Status = "running" } });
        vm.ApplySelectors(new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 5 }, Chat = ChatDomainState.Empty with { Status = "idle" } });

        await Assert.That(scrollCount).IsEqualTo(1);
        await Assert.That(statusCount).IsEqualTo(2);
    }
}
