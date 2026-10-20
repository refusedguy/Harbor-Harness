using Harbor.Abstractions.Extensions;

namespace Harbor.Application.Agents;

/// <summary>
///     Coalesces the per-delta <see cref="MessageUpdateEvent" /> publishes into
///     batched updates (#1527).
/// </summary>
/// <remarks>
///     <para>
///         The turn runner used to publish one update per streamed delta: one
///         <see cref="MessageUpdateEvent" /> allocation plus one bus dispatch
///         per token, on a path whose consumers redraw at frame rate. This
///         class buffers the deltas of a RUN (a maximal sequence of deltas of
///         the same kind and content-block id) and publishes them as one
///         merged delta when the <see cref="UpdateFlushPolicy" /> says so.
///     </para>
///     <para>
///         <b>THE NO-LOSS INVARIANT.</b> A buffered delta is never dropped,
///         only merged, and the merge is lossless: the published delta's text
///         is the concatenation of the buffered deltas' texts, in order, and
///         the published event keeps the run's kind and id. Every consumer in
///         the tree ACCUMULATES deltas (they append the text to a buffer), so
///         a merged delta renders exactly like the deltas it replaces.
///     </para>
///     <para>
///         The invariant has two halves, and both are load-bearing:
///         <list type="number">
///             <item>
///                 <b>The tail.</b> The pending batch is flushed before EVERY
///                 event that follows it — a change of delta kind, a tool-call
///                 event, the step finish, a stream error — and once more
///                 before the turn's <see cref="MessageEndEvent" />. The last
///                 delta of a turn is therefore always published; the turn's
///                 end event never overtakes it.
///             </item>
///             <item>
///                 <b>The order.</b> The batch is published BEFORE the event
///                 that ends the run, so the wire order of the updates is the
///                 order of the deltas. A merged delta never jumps the queue
///                 of the event that terminated its run.
///             </item>
///         </list>
///     </para>
///     <para>
///         <b>Why the first delta of a run is published alone.</b> A run's
///         first token must never wait for a batch to fill: on a slow stream
///         (one token every few hundred milliseconds) a batch that waits for
///         32 deltas would hold the first token for half a minute. The first
///         delta of a run therefore goes out on its own, and the batching
///         applies to the rest of the run.
///     </para>
///     <para>
///         <b>Why tool-call fragments are not merged.</b> A tool-call delta
///         carries the id of the call it belongs to, and two calls' fragments
///         can interleave; a merged fragment would have to pick one id and
///         would then attribute the other's bytes to it. They are published as
///         they arrive.
///     </para>
///     <para>
///         One instance per turn; the caller owns the lifetime and must
///         dispose it (it returns a pooled builder).
///     </para>
/// </remarks>
internal sealed class UpdateCoalescer : IDisposable
{
    /// <summary>What kind of delta the pending batch holds.</summary>
    private enum BatchKind
    {
        /// <summary>No pending batch.</summary>
        None,

        /// <summary>A text delta batch.</summary>
        Text,

        /// <summary>A thinking delta batch.</summary>
        Thinking,
    }

    private readonly IEventBus _bus;
    private readonly TimeProvider _clock;
    private readonly UpdateFlushPolicy _policy;
    private readonly StringBuilderPool.PooledStringBuilder _buffer;

    private BatchKind _kind;
    private string _id = string.Empty;
    private int _deltaCount;
    private int _charCount;

    /// <summary>
    ///     The partial the pending batch carries — captured when the batch
    ///     started, so the published event reports the same message state the
    ///     individual deltas would have reported.
    /// </summary>
    private AssistantMessage _partial = null!;

    /// <summary>
    ///     Whether the CURRENT run has published at least one batch. Reset when
    ///     the run changes (a different delta kind or content-block id), so
    ///     every run publishes its first delta alone.
    /// </summary>
    private bool _runPublished;

    /// <summary>When the last batch was published — the interval's zero point.</summary>
    private long _lastPublishTicks;

    private bool _disposed;

    /// <summary>
    ///     Construct a coalescer.
    /// </summary>
    /// <param name="bus">The bus the batched updates are published on.</param>
    /// <param name="policy">The flush policy.</param>
    /// <param name="clock">The clock the interval bound is measured on.</param>
    public UpdateCoalescer(IEventBus bus, UpdateFlushPolicy policy, TimeProvider clock)
    {
        _bus = bus;
        _policy = policy;
        _clock = clock;
        _buffer = StringBuilderPool.Rent(1024);
        _lastPublishTicks = clock.GetTimestamp();
    }

    /// <summary>
    ///     Buffer a text delta; publish the pending batch when the policy says
    ///     so.
    /// </summary>
    public Task AppendTextAsync(TextDeltaEvent delta, AssistantMessage partial, CancellationToken ct) =>
        AppendAsync(BatchKind.Text, delta.Id, delta.Delta, partial, ct);

    /// <summary>
    ///     Buffer a thinking delta; publish the pending batch when the policy
    ///     says so.
    /// </summary>
    public Task AppendThinkingAsync(ThinkingDeltaEvent delta, AssistantMessage partial, CancellationToken ct) =>
        AppendAsync(BatchKind.Thinking, delta.Id, delta.Delta, partial, ct);

    /// <summary>
    ///     Publish the pending batch, if there is one.
    /// </summary>
    /// <remarks>
    ///     This is the tail guarantee's seam: the turn runner calls it before
    ///     every event that follows the batch and once more before the turn's
    ///     end event, so a buffered delta can never be overtaken — or lost.
    /// </remarks>
    public Task FlushAsync(CancellationToken ct) =>
        _deltaCount == 0 ? Task.CompletedTask : FlushPendingAsync(ct);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _buffer.Dispose();
    }

    /// <summary>
    ///     Buffer one delta and decide whether the batch goes out now.
    /// </summary>
    private async Task AppendAsync(BatchKind kind, string id, string text, AssistantMessage partial, CancellationToken ct)
    {
        if (_kind != kind || !string.Equals(_id, id, StringComparison.Ordinal))
        {
            // A different delta kind (or a different content-block id) ends the
            // run: the pending batch goes out FIRST, so the published order is
            // the order of the deltas.
            await FlushPendingAsync(ct).ConfigureAwait(false);
            _kind = kind;
            _id = id;
            _runPublished = false;
        }

        _partial = partial;
        _buffer.Builder.Append(text);
        _deltaCount++;
        _charCount += text.Length;

        if (!_runPublished)
        {
            // The first delta of a run goes out alone: a run's first token must
            // never wait for a batch to fill.
            _runPublished = true;
            await FlushPendingAsync(ct).ConfigureAwait(false);
            return;
        }

        if (_deltaCount >= _policy.MaxDeltas || _charCount >= _policy.MaxChars || IntervalElapsed())
        {
            await FlushPendingAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Whether the batch has waited longer than the policy's interval
    ///     since the last publish.
    /// </summary>
    private bool IntervalElapsed()
    {
        if (_policy.MaxInterval is not { } interval)
        {
            return false;
        }

        return _clock.GetElapsedTime(_lastPublishTicks, _clock.GetTimestamp()) >= interval;
    }

    /// <summary>
    ///     Publish the pending batch as one merged delta.
    /// </summary>
    private async Task FlushPendingAsync(CancellationToken ct)
    {
        if (_deltaCount == 0)
        {
            return;
        }

        // One copy per batch: the merged text is the only string the publish
        // path allocates, and it replaces one event allocation plus one bus
        // dispatch per buffered delta.
        string text = _buffer.ToString();
        LlmEvent merged = _kind switch
        {
            BatchKind.Text => new TextDeltaEvent(_id, text),
            BatchKind.Thinking => new ThinkingDeltaEvent(_id, text),
            _ => throw new InvalidOperationException(
                $"A pending batch always has a kind (text or thinking); got '{_kind}'."),
        };

        _buffer.Builder.Clear();
        _deltaCount = 0;
        _charCount = 0;
        _lastPublishTicks = _clock.GetTimestamp();

        await _bus.PublishAsync(new MessageUpdateEvent(merged, _partial), ct).ConfigureAwait(false);
    }
}
