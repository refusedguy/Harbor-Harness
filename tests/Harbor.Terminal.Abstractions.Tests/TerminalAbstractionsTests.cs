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
        await Assert.That((int)TuiViewPlacement.StatusBar).IsEqualTo(0);
        await Assert.That((int)TuiViewPlacement.ChatHistory).IsEqualTo(1);
        await Assert.That((int)TuiViewPlacement.Input).IsEqualTo(2);
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

        public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
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
        renderer.Dispose();
        await Assert.That(true).IsTrue();
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
        await Assert.That(true).IsTrue();
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
