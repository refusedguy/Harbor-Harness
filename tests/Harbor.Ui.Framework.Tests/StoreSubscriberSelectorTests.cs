using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.ViewModels;
using Harbor.TestKit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Tests for <see cref="StoreSubscriberViewModel" /> selector DistinctUntilChanged behaviour.
/// </summary>
/// <remarks>
///     The double this file used to carry — a
///     <c>TestableStoreSubscriberViewModel</c> — moved to
///     <c>Harbor.TestKit</c> in #1549 as
///     <see cref="CountingStoreSubscriberStub" />: the same counting shape,
///     the same exposed selector API, one copy for the whole repository.
/// </remarks>
public class StoreSubscriberSelectorTests
{
    [Test]
    public async Task ApplySelectors_InvokesApplyForEachRegisteredSelector()
    {
        var vm = new CountingStoreSubscriberStub();
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
        var vm = new CountingStoreSubscriberStub();
        int count = 0;

        vm.RegisterSelector(s => s.Ui.ScrollOffset, v => count++);

        vm.ApplySelectors(new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 5 } });
        vm.ApplySelectors(new UiState { Ui = TerminalUiState.Empty with { ScrollOffset = 5 } });

        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task DistinctUntilChanged_AppliesOnChangedValue()
    {
        var vm = new CountingStoreSubscriberStub();
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
        var vm = new CountingStoreSubscriberStub();
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
        var vm = new CountingStoreSubscriberStub();
        string? lastStatus = null;

        vm.RegisterSelector(s => s.Chat.Status, v => lastStatus = v);

        vm.ApplySelectors(new UiState { Chat = ChatDomainState.Empty with { Status = "idle" } });

        await Assert.That(lastStatus).IsEqualTo("idle");
    }

    [Test]
    public async Task CustomEqualityComparer_Respected()
    {
        var vm = new CountingStoreSubscriberStub();
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
        var vm = new CountingStoreSubscriberStub();
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
