using Harbor.Abstractions.Events;
using TUnit.Assertions;
using Harbor.Registries.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Registries.Tests;

/// <summary>
///     Pins the <b>publish-return shape</b> of <see cref="InMemoryEventBus.PublishAsync" />
///     — the mechanical half of the #47/S4 decision recorded in
///     <c>docs/EVENT_TOPOLOGY.md</c> §7.6.
/// </summary>
/// <remarks>
///     <para>
///         #47/S4 was opened on one premise: <i>"the subscriber path awaits fan-out,
///         so <c>Task</c>-returning <c>PublishAsync</c> boxes/completes a state machine
///         per call"</i>, with the fix implied to be a <c>ValueTask</c> return shape on
///         <see cref="IEventBus" />. Measuring first (the issue's own acceptance
///         criterion #2: "no redesign without that evidence") shows the premise does not
///         hold, in three parts.
///     </para>
///     <para>
///         <b>1. There is no state machine at the publish call at all.</b>
///         <see cref="InMemoryEventBus.PublishAsync" /> is <i>not</i> an <c>async</c>
///         method (<c>InMemoryEventBus.cs:398</c>): it is a plain method with three
///         returns — the cached <c>Task.CompletedTask</c> (fast path, :437),
///         <c>DrainOptionalSinksAsync</c> (:462) and <c>PublishSlowAsync</c> (:504).
///         A state machine has to exist before it can be boxed.
///     </para>
///     <para>
///         <b>2. Nothing on the synchronous path suspends, so nothing boxes.</b> For a
///         subscriber that returns an already-completed <c>ValueTask</c> — the common
///         real case — <c>DispatchToOneAsync</c> takes its
///         <c>dispatch.IsCompletedSuccessfully</c> branch (<c>InMemoryEventBus.cs:1137</c>)
///         and returns without awaiting a real suspension. An <c>async Task</c> method
///         that completes without ever suspending stays a struct on the stack and
///         returns the builder's cached completed task. This is what these tests pin,
///         and <b>not</b> the presence or absence of an <c>async</c> keyword: a
///         synchronous <c>async Task</c> and a plain method returning a cached task are
///         indistinguishable here, because in both cases there is genuinely nothing to
///         catch.
///     </para>
///     <para>
///         <b>3. The bytes that are left are not the interface's.</b> The measured
///         residue on the 1-subscriber path is <b>200 B/publish</b>, attributed
///         exhaustively in <c>docs/BENCHMARKS.md</c> §5.4.3: 80 B from
///         <c>Task&lt;ValueTuple&lt;bool, AgentEvent&gt;&gt;</c>
///         (<c>RunMiddlewareAsync</c>, <c>InMemoryEventBus.cs:926</c>), 80 B
///         <i>per subscriber</i> from
///         <c>Task&lt;ValueTuple&lt;DispatchOutcome, Task?&gt;&gt;</c>
///         (<c>DispatchToOneAsync</c>, :1122), and 40 B from the fan-out method's own
///         <c>Task</c> (<c>DispatchToSubscribersAsync</c>, :973). All three are
///         <i>private</i> method results. Re-shaping <see cref="IEventBus" /> cannot
///         reach any of them; the same three signatures re-shaped in place would.
///     </para>
///     <para>
///         <b>What these tests are for.</b> #47/S4's premise is falsifiable, so it is
///         enforced rather than written down: if a future change makes the synchronous
///         publish suspend — a <c>Task.Run</c>, a <c>Task.Yield()</c>, a genuinely
///         asynchronous sink on the fan-out path — the publish starts returning a fresh
///         <c>Task</c> per call, and the cost that #152, #391 and #47/S3 spent five
///         slices removing comes back silently. The last case is the deliberate
///         positive control for exactly that transition, so the assertion below is
///         known to have teeth rather than being a tautology that passes by
///         construction.
///     </para>
/// </remarks>
public class EventBusPublishShapeTests
{
    /// <summary>
    ///     A subscriber that is already done — the common real case, and the one the
    ///     <c>PublishAsync_1Sub</c> / <c>PublishAsync_1Sub_ScrollbackOff</c> rows of
    ///     <c>tests/Harbor.Benchmarks/EventBusBenchmark.cs</c> measure.
    /// </summary>
    private static readonly AgentEvent Event = new TurnStartEvent(1);

    /// <summary>
    ///     Fast path (no subscriber, no sink, unarmed ring): the return is the
    ///     <c>Task.CompletedTask</c> literal at <c>InMemoryEventBus.cs:437</c>, so two
    ///     publishes hand back the <em>same</em> instance — proof that the call
    ///     allocates no task of its own.
    /// </summary>
    [Test]
    public async Task PublishAsync_FastPath_ReturnsOneSharedTaskInstance()
    {
        var bus = new InMemoryEventBus(maxScrollback: 0);

        Task first = bus.PublishAsync(Event);
        Task second = bus.PublishAsync(Event);
        await first;
        await second;

        await Assert.That(ReferenceEquals(first, second)).IsTrue()
            .Because("the fast path returns the cached Task.CompletedTask literal, not a per-call task");
    }

    /// <summary>
    ///     The subscriber path, with the fan-out actually running. Same shared-instance
    ///     claim, and — the half that stops this from being an identity tautology — the
    ///     subscriber is asserted to have run: "one cached task" must never be bought by
    ///     skipping delivery. This is contract <b>G5</b>
    ///     (<c>docs/EVENT_TOPOLOGY.md</c> §4) restated at the return-shape level.
    /// </summary>
    /// <param name="handlerBudget">
    ///     Both shapes the product builds. The production default arms a per-handler
    ///     budget (so the dispatch takes the <c>IsCompletedSuccessfully</c> branch and
    ///     never arms a timer), and <c>TimeSpan.Zero</c> disables the budget and awaits
    ///     the handler directly. Neither may allocate a task for the publish itself.
    /// </param>
    [Test]
    [Arguments(250)]
    [Arguments(0)]
    public async Task PublishAsync_SynchronousSubscriber_ReturnsOneSharedTaskInstanceAndDelivers(int handlerBudgetMs)
    {
        int delivered = 0;
        var bus = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance,
            maxScrollback: 0,
            handlerBudget: TimeSpan.FromMilliseconds(handlerBudgetMs));
        bus.Subscribe((_, _) =>
        {
            delivered++;
            return ValueTask.CompletedTask;
        });

        Task first = bus.PublishAsync(Event);
        Task second = bus.PublishAsync(Event);
        await first;
        await second;

        await Assert.That(ReferenceEquals(first, second)).IsTrue()
            .Because("a publish whose handler completes synchronously must not allocate a Task for itself");
        await Assert.That(delivered).IsEqualTo(2)
            .Because("sharing one Task is only legitimate while the fan-out still runs to completion (G5)");
    }

    /// <summary>
    ///     The positive control, and the reason the two cases above can be trusted.
    ///     <para>
    ///         A subscriber that genuinely suspends makes the publish genuinely
    ///         asynchronous, the state machine boxes, and each publish returns its
    ///         <i>own</i> task. That is the #47/S4 premise realised — so the shared
    ///         instance above is a real discriminator and not a property that holds for
    ///         every possible publish. <c>Task.Yield()</c> always suspends (it posts to
    ///         the current scheduler), so this case is deterministic rather than a race:
    ///         if it ever stopped being distinct, the control would be the thing that
    ///         goes red, not the claim.
    ///     </para>
    /// </summary>
    [Test]
    public async Task PublishAsync_SuspendingSubscriber_ReturnsAPerCallTask()
    {
        var bus = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance,
            maxScrollback: 0,
            handlerBudget: TimeSpan.FromMilliseconds(InMemoryEventBus.DefaultHandlerBudget.TotalMilliseconds));
        bus.Subscribe(static async (_, _) => await Task.Yield());

        Task first = bus.PublishAsync(Event);
        Task second = bus.PublishAsync(Event);
        await first;
        await second;

        await Assert.That(ReferenceEquals(first, second)).IsFalse()
            .Because("a publish that really suspends MUST allocate a per-call Task — otherwise the shared-instance assertions above prove nothing");
    }
}