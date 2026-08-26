using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

public class TimelineRingTests
{
    private static SystemBlock Make(string text) => new(text);

    [Test]
    public async Task Append_UnderBudget_KeepsEverything()
    {
        var ring = new TimelineRing(budgetBytes: 100_000);
        for (int i = 0; i < 50; i++)
        {
            ring.Append(Make($"m{i}"));
        }

        await Assert.That(ring.Count).IsEqualTo(50);
        await Assert.That(ring[0].RawText()).IsEqualTo("m0");
        await Assert.That(ring[ring.Count - 1].RawText()).IsEqualTo("m49");
    }

    [Test]
    public async Task OverBudget_EvictsOldestFirst()
    {
        var ring = new TimelineRing(budgetBytes: 256);
        for (int i = 0; i < 20; i++)
        {
            ring.Append(Make(new string('x', 40))); // ~128 B each
        }

        await Assert.That(ring.Count).IsLessThan(20);
        await Assert.That(ring.UsedBytes).IsLessThanOrEqualTo(256 + 128); // newest may overshoot alone
        await Assert.That(ring[ring.Count - 1].RawText()).IsEqualTo(new string('x', 40)); // newest survives
    }

    [Test]
    public async Task Single_OversizedBlock_IsAlwaysAdmitted()
    {
        var ring = new TimelineRing(budgetBytes: 8);
        var big = Make(new string('y', 500));
        ring.Append(big);

        await Assert.That(ring.Count).IsEqualTo(1);
        await Assert.That(ring[0]).IsSameReferenceAs(big);
    }

    [Test]
    public async Task Order_IsMonotonicAfterEvictions()
    {
        var ring = new TimelineRing(budgetBytes: 300);
        for (int i = 0; i < 30; i++)
        {
            ring.Append(Make($"n{i}"));
        }

        int firstVisible = 30 - ring.Count;
        for (int i = 0; i < ring.Count; i++)
        {
            await Assert.That(ring[i].RawText()).IsEqualTo($"n{firstVisible + i}");
        }
    }
}
