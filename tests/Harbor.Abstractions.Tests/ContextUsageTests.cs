using Harbor.Abstractions.Models;
namespace Harbor.Abstractions.Tests;

/// <summary>
///     Pins the canonical #75 ctx% formula: accumulated input+output over the
///     context window. All three surfaces (status-bar VM, sidebar, CellForge
///     status bar) must compute through <see cref="ContextUsage" />.
/// </summary>
public class ContextUsageTests
{
    [Test]
    public async Task PercentUsed_Accumulates_Input_And_Output()
    {
        await Assert.That(ContextUsage.PercentUsed(3000, 500, 10_000)).IsEqualTo(35);
        await Assert.That(ContextUsage.PercentUsed(7400, 700, 10_000)).IsEqualTo(81);
    }

    [Test]
    public async Task PercentUsed_Summed_Overload_Agrees_With_Split_Overload()
    {
        await Assert.That(ContextUsage.PercentUsed(8100, 10_000))
            .IsEqualTo(ContextUsage.PercentUsed(7400, 700, 10_000));
    }

    [Test]
    public async Task PercentUsed_Saturates_At_100()
    {
        await Assert.That(ContextUsage.PercentUsed(6000, 5000, 10_000)).IsEqualTo(100);
        await Assert.That(ContextUsage.PercentUsed(10_000, 10_000)).IsEqualTo(100);
    }

    [Test]
    public async Task PercentUsed_Zero_When_Window_Unknown()
    {
        await Assert.That(ContextUsage.PercentUsed(5000, 1000, 0)).IsEqualTo(0);
        await Assert.That(ContextUsage.PercentUsed(5000, 1000, -1)).IsEqualTo(0);
        await Assert.That(ContextUsage.PercentUsed(6000, 0)).IsEqualTo(0);
    }

    [Test]
    public async Task PercentUsed_Zero_When_No_Usage()
    {
        await Assert.That(ContextUsage.PercentUsed(0, 0, 10_000)).IsEqualTo(0);
    }

    [Test]
    public async Task PercentUsed_Huge_Values_Saturate_Without_Overflow()
    {
        await Assert.That(ContextUsage.PercentUsed(long.MaxValue, 1000)).IsEqualTo(100);
        await Assert.That(ContextUsage.PercentUsed(long.MaxValue / 2, long.MaxValue / 2, 1000)).IsEqualTo(100);
        // used < window but used*100 would overflow long → must still saturate, not wrap.
        await Assert.That(ContextUsage.PercentUsed(long.MaxValue / 50, long.MaxValue)).IsEqualTo(100);
    }

    [Test]
    public async Task RatioUsed_Matches_Percent_Scale_And_Clamps()
    {
        await Assert.That(ContextUsage.RatioUsed(3000, 0, 10_000)).IsEqualTo(0.3);
        await Assert.That(ContextUsage.RatioUsed(6000, 5000, 10_000)).IsEqualTo(1.0);
        await Assert.That(ContextUsage.RatioUsed(1000, 1000, 0)).IsEqualTo(0.0);
        await Assert.That(ContextUsage.RatioUsed(0, 0, 10_000)).IsEqualTo(0.0);
    }

    [Test]
    public async Task Thresholds_Are_Warn_50_And_Danger_85()
    {
        await Assert.That(ContextUsage.WarnThreshold).IsEqualTo(0.50);
        await Assert.That(ContextUsage.DangerThreshold).IsEqualTo(0.85);
    }
}
