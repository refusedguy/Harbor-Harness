namespace Harbor.Ui.Framework.Rendering.Markdown;

/// <summary>
///     Thread-safe frozen-tail markdown cache (renderer-unification sprint
///     Phase 6.4): completed markdown blocks are stored as immutable
///     <see cref="Cell"/> snapshots keyed by block id, so re-rendering a
///     finished block is an O(1) restore instead of a re-parse + re-style.
/// </summary>
/// <remarks>
///     <para>
///         <b>Contract</b> (see <c>MarkdownRenderPerformanceContract</c>):
///         capacity-bounded (LRU eviction at <see cref="DefaultCapacity"/>,
///         override via constructor) so the cache cannot grow unbounded on
///         long documents; restores are a single array copy (&lt;1 ms per
///         block, enforced by benchmark).
///     </para>
///     <para>
///         <b>Observable:</b> <see cref="BlockFrozen"/> lets other backends
///         invalidate their own derived state when a block freezes (e.g. a
///         SpectreTui panel dropping a precomputed widget).
///     </para>
/// </remarks>
public sealed class FrozenTailMarkdownCache
{
    /// <summary>Default maximum number of retained frozen blocks.</summary>
    public const int DefaultCapacity = 500;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<int, Cell[]> _blocks;
    private readonly LinkedList<int> _lru; // MRU at front

    public FrozenTailMarkdownCache(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least 1.");
        }

        _capacity = capacity;
        _blocks = new Dictionary<int, Cell[]>(capacity);
        _lru = new LinkedList<int>();
    }

    /// <summary>Raised when a block freezes (or re-freezes) — after the snapshot is stored.</summary>
    public event EventHandler<BlockFrozenEventArgs>? BlockFrozen;

    /// <summary>Number of retained frozen blocks.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _blocks.Count;
            }
        }
    }

    /// <summary>
    ///     Attempts an O(1) restore of a frozen block. Touches the LRU order.
    /// </summary>
    public bool TryGet(int blockId, out Cell[] snapshot)
    {
        lock (_gate)
        {
            if (_blocks.TryGetValue(blockId, out Cell[]? found))
            {
                TouchLocked(blockId);
                snapshot = found;
                return true;
            }
        }

        snapshot = [];
        return false;
    }

    /// <summary>
    ///     Stores (or overwrites) the immutable snapshot for
    ///     <paramref name="blockId"/> and raises <see cref="BlockFrozen"/>.
    ///     Evicts the least-recently-used block when at capacity.
    /// </summary>
    public void Freeze(int blockId, Cell[] snapshot)
    {
        BlockFrozenEventArgs? args = null;
        lock (_gate)
        {
            if (_blocks.TryGetValue(blockId, out _))
            {
                _blocks[blockId] = snapshot;
                TouchLocked(blockId);
            }
            else
            {
                if (_blocks.Count >= _capacity)
                {
                    int evicted = _lru.Last!.Value;
                    _lru.RemoveLast();
                    _blocks.Remove(evicted);
                }

                _blocks[blockId] = snapshot;
                _lru.AddFirst(blockId);
            }

            args = new BlockFrozenEventArgs(blockId, snapshot);
        }

        // Raise outside the gate: handlers may re-enter via TryGet/Count and
        // must never deadlock against the freeze itself.
        BlockFrozen?.Invoke(this, args);
    }

    /// <summary>Drops every frozen block (theme switch, resize, session reset).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _blocks.Clear();
            _lru.Clear();
        }
    }

    private void TouchLocked(int blockId)
    {
        // Relink to MRU front if not already there (dictionary hit implies
        // the node exists; find is O(n) worst case but n == capacity ≤ 500).
        LinkedListNode<int>? node = _lru.First;
        while (node is not null && node.Value != blockId)
        {
            node = node.Next;
        }

        if (node is not null && node != _lru.First)
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
    }
}

/// <summary>Payload of <see cref="FrozenTailMarkdownCache.BlockFrozen"/>.</summary>
public sealed class BlockFrozenEventArgs(int blockId, Cell[] snapshot) : EventArgs
{
    public int BlockId { get; } = blockId;
    public Cell[] Snapshot { get; } = snapshot;
}
