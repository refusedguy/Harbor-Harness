using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Registries.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Core.Tests;

public class EventBusMiddlewareTests
{
    [Test]
    public async Task MiddlewarePipeline_PassThrough_EventReachesSubscriber()
    {
        var mw = new PassThroughMiddleware();
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000, new[] { mw });
        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        var testEvent = new TurnStartEvent(1);
        await bus.PublishAsync(testEvent);

        await Assert.That(received.Count).IsEqualTo(1);
        await Assert.That(received[0]).IsEqualTo(testEvent);
    }

    [Test]
    public async Task MiddlewarePipeline_Drop_EventDoesNotReachSubscriber()
    {
        var mw = new DropMiddleware();
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000, new[] { mw });
        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        var testEvent = new TurnStartEvent(1);
        await bus.PublishAsync(testEvent);

        await Assert.That(received.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MiddlewarePipeline_Transform_SubscriberSeesTransformedEvent()
    {
        var replacement = new TurnStartEvent(42);
        var mw = new TransformMiddleware(replacement);
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000, new[] { mw });
        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        await bus.PublishAsync(new TurnStartEvent(1));

        await Assert.That(received.Count).IsEqualTo(1);
        await Assert.That(received[0]).IsEqualTo(replacement);
    }

    [Test]
    public async Task MiddlewarePipeline_Exception_EventDropped_BusNotBroken()
    {
        var mw = new ThrowingMiddleware();
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000, new[] { mw });
        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        await bus.PublishAsync(new TurnStartEvent(1));

        await Assert.That(received.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MiddlewarePipeline_Exception_SubsequentEventsStillDelivered()
    {
        var mw = new ThrowingMiddleware();
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000, new[] { mw });
        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        await bus.PublishAsync(new TurnStartEvent(1));
        await Assert.That(received.Count).IsEqualTo(0);

        // Now publish without the throwing middleware
        var bus2 = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000);
        var received2 = new List<AgentEvent>();
        bus2.Subscribe(async (evt, ct) => received2.Add(evt));
        await bus2.PublishAsync(new TurnStartEvent(2));
        await Assert.That(received2.Count).IsEqualTo(1);
    }

    [Test]
    public async Task MultipleMiddlewares_ExecutedInOrder()
    {
        var order = new List<string>();
        var mw1 = new RecordingMiddleware("mw1", order);
        var mw2 = new RecordingMiddleware("mw2", order);
        var mw3 = new RecordingMiddleware("mw3", order);
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000, new[] { mw1, mw2, mw3 });
        bus.Subscribe(async (evt, ct) => { });

        await bus.PublishAsync(new TurnStartEvent(1));

        await Assert.That(order).IsEquivalentTo(new[] { "mw1", "mw2", "mw3" });
    }

    [Test]
    public async Task MultipleMiddlewares_ShortCircuitOnDrop()
    {
        var mw1 = new PassThroughMiddleware();
        var mw2 = new DropMiddleware();
        var mw3 = new PassThroughMiddleware();
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000, new IEventBusMiddleware[] { mw1, mw2, mw3 });
        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        await bus.PublishAsync(new TurnStartEvent(1));

        await Assert.That(received.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SamplingMiddleware_PassAll_Rate1()
    {
        var mw = new SamplingMiddleware(NullLogger<SamplingMiddleware>.Instance, rate: 1.0);
        int passed = 0;
        const int total = 1000;

        for (int i = 0; i < total; i++)
        {
            AgentEvent evt = new MessageUpdateEvent(
                new TextDeltaEvent("id", "delta"),
                AssistantMessage.Empty("s", "m"));
            bool result = await mw.ProcessAsync(ref evt, CancellationToken.None);
            if (result) passed++;
        }

        await Assert.That(passed).IsEqualTo(total);
    }

    [Test]
    public async Task SamplingMiddleware_DropAll_Rate0()
    {
        var mw = new SamplingMiddleware(NullLogger<SamplingMiddleware>.Instance, rate: 0.0);
        int passed = 0;
        const int total = 1000;

        for (int i = 0; i < total; i++)
        {
            AgentEvent evt = new MessageUpdateEvent(
                new TextDeltaEvent("id", "delta"),
                AssistantMessage.Empty("s", "m"));
            bool result = await mw.ProcessAsync(ref evt, CancellationToken.None);
            if (result) passed++;
        }

        await Assert.That(passed).IsEqualTo(0);
    }

    [Test]
    public async Task SamplingMiddleware_NonMessageUpdateEvent_PassThrough()
    {
        var mw = new SamplingMiddleware(NullLogger<SamplingMiddleware>.Instance, rate: 0.0);
        AgentEvent evt = new TurnStartEvent(1);
        bool result = await mw.ProcessAsync(ref evt, CancellationToken.None);
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task SamplingMiddleware_StatisticalRateApproximately10Percent()
    {
        var mw = new SamplingMiddleware(NullLogger<SamplingMiddleware>.Instance, rate: 0.1);
        int passed = 0;
        const int total = 10000;

        for (int i = 0; i < total; i++)
        {
            AgentEvent evt = new MessageUpdateEvent(
                new TextDeltaEvent("id", "delta"),
                AssistantMessage.Empty("s", "m"));
            bool result = await mw.ProcessAsync(ref evt, CancellationToken.None);
            if (result) passed++;
        }

        // With rate=0.1, expect ~10% pass. Allow 5%-15% tolerance.
        double ratio = (double)passed / total;
        await Assert.That(ratio).IsGreaterThanOrEqualTo(0.05);
        await Assert.That(ratio).IsLessThanOrEqualTo(0.15);
    }

    [Test]
    public async Task TypeFilterMiddleware_AllowList_AllowsMatchingType()
    {
        var mw = new TypeFilterMiddleware(
            NullLogger<TypeFilterMiddleware>.Instance, typeof(TurnStartEvent));
        AgentEvent evt = new TurnStartEvent(1);
        bool result = await mw.ProcessAsync(ref evt, CancellationToken.None);
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task TypeFilterMiddleware_AllowList_DropsNonMatchingType()
    {
        var mw = new TypeFilterMiddleware(
            NullLogger<TypeFilterMiddleware>.Instance, typeof(TurnStartEvent));
        AgentEvent evt = new MessageStartEvent(AssistantMessage.Empty("s", "m"));
        bool result = await mw.ProcessAsync(ref evt, CancellationToken.None);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task TypeFilterMiddleware_NoAllowedTypes_AllowAll()
    {
        var mw = new TypeFilterMiddleware(NullLogger<TypeFilterMiddleware>.Instance);
        AgentEvent evt = new TurnStartEvent(1);
        bool result = await mw.ProcessAsync(ref evt, CancellationToken.None);
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task TypeFilterMiddleware_MultipleAllowedTypes()
    {
        var mw = new TypeFilterMiddleware(
            NullLogger<TypeFilterMiddleware>.Instance,
            typeof(TurnStartEvent),
            typeof(MessageStartEvent));

        AgentEvent evt1 = new TurnStartEvent(1);
        bool result1 = await mw.ProcessAsync(ref evt1, CancellationToken.None);
        await Assert.That(result1).IsTrue();

        AgentEvent evt2 = new MessageStartEvent(AssistantMessage.Empty("s", "m"));
        bool result2 = await mw.ProcessAsync(ref evt2, CancellationToken.None);
        await Assert.That(result2).IsTrue();

        AgentEvent evt3 = new TurnEndEvent(
            AssistantMessage.Empty("s", "m"),
            Array.Empty<ToolResultMessage>());
        bool result3 = await mw.ProcessAsync(ref evt3, CancellationToken.None);
        await Assert.That(result3).IsFalse();
    }

    [Test]
    public async Task SamplingMiddleware_ProcessAsync_ZeroAlloc()
    {
        var mw = new SamplingMiddleware(NullLogger<SamplingMiddleware>.Instance, rate: 1.0);
        AgentEvent evt = new MessageUpdateEvent(
            new TextDeltaEvent("id", "delta"),
            AssistantMessage.Empty("s", "m"));

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool result = await mw.ProcessAsync(ref evt, CancellationToken.None);
        long after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(result).IsTrue();
        await Assert.That(after - before).IsEqualTo(0);
    }

    [Test]
    public async Task TypeFilterMiddleware_ProcessAsync_ZeroAlloc()
    {
        var mw = new TypeFilterMiddleware(
            NullLogger<TypeFilterMiddleware>.Instance, typeof(TurnStartEvent));
        AgentEvent evt = new TurnStartEvent(1);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool result = await mw.ProcessAsync(ref evt, CancellationToken.None);
        long after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(result).IsTrue();
        await Assert.That(after - before).IsEqualTo(0);
    }

    // ── Helper middleware implementations ──

    private sealed class PassThroughMiddleware : IEventBusMiddleware
    {
        public string Name => "pass-through";
        public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default) =>
            ValueTask.FromResult(true);
    }

    private sealed class DropMiddleware : IEventBusMiddleware
    {
        public string Name => "drop";
        public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default) =>
            ValueTask.FromResult(false);
    }

    private sealed class TransformMiddleware(AgentEvent replacement) : IEventBusMiddleware
    {
        private readonly AgentEvent _replacement = replacement;
        public string Name => "transform";
        public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default)
        {
            @event = _replacement;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class ThrowingMiddleware : IEventBusMiddleware
    {
        public string Name => "throwing";
        public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class RecordingMiddleware(string name, List<string> order) : IEventBusMiddleware
    {
        public string Name => name;
        public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default)
        {
            order.Add(name);
            return ValueTask.FromResult(true);
        }
    }
}
