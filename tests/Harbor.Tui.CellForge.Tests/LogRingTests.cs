using Harbor.Tui.CellForge.Generic.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Scrollback ring coverage for the generic log primitive (#2323): byte
/// budget, line cap and the invariant that the newest line survives even when
/// it alone is over budget. Mirrors the chat timeline ring's contract without
/// borrowing any chat type.
/// </summary>
public class LogRingTests
{
    [Test]
    public async Task Append_ByteBudget_EvictsOldestFirst()
    {
        // "L" + digit is 2 chars, so 36 bytes each and four fit in 144 bytes.
        var ring = new LogRing(budgetBytes: 144);
        for (int i = 0; i < 10; i++)
        {
            ring.Append(new LogLine("L" + i));
        }

        await Assert.That(ring.Count).IsEqualTo(4);
        await Assert.That(ring[0].Text).IsEqualTo("L6");
        await Assert.That(ring[ring.Count - 1].Text).IsEqualTo("L9");
        await Assert.That(ring.EvictedCount).IsEqualTo(6);
        await Assert.That(ring.UsedBytes).IsLessThanOrEqualTo(144);
    }

    [Test]
    public async Task Append_OverBudgetSingleLine_KeepsNewest()
    {
        var ring = new LogRing(budgetBytes: 16);
        ring.Append(new LogLine("short"));
        ring.Append(new LogLine("a much longer line than the budget"));

        await Assert.That(ring.Count).IsEqualTo(1);
        await Assert.That(ring[0].Text).IsEqualTo("a much longer line than the budget");
        await Assert.That(ring.EvictedCount).IsEqualTo(1);
    }

    [Test]
    public async Task MaxLines_CapsScrollback_AndTrimOnLower()
    {
        var ring = new LogRing(budgetBytes: long.MaxValue, maxLines: 5);
        for (int i = 0; i < 100; i++)
        {
            ring.Append(new LogLine("L" + i));
        }

        await Assert.That(ring.Count).IsEqualTo(5);
        await Assert.That(ring[0].Text).IsEqualTo("L95");

        ring.MaxLines = 2;
        await Assert.That(ring.Count).IsEqualTo(2);
        await Assert.That(ring[0].Text).IsEqualTo("L98");
        await Assert.That(ring[1].Text).IsEqualTo("L99");
    }

    [Test]
    public async Task GeometricGrowth_PreservesFifoOrder()
    {
        var ring = new LogRing(budgetBytes: long.MaxValue);
        for (int i = 0; i < 1000; i++)
        {
            ring.Append(new LogLine(i.ToString()));
        }

        await Assert.That(ring.Count).IsEqualTo(1000);
        for (int i = 0; i < 1000; i++)
        {
            await Assert.That(ring[i].Text).IsEqualTo(i.ToString());
        }
    }

    [Test]
    public async Task EvictionCallback_ReceivesEvictedLinesInOrder()
    {
        var evicted = new List<string>();
        var ring = new LogRing(budgetBytes: 128, onEvict: line => evicted.Add(line.Text));
        for (int i = 0; i < 6; i++)
        {
            ring.Append(new LogLine("L" + i));
        }

        // 128 bytes holds three 36-byte lines; the first three are evicted.
        await Assert.That(evicted).IsEquivalentTo(["L0", "L1", "L2"]);
        await Assert.That(ring.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Clear_ResetsCounts_ButKeepsBudget()
    {
        var ring = new LogRing(budgetBytes: 144);
        for (int i = 0; i < 6; i++)
        {
            ring.Append(new LogLine("L" + i));
        }

        ring.Clear();
        await Assert.That(ring.Count).IsEqualTo(0);
        await Assert.That(ring.UsedBytes).IsEqualTo(0L);
        await Assert.That(ring.EvictedCount).IsEqualTo(2);
        await Assert.That(ring.BudgetBytes).IsEqualTo(144L);
    }
}
