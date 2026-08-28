using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Streaming;

/// <summary>
/// One streaming assistant message rendered inline: deltas accumulate in a
/// <see cref="ChunkedBuffer"/> (O(1) push, no string concatenation per token);
/// materialization into the synced text happens only when the shared
/// <see cref="StreamingSync"/> flush policy demands it. Completed lines move
/// into a paced reveal queue driven by <see cref="CommitTickPacer"/>.
/// </summary>
public sealed class StreamBlock
{
    private readonly CommitTickPacer _pacer = new();
    private readonly Queue<QueuedLine> _queue = new();
    private ChunkedBuffer _pending = ChunkedBuffer.Empty;
    private string _synced = string.Empty;
    private long _nowMs;
    private int _scanFrom;

    /// <summary>Injects monotonic time for deterministic tests.</summary>
    public StreamBlock(long initialNowMs = 0) => _nowMs = initialNowMs;

    private readonly record struct QueuedLine(string Text, long EnqueuedAtMs, bool NewlineTerminated);

    /// <summary>Text materialized so far (revealed lines + partial tail).</summary>
    public string SyncedText => _synced;

    /// <summary>Char cursor just past everything revealed (the partial-tail start).</summary>
    public int RevealedChars { get; private set; }

    public int PendingLength => _pending.Length;

    /// <summary>Lines already handed to the renderer via <see cref="Tick"/>.</summary>
    public int LinesConsumed { get; private set; }

    /// <summary>Lines queued but not yet revealed.</summary>
    public int QueuedDepth => _queue.Count;

    public bool IsFinalized { get; private set; }

    // ── Input side ─────────────────────────────────────────────────────────

    public void AppendDelta(string delta)
    {
        if (IsFinalized || string.IsNullOrEmpty(delta))
        {
            return;
        }

        _pending = _pending.Append(delta);
        if (StreamingSync.ShouldFlush(_synced.Length, _pending.Length))
        {
            MaterializePending();
        }
    }

    /// <summary>Flushes everything still pending; no more deltas accepted.</summary>
    public void Complete()
    {
        MaterializePending();

        // Everything after the last newline becomes a final unterminated line.
        while (_synced.IndexOf('\n', _scanFrom) is >= 0 and var idx)
        {
            _queue.Enqueue(new QueuedLine(_synced.Substring(_scanFrom, idx - _scanFrom), _nowMs, true));
            _scanFrom = idx + 1;
        }

        if (_scanFrom < _synced.Length)
        {
            _queue.Enqueue(new QueuedLine(_synced[_scanFrom..], _nowMs, false));
            _scanFrom = _synced.Length;
        }

        IsFinalized = true;
    }

    // ── Tick side ──────────────────────────────────────────────────────────

    /// <summary>Advances time and reveals queued lines according to the pacer
    /// plan (Single = one per tick, BatchAll = everything held).</summary>
    public IReadOnlyList<string> Tick(long nowMs)
    {
        _nowMs = nowMs;

        // Newly completed lines join the queue with their arrival timestamp.
        while (_synced.IndexOf('\n', _scanFrom) is >= 0 and var idx)
        {
            _queue.Enqueue(new QueuedLine(_synced.Substring(_scanFrom, idx - _scanFrom), _nowMs, true));
            _scanFrom = idx + 1;
        }

        var revealed = new List<string>();
        if (_queue.Count > 0)
        {
            var oldest = _queue.Peek().EnqueuedAtMs;
            var snapshot = new QueueSnapshot(_queue.Count, TimeSpan.FromMilliseconds(nowMs - oldest));
            var plan = IsFinalized ? DrainPlanKind.BatchAll : _pacer.Decide(snapshot, nowMs);
            int take = plan == DrainPlanKind.BatchAll ? _queue.Count : 1;
            for (int i = 0; i < take; i++)
            {
                var line = _queue.Dequeue();
                LinesConsumed++;
                RevealedChars += line.Text.Length + (line.NewlineTerminated ? 1 : 0);
                revealed.Add(line.Text);
            }
        }

        return revealed;
    }

    /// <summary>The not-yet-revealed tail (empty once everything was revealed).</summary>
    public ReadOnlySpan<char> PartialTail()
    {
        int start = Math.Min(RevealedChars, _synced.Length);
        return _synced.AsSpan(start);
    }

    private void MaterializePending()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        _synced = StreamingSync.Concat(_synced, _pending);
        _pending = ChunkedBuffer.Empty;
    }
}
