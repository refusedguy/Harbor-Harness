using Harbor.Abstractions.Events;

namespace Harbor.TestKit;

/// <summary>
///     A middleware that records what it saw and lets every event through.
/// </summary>
/// <remarks>
///     <para>
///         Eleven hand-written middlewares implemented
///         <see cref="IEventBusMiddleware" /> across two files. They differed
///         in whether they declared a sink verdict, whether they counted, and
///         what they answered — and the sink verdict is the part that is easy
///         to get wrong by accident: it is a DEFAULT interface member, so a
///         base class that implements the interface without it gets a
///         compiler-synthesised forwarder, and a derived class that merely
///         declares a same-named member does not re-implement the interface.
///         The forwarder keeps answering the base verdict.
///     </para>
///     <para>
///         Every class here implements the interface DIRECTLY and declares its
///         verdict explicitly, so the trap has nowhere to hide.
///     </para>
/// </remarks>
public class RecordingEventBusMiddleware : IEventBusMiddleware
{
    private readonly List<string>? _order;
    private int _seen;

    /// <summary>
    ///     Creates a recording middleware.
    /// </summary>
    /// <param name="name">The name the bus reports this middleware under.</param>
    /// <param name="sinkKind">The sink verdict this middleware declares.</param>
    /// <param name="order">
    ///     An optional list the middleware appends its name to, in call order.
    ///     A pipeline of these records the ORDER the bus ran them in, which is
    ///     a different fact from how many events each of them saw.
    /// </param>
    public RecordingEventBusMiddleware(
        string name = "recording",
        EventBusSinkKind sinkKind = EventBusSinkKind.Mandatory,
        List<string>? order = null)
    {
        Name = name;
        SinkKind = sinkKind;
        _order = order;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public EventBusSinkKind SinkKind { get; }

    /// <summary>How many events this middleware was asked about.</summary>
    public int Seen => Volatile.Read(ref _seen);

    /// <inheritdoc />
    /// <remarks>
    ///     Allocation-free on purpose: the bus drains OPTIONAL sinks on its
    ///     fast path, and a middleware that allocated there would put an
    ///     allocation back into a path that is measured to be zero. So the
    ///     counter is an interlocked increment and nothing else happens — the
    ///     order list is opt-in, because a caller that asks for it has already
    ///     accepted an allocation.
    /// </remarks>
    public virtual ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _seen);
        _order?.Add(Name);
        return ValueTask.FromResult(true);
    }
}

/// <summary>
///     A middleware that declares NO sink verdict, so the interface's default
///     answers for it.
/// </summary>
/// <remarks>
///     The default is <see cref="EventBusSinkKind.Mandatory" /> — the
///     conservative direction: a sink that has not thought about it can never
///     be bypassed by accident. A middleware that DOES declare a verdict
///     cannot stand in for this one, because then the test would be proving
///     that the declaration works rather than that the default does.
/// </remarks>
public sealed class UndeclaredSinkEventBusMiddleware : IEventBusMiddleware
{
    /// <inheritdoc />
    public string Name => "undeclared";

    /// <inheritdoc />
    public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default)
        => ValueTask.FromResult(true);
}

/// <summary>
///     A middleware that only finishes once the gate it was given does.
/// </summary>
/// <remarks>
///     The shape of a real async sink: the pipeline must not proceed until
///     the sink has done its work, and the completion cannot be an
///     <c>async</c> method — CS1988 forbids an async method from declaring
///     <c>ref</c>/<c>out</c> parameters, and the interface passes the event by
///     reference. The await therefore lives in a separate helper.
/// </remarks>
public sealed class GatedEventBusMiddleware : IEventBusMiddleware
{
    private readonly Task _gate;
    private readonly Lock _gateLock = new();

    /// <summary>
    ///     Creates a gated middleware.
    /// </summary>
    /// <param name="gate">The task that must complete before the pipeline continues.</param>
    /// <param name="name">The name the bus reports this middleware under.</param>
    /// <param name="sinkKind">The sink verdict this middleware declares.</param>
    public GatedEventBusMiddleware(
        Task gate,
        string name = "gated",
        EventBusSinkKind sinkKind = EventBusSinkKind.Optional)
    {
        _gate = gate;
        Name = name;
        SinkKind = sinkKind;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public EventBusSinkKind SinkKind { get; }

    /// <summary>How many events this middleware was asked about.</summary>
    public int Seen { get; private set; }

    /// <inheritdoc />
    public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default)
        => new(CountAfterGateAsync());

    private async Task<bool> CountAfterGateAsync()
    {
        await _gate.ConfigureAwait(false);
        lock (_gateLock)
        {
            Seen++;
        }

        return true;
    }
}

/// <summary>A middleware that drops every event it is asked about.</summary>
public sealed class DropEventBusMiddleware : IEventBusMiddleware
{
    /// <summary>
    ///     Creates a dropping middleware.
    /// </summary>
    /// <param name="name">The name the bus reports this middleware under.</param>
    /// <param name="sinkKind">The sink verdict this middleware declares.</param>
    public DropEventBusMiddleware(
        string name = "drop",
        EventBusSinkKind sinkKind = EventBusSinkKind.Mandatory)
    {
        Name = name;
        SinkKind = sinkKind;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public EventBusSinkKind SinkKind { get; }

    /// <inheritdoc />
    public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default)
        => ValueTask.FromResult(false);
}

/// <summary>A middleware that lets every event through and does nothing else.</summary>
public sealed class PassThroughEventBusMiddleware : IEventBusMiddleware
{
    /// <summary>
    ///     Creates a pass-through middleware.
    /// </summary>
    /// <param name="name">The name the bus reports this middleware under.</param>
    /// <param name="sinkKind">The sink verdict this middleware declares.</param>
    public PassThroughEventBusMiddleware(
        string name = "pass-through",
        EventBusSinkKind sinkKind = EventBusSinkKind.Mandatory)
    {
        Name = name;
        SinkKind = sinkKind;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public EventBusSinkKind SinkKind { get; }

    /// <inheritdoc />
    public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default)
        => ValueTask.FromResult(true);
}

/// <summary>A middleware that replaces every event it is asked about.</summary>
public sealed class TransformEventBusMiddleware : IEventBusMiddleware
{
    private readonly AgentEvent _replacement;

    /// <summary>
    ///     Creates a transforming middleware.
    /// </summary>
    /// <param name="replacement">The event to put in place of the original.</param>
    /// <param name="name">The name the bus reports this middleware under.</param>
    /// <param name="sinkKind">The sink verdict this middleware declares.</param>
    public TransformEventBusMiddleware(
        AgentEvent replacement,
        string name = "transform",
        EventBusSinkKind sinkKind = EventBusSinkKind.Mandatory)
    {
        _replacement = replacement;
        Name = name;
        SinkKind = sinkKind;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public EventBusSinkKind SinkKind { get; }

    /// <inheritdoc />
    public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default)
    {
        @event = _replacement;
        return ValueTask.FromResult(true);
    }
}

/// <summary>A middleware that throws, to prove the bus survives a faulty sink.</summary>
public sealed class ThrowingEventBusMiddleware : IEventBusMiddleware
{
    /// <summary>
    ///     Creates a throwing middleware.
    /// </summary>
    /// <param name="name">The name the bus reports this middleware under.</param>
    /// <param name="sinkKind">The sink verdict this middleware declares.</param>
    public ThrowingEventBusMiddleware(
        string name = "throwing",
        EventBusSinkKind sinkKind = EventBusSinkKind.Mandatory)
    {
        Name = name;
        SinkKind = sinkKind;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public EventBusSinkKind SinkKind { get; }

    /// <inheritdoc />
    public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default)
        => throw new InvalidOperationException("boom");
}
