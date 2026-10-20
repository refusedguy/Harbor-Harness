using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Terminal.Abstractions.Views;
using Microsoft.Extensions.Logging.Abstractions;
using System.ComponentModel;
using TUnit.Assertions;

namespace Harbor.Terminal.Abstractions.Tests;

public class TuiViewPlacementTests
{
    [Test]
    public async Task TuiViewPlacement_AllExpectedValues_Exist()
    {
        var values = Enum.GetValues<TuiViewPlacement>();
        await Assert.That(values).Contains(TuiViewPlacement.StatusBar);
        await Assert.That(values).Contains(TuiViewPlacement.ChatHistory);
        await Assert.That(values).Contains(TuiViewPlacement.Input);
        await Assert.That(values).Contains(TuiViewPlacement.Footer);
        await Assert.That(values).Contains(TuiViewPlacement.Overlay);
        await Assert.That(values).Contains(TuiViewPlacement.SidebarRight);
        await Assert.That(values).Contains(TuiViewPlacement.SidebarLeft);
        await Assert.That(values.Length).IsEqualTo(7);
    }

    [Test]
    public async Task TuiViewPlacement_CanCastToInt()
    {
        // The placement's NUMERIC value is the contract, not an accident:
        // ViewRegistry's frozen snapshot indexes a per-placement slot array
        // with `int slot = (int)placement`, so the values have to stay
        // contiguous from zero. Pinning them is the whole point of the test,
        // and a change to an enum member recompiles these assertions to a
        // different number, so the test fails and the change is noticed.
        //
        // TUnit's TUnitAssertions0005 reports an Assert.That whose first
        // argument is a constant, and an enum member cast to int is one. The
        // constant IS the subject here, so the finding is waived at the call
        // site rather than papered over.
#pragma warning disable TUnitAssertions0005 // the placement's numeric value is the contract (ViewRegistry indexes slots with it)
        await Assert.That((int)TuiViewPlacement.StatusBar).IsEqualTo(0);
        await Assert.That((int)TuiViewPlacement.ChatHistory).IsEqualTo(1);
        await Assert.That((int)TuiViewPlacement.Input).IsEqualTo(2);
#pragma warning restore TUnitAssertions0005
    }
}

public class ViewRegistryTests
{
    [Test]
    public async Task ViewRegistry_RegisterAndRetrieve_Works()
    {
        var registry = new ViewRegistry();
        var view = new StubTuiView("test-view");
        registry.Register(view);
        await Assert.That(registry.Get("test-view")).IsSameReferenceAs(view);
    }

    [Test]
    public async Task ViewRegistry_GetMissing_ReturnsNull()
    {
        var registry = new ViewRegistry();
        await Assert.That(registry.Get("nonexistent")).IsNull();
    }

    [Test]
    public async Task ViewRegistry_GetAll_ReturnsRegisteredViews()
    {
        var registry = new ViewRegistry();
        registry.Register(new StubTuiView("v1"));
        registry.Register(new StubTuiView("v2"));
        await Assert.That(registry.GetAll().Count).IsEqualTo(2);
    }

    [Test]
    public async Task ViewRegistry_Freeze_AllowsRetrieval()
    {
        var registry = new ViewRegistry();
        registry.Register(new StubTuiView("v1"));
        registry.Freeze();
        await Assert.That(registry.Get("v1") is not null).IsTrue();
    }
}

public class ViewModelRegistryTests
{
    [Test]
    public async Task ViewModelRegistry_RegisterAndRetrieve_Works()
    {
        var registry = new ViewModelRegistry();
        var vm = new StubTuiViewModel("test-vm");
        registry.Register(vm);
        await Assert.That(registry.Get("test-vm")).IsSameReferenceAs(vm);
    }

    [Test]
    public async Task ViewModelRegistry_GetAll_ReturnsRegisteredViewModels()
    {
        var registry = new ViewModelRegistry();
        registry.Register(new StubTuiViewModel("vm1"));
        registry.Register(new StubTuiViewModel("vm2"));
        await Assert.That(registry.GetAll().Count).IsEqualTo(2);
    }
}

public class BaseTuiRendererTests
{
    private sealed class ConcreteTuiRenderer : BaseTuiRenderer
    {
        public ConcreteTuiRenderer() : base(NullLogger.Instance) { }

        public override ITuiRenderContext Context => throw new NotImplementedException();

        public override Task<Maybe<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
            => throw new NotImplementedException();

        public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
            => throw new NotImplementedException();

        public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public override Task<Result> ClearAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    [Test]
    public async Task BaseTuiRenderer_CanBeSubclassed()
    {
        var renderer = new ConcreteTuiRenderer();
        await Assert.That(renderer).IsNotNull();
    }

    [Test]
    public async Task BaseTuiRenderer_InitializeAsync_RegistersBuiltinViews()
    {
        var renderer = new ConcreteTuiRenderer();
        var result = await renderer.InitializeAsync();
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(renderer.Views.GetAll().Count).IsGreaterThan(0);
    }

    [Test]
    public async Task BaseTuiRenderer_RegistersDefaultViewModels()
    {
        var renderer = new ConcreteTuiRenderer();
        await Assert.That(renderer.ViewModels.GetAll().Count).IsGreaterThan(0);
    }

    [Test]
    public async Task BaseTuiRenderer_Dispose_DoesNotThrow()
    {
        var renderer = new ConcreteTuiRenderer();
        // "DoesNotThrow" is the claim, so the assertion is that the dispose
        // does not throw. The trailing `Assert.That(true).IsTrue()` asserted
        // nothing at all (TUnit's TUnitAssertions0005 says so) and could not
        // fail, so it was decoration on a call that could throw.
        await Assert.That(() => renderer.Dispose()).ThrowsNothing();
    }
}

public class ITuiViewModelContractTests
{
    [Test]
    public async Task ITuiViewModel_ImplementingClass_ExposesRequiredMembers()
    {
        var vm = new StatusBarViewModel();
        await Assert.That(vm.Id).IsEqualTo("status-bar");
    }

    [Test]
    public async Task ITuiViewModel_UpdateFromEventAsync_HandlesAgentStart()
    {
        var vm = new StatusBarViewModel();
        var @event = new AgentStartEvent("test-session", [], null);
        await vm.UpdateFromEventAsync(@event, CancellationToken.None);
        // "HandlesAgentStart" is the claim, so the assertion is what the
        // handler DID: the status bar leaves "idle" for "running". The
        // trailing `Assert.That(true).IsTrue()` asserted nothing (TUnit's
        // TUnitAssertions0005 says so), so the handler could have ignored the
        // event entirely and the test would still have passed.
        await Assert.That(vm.Status).IsEqualTo("running");
    }
}

// Stub implementations for testing contracts
file sealed class StubTuiView(string id) : ITuiView
{
    public string Id { get; } = id;
    public string DisplayName { get; } = id;
    public TuiViewPlacement Placement { get; } = TuiViewPlacement.ChatHistory;
    public ITuiViewModel? ViewModel { get; set; }

    public Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task OnEventAsync(AgentEvent @event, CancellationToken ct = default)
        => Task.CompletedTask;

    public void Dispose() { }
}

file sealed class StubTuiViewModel(string id) : ITuiViewModel
{
    public string Id { get; } = id;
    public string DisplayName { get; } = id;
    public event PropertyChangedEventHandler? PropertyChanged;

    public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default)
        => Task.CompletedTask;
}
