using Harbor.Abstractions.Dsl;
using Harbor.Ui.Framework.Projection;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2430: the projector resolves panels through an ordered chain of
///     descriptors rather than a type switch over the state.
/// </summary>
public class PanelDescriptorChainTests
{
    private sealed record CounterState(int Count);

    private sealed record UnknownState;

    private sealed class LocalCounterPanel : IPanelDescriptor
    {
        public Type StateType => typeof(CounterState);

        public bool Match(object state) => state is CounterState;

        public object? Build(object state) => $"local:{(CounterState)state}";
    }

    private sealed class GlobalCounterPanel : IPanelDescriptor
    {
        public Type StateType => typeof(CounterState);

        public bool Match(object state) => state is CounterState;

        public object? Build(object state) => "global";
    }

    private sealed class CustomFallbackPanel : IPanelDescriptor
    {
        public Type StateType => typeof(object);

        public bool Match(object state) => true;

        public object? Build(object state) => "custom fallback";
    }

    [Test]
    public async Task LocalRule_WinsOverGlobalRule()
    {
        var registry = new PanelDescriptorRegistry();
        registry.AddGlobal(new GlobalCounterPanel());
        registry.AddLocal(new LocalCounterPanel());
        var projector = new PanelDescriptorProjector(registry);

        await Assert.That(projector.Project(new CounterState(3))).IsEqualTo("local:CounterState { Count = 3 }");
    }

    [Test]
    public async Task GlobalRule_WinsOverFallback()
    {
        var registry = new PanelDescriptorRegistry();
        registry.AddGlobal(new GlobalCounterPanel());
        var projector = new PanelDescriptorProjector(registry);

        await Assert.That(projector.Project(new CounterState(1))).IsEqualTo("global");
    }

    [Test]
    public async Task UnmatchedState_UsesFallbackLast()
    {
        var registry = new PanelDescriptorRegistry();
        registry.AddLocal(new LocalCounterPanel());
        registry.AddGlobal(new GlobalCounterPanel());
        var projector = new PanelDescriptorProjector(registry);

        await Assert.That(projector.Project(new UnknownState()))
            .IsEqualTo("[no panel for UnknownState]");
    }

    [Test]
    public async Task Find_ReturnsLocalBeforeGlobalBeforeFallback()
    {
        var local = new LocalCounterPanel();
        var global = new GlobalCounterPanel();
        var registry = new PanelDescriptorRegistry();
        registry.AddGlobal(global);
        registry.AddLocal(local);

        await Assert.That(ReferenceEquals(registry.Find(new CounterState(1)), local)).IsTrue();
        await Assert.That(ReferenceEquals(registry.Fallback, registry.Find(new UnknownState()))).IsTrue();
    }

    [Test]
    public async Task CustomFallback_IsTriedAfterEveryOtherRule()
    {
        var custom = new CustomFallbackPanel();
        var registry = new PanelDescriptorRegistry(custom);
        registry.AddGlobal(new GlobalCounterPanel());
        var projector = new PanelDescriptorProjector(registry);

        await Assert.That(ReferenceEquals(registry.Find(new UnknownState()), custom)).IsTrue();
        await Assert.That(projector.Project(new CounterState(1))).IsEqualTo("global");
        await Assert.That(projector.Project(new UnknownState())).IsEqualTo("custom fallback");
    }
}
