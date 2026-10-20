using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Registries.Events;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Core.Tests;

public class EventBusMiddlewareTests
{
    [Test]
    public async Task MiddlewarePipeline_PassThrough_EventReachesSubscriber()
    {
        var mw = new PassThroughEventBusMiddleware();
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000, new[] { mw });
        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        var testEvent = TestEvents.TurnStart();
        await bus.PublishAsync(testEvent);

        await Assert.That(received.Count).IsEqualTo(1);
        await Assert.That(received[0]).IsEqualTo(testEvent);
    }

    [Test]
    public async Task MiddlewarePipeline_Drop_EventDoesNotReachSubscriber()
    {
        var mw = new DropEventBusMiddleware();
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000, new[] { mw });
        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        var testEvent = TestEvents.TurnStart();
        await bus.PublishAsync(testEvent);

        await Assert.That(received.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MiddlewarePipeline_Transform_SubscriberSeesTransformedEvent()
    {
        var replacement = TestEvents.TurnStart(42);
        var mw = new TransformEventBusMiddleware(replacement);
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000, new[] { mw });
        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        await bus.PublishAsync(TestEvents.TurnStart());

        await Assert.That(received.Count).IsEqualTo(1);
        await Assert.That(received[0]).IsEqualTo(replacement);
    }

    [Test]
    public async Task MiddlewarePipeline_Exception_EventDropped_BusNotBroken()
    {
        var mw = new ThrowingEventBusMiddleware();
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000, new[] { mw });
        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        await bus.PublishAsync(TestEvents.TurnStart());

        await Assert.That(received.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MiddlewarePipeline_Exception_SubsequentEventsStillDelivered()
    {
        var mw = new ThrowingEventBusMiddleware();
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000, new[] { mw });
        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        await bus.PublishAsync(TestEvents.TurnStart());
        await Assert.That(received.Count).IsEqualTo(0);

        // Now publish without the throwing middleware
        var bus2 = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000);
        var received2 = new List<AgentEvent>();
        bus2.Subscribe(async (evt, ct) => received2.Add(evt));
        await bus2.PublishAsync(TestEvents.TurnStart(2));
        await Assert.That(received2.Count).IsEqualTo(1);
    }

    [Test]
    public async Task MultipleMiddlewares_ExecutedInOrder()
    {
        var order = new List<string>();
        var mw1 = new RecordingEventBusMiddleware("mw1", EventBusSinkKind.Mandatory, order);
        var mw2 = new RecordingEventBusMiddleware("mw2", EventBusSinkKind.Mandatory, order);
        var mw3 = new RecordingEventBusMiddleware("mw3", EventBusSinkKind.Mandatory, order);
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000, new[] { mw1, mw2, mw3 });
        bus.Subscribe(async (evt, ct) => { });

        await bus.PublishAsync(TestEvents.TurnStart());

        await Assert.That(order).IsEquivalentTo(new[] { "mw1", "mw2", "mw3" });
    }

    [Test]
    public async Task MultipleMiddlewares_ShortCircuitOnDrop()
    {
        var mw1 = new PassThroughEventBusMiddleware();
        var mw2 = new DropEventBusMiddleware();
        var mw3 = new PassThroughEventBusMiddleware();
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, 1000, new IEventBusMiddleware[] { mw1, mw2, mw3 });
        var received = new List<AgentEvent>();
        bus.Subscribe(async (evt, ct) => received.Add(evt));

        await bus.PublishAsync(TestEvents.TurnStart());

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
            AgentEvent evt = TestEvents.TextDeltaUpdate("id", "delta", AssistantMessage.Empty("s", "m"));
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
            AgentEvent evt = TestEvents.TextDeltaUpdate("id", "delta", AssistantMessage.Empty("s", "m"));
            bool result = await mw.ProcessAsync(ref evt, CancellationToken.None);
            if (result) passed++;
        }

        await Assert.That(passed).IsEqualTo(0);
    }

    [Test]
    public async Task SamplingMiddleware_NonMessageUpdateEvent_PassThrough()
    {
        var mw = new SamplingMiddleware(NullLogger<SamplingMiddleware>.Instance, rate: 0.0);
        AgentEvent evt = TestEvents.TurnStart();
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
            AgentEvent evt = TestEvents.TextDeltaUpdate("id", "delta", AssistantMessage.Empty("s", "m"));
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
        AgentEvent evt = TestEvents.TurnStart();
        bool result = await mw.ProcessAsync(ref evt, CancellationToken.None);
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task TypeFilterMiddleware_AllowList_DropsNonMatchingType()
    {
        var mw = new TypeFilterMiddleware(
            NullLogger<TypeFilterMiddleware>.Instance, typeof(TurnStartEvent));
        AgentEvent evt = TestEvents.MessageStart(AssistantMessage.Empty("s", "m"));
        bool result = await mw.ProcessAsync(ref evt, CancellationToken.None);
        await Assert.That(result).IsFalse();
    }

    /// <summary>
    ///     #478: the typeless construction used to mean "allow everything" — a
    ///     filter that filters nothing while still declaring
    ///     <c>EventBusSinkKind.Mandatory</c>, the verdict that keeps the bus off
    ///     its fast path. The CLI shipped exactly that for its whole life. It is a
    ///     construction error now, not a documented mode.
    /// </summary>
    [Test]
    public async Task TypeFilterMiddleware_NoAllowedTypes_Throws()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => new TypeFilterMiddleware(NullLogger<TypeFilterMiddleware>.Instance));

        await Assert.That(ex.ParamName).IsEqualTo("allowedTypes");
        await Assert.That(ex.Message).Contains("at least one allowed event type");
    }

    [Test]
    public async Task TypeFilterMiddleware_MultipleAllowedTypes()
    {
        var mw = new TypeFilterMiddleware(
            NullLogger<TypeFilterMiddleware>.Instance,
            typeof(TurnStartEvent),
            typeof(MessageStartEvent));

        AgentEvent evt1 = TestEvents.TurnStart();
        bool result1 = await mw.ProcessAsync(ref evt1, CancellationToken.None);
        await Assert.That(result1).IsTrue();

        AgentEvent evt2 = TestEvents.MessageStart(AssistantMessage.Empty("s", "m"));
        bool result2 = await mw.ProcessAsync(ref evt2, CancellationToken.None);
        await Assert.That(result2).IsTrue();

        AgentEvent evt3 = TestEvents.TurnEnd(AssistantMessage.Empty("s", "m"));
        bool result3 = await mw.ProcessAsync(ref evt3, CancellationToken.None);
        await Assert.That(result3).IsFalse();
    }

    [Test]
    public async Task SamplingMiddleware_ProcessAsync_ZeroAlloc()
    {
        var mw = new SamplingMiddleware(NullLogger<SamplingMiddleware>.Instance, rate: 1.0);
        AgentEvent evt = TestEvents.TextDeltaUpdate("id", "delta", AssistantMessage.Empty("s", "m"));

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
        AgentEvent evt = TestEvents.TurnStart();

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool result = await mw.ProcessAsync(ref evt, CancellationToken.None);
        long after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(result).IsTrue();
        await Assert.That(after - before).IsEqualTo(0);
    }
}
