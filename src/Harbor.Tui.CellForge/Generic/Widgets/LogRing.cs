using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// FIFO scrollback ring for log lines. The generic counterpart of the chat
/// timeline's byte-budget ring: oldest lines evict first, the newest line is
/// always admitted even when alone over budget, and a line cap can further
/// bound scrollback. Storage grows geometrically and never shrinks, so a long
/// stream reaches a steady state where appends allocate nothing.
///
/// An optional eviction callback lets an owner keep derived state (a filtered
/// view, a row counter) in sync without scanning the ring on every append.
/// </summary>
public sealed class LogRing
{
    private LogLine[] _slots;
    private int _head;
    private int _count;
    private int _maxLines;

    /// <summary>Creates a ring with the given byte budget and optional line cap.</summary>
    public LogRing(long budgetBytes = DefaultBudgetBytes, int maxLines = int.MaxValue, Action<LogLine>? onEvict = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLines);
        BudgetBytes = budgetBytes;
        _maxLines = maxLines;
        OnEvict = onEvict;
        _slots = new LogLine[16];
    }

    /// <summary>Default resident byte budget (1 MiB).</summary>
    public const long DefaultBudgetBytes = 1 << 20;

    /// <summary>Callback invoked for each line removed by eviction or trimming.</summary>
    public Action<LogLine>? OnEvict { get; }

    /// <summary>Maximum resident bytes. Immutable; the newest line may exceed it alone.</summary>
    public long BudgetBytes { get; }

    /// <summary>Maximum resident lines. Lowering it trims immediately.</summary>
    public int MaxLines
    {
        get => _maxLines;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxLines = value;
            Trim();
        }
    }

    /// <summary>Current resident line count.</summary>
    public int Count => _count;

    /// <summary>Sum of the resident lines' byte costs.</summary>
    public long UsedBytes { get; private set; }

    /// <summary>Total lines removed by eviction and trimming since construction.</summary>
    public long EvictedCount { get; private set; }

    /// <summary>Oldest-to-newest access; wraps the internal array.</summary>
    public LogLine this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
            return _slots[(_head + index) % _slots.Length];
        }
    }

    /// <summary>Appends a line, evicting oldest entries while over a limit.</summary>
    public void Append(LogLine line)
    {
        EnsureCapacity(_count + 1);
        _slots[(_head + _count) % _slots.Length] = line;
        _count++;
        UsedBytes += line.BudgetBytes;
        Trim();
    }

    /// <summary>Evicts oldest entries until both limits are satisfied.</summary>
    public void Trim()
    {
        while (_count > 1 && (UsedBytes > BudgetBytes || _count > _maxLines))
        {
            EvictOldest();
        }
    }

    /// <summary>
    /// Removes and returns the oldest line without invoking the callback.
    /// Owners of a derived view call this directly when they already know the
    /// removed line and handle their own accounting.
    /// </summary>
    public LogLine RemoveOldest()
    {
        if (_count == 0)
        {
            return default;
        }

        var removed = _slots[_head];
        UsedBytes -= removed.BudgetBytes;
        _slots[_head] = default;
        _head = (_head + 1) % _slots.Length;
        _count--;
        return removed;
    }

    /// <summary>Drops all resident lines. The eviction callback is not invoked.</summary>
    public void Clear()
    {
        Array.Clear(_slots, 0, _slots.Length);
        _head = 0;
        _count = 0;
        UsedBytes = 0;
    }

    private void EvictOldest()
    {
        var removed = RemoveOldest();
        EvictedCount++;
        OnEvict?.Invoke(removed);
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _slots.Length)
        {
            return;
        }

        var grown = new LogLine[_slots.Length * 2];
        for (int i = 0; i < _count; i++)
        {
            grown[i] = _slots[(_head + i) % _slots.Length];
        }

        _slots = grown;
        _head = 0;
    }
}
