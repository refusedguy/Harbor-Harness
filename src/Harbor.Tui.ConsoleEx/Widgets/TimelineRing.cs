using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Widgets;

/// <summary>
/// Byte-budget eviction ring over timeline blocks (widgets §3.1): oldest
/// blocks drop first once the resident budget is exceeded; the newest block
/// is always admitted even if alone over budget (a session never loses its
/// live tail). Backing storage grows geometrically and never shrinks —
/// steady-state appends allocate nothing.
/// </summary>
public sealed class TimelineRing
{
    private IChatBlock[] _slots;
    private int _head;       // index of the oldest element
    private int _count;

    public TimelineRing(long budgetBytes = DefaultBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        BudgetBytes = budgetBytes;
        _slots = new IChatBlock[16];
    }

    public const long DefaultBudgetBytes = 1 << 20;

    public long BudgetBytes { get; }

    public int Count => _count;

    public long UsedBytes { get; private set; }

    /// <summary>Oldest-to-newest access; wraps the internal ring.</summary>
    public IChatBlock this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
            return _slots[(_head + index) % _slots.Length];
        }
    }

    /// <summary>Appends and evicts oldest entries while over budget.</summary>
    public void Append(IChatBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        EnsureCapacity(_count + 1);
        _slots[(_head + _count) % _slots.Length] = block;
        _count++;
        UsedBytes += Math.Max(0, block.BudgetBytes);

        while (_count > 1 && UsedBytes > BudgetBytes)
        {
            UsedBytes -= Math.Max(0, _slots[_head].BudgetBytes);
            _slots[_head] = null!;
            _head = (_head + 1) % _slots.Length;
            _count--;
        }
    }

    internal IChatBlock[] SlotsForTests => _slots;

    private void EnsureCapacity(int needed)
    {
        if (needed <= _slots.Length)
        {
            return;
        }

        var grown = new IChatBlock[_slots.Length * 2];
        for (int i = 0; i < _count; i++)
        {
            grown[i] = _slots[(_head + i) % _slots.Length];
        }

        _slots = grown;
        _head = 0;
    }
}
