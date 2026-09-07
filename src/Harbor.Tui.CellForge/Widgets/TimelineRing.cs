namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
///     Byte-budget eviction ring over timeline blocks (widgets §3.1): oldest
///     blocks drop first once the resident budget is exceeded; the newest block
///     is always admitted even if alone over budget (a session never loses its
///     live tail). Backing storage grows geometrically and never shrinks —
///     steady-state appends allocate nothing.
/// </summary>
public sealed class TimelineRing
{

    public const long DefaultBudgetBytes = 1 << 20;
    private int _head; // index of the oldest element

    public TimelineRing(long budgetBytes = DefaultBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        BudgetBytes = budgetBytes;
        SlotsForTests = new IChatBlock[16];
    }

    public long BudgetBytes { get; }

    public int Count { get; private set; }

    public long UsedBytes { get; private set; }

    /// <summary>Oldest-to-newest access; wraps the internal ring.</summary>
    public IChatBlock this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return SlotsForTests[(_head + index) % SlotsForTests.Length];
        }
    }

    internal IChatBlock[] SlotsForTests { get; private set; }

    /// <summary>Appends and evicts oldest entries while over budget.</summary>
    public void Append(IChatBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        EnsureCapacity(Count + 1);
        SlotsForTests[(_head + Count) % SlotsForTests.Length] = block;
        Count++;
        UsedBytes += Math.Max(0, block.BudgetBytes);

        while (Count > 1 && UsedBytes > BudgetBytes)
        {
            UsedBytes -= Math.Max(0, SlotsForTests[_head].BudgetBytes);
            SlotsForTests[_head] = null!;
            _head = (_head + 1) % SlotsForTests.Length;
            Count--;
        }
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= SlotsForTests.Length)
        {
            return;
        }

        var grown = new IChatBlock[SlotsForTests.Length * 2];
        for (int i = 0; i < Count; i++)
        {
            grown[i] = SlotsForTests[(_head + i) % SlotsForTests.Length];
        }

        SlotsForTests = grown;
        _head = 0;
    }
}
