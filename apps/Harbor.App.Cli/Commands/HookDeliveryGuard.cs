namespace Harbor.App.Cli.Commands;

/// <summary>
///     Delivery policy for hook-triggered automation under #2667. A result is
///     forwarded at most once (dedupe on the session/tool-call pair) and at
///     most <see cref="MaxPerMinute"/> times in any sliding one-minute window.
///     Every decision uses a caller-supplied timestamp: the guard reads no
///     clock, touches no I/O and uses no reflection, so it stays AOT-safe and
///     can be pinned by deterministic tests. This is the receiving-half
///     counterpart of the G11 <c>WebhookSignature</c> proof and deliberately
///     stands apart from the sending half.
/// </summary>
internal sealed class HookDeliveryGuard
{
    /// <summary>Rate ceiling for one sliding minute, per #2667.</summary>
    internal const int MaxPerMinute = 60;

    /// <summary>
    ///     Upper bound on remembered results so a long-lived daemon does not
    ///     grow without limit. The oldest tracked result is evicted first.
    /// </summary>
    internal const int MaxTrackedResults = 4096;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly HashSet<ResultKey> _seen = new();
    private readonly Queue<ResultKey> _seenOrder = new();
    private readonly Queue<DateTimeOffset> _hits = new();

    /// <summary>
    ///     Decide whether a hook delivery should wake the session. Returns
    ///     <c>true</c> only for a result not seen before while the sliding
    ///     window still has capacity; a duplicate or an over-rate delivery is
    ///     dropped.
    /// </summary>
    internal bool TryAccept(string sessionId, string toolCallId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var key = new ResultKey(sessionId, toolCallId);
            if (_seen.Contains(key))
                return false;

            while (_hits.Count > 0 && now - _hits.Peek() >= Window)
                _hits.Dequeue();
            if (_hits.Count >= MaxPerMinute)
                return false;

            _seen.Add(key);
            _seenOrder.Enqueue(key);
            if (_seenOrder.Count > MaxTrackedResults)
                _seen.Remove(_seenOrder.Dequeue());

            _hits.Enqueue(now);
            return true;
        }
    }

    /// <summary>Identity of one automation result: the #2667 dedupe key.</summary>
    internal readonly record struct ResultKey(string SessionId, string ToolCallId);
}
