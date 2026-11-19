using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2667 receiving-half delivery policy. The guard is pure and
///     timestamp-driven, so dedupe and the 60/min rate limit are pinned without
///     a clock, a daemon listener or any I/O.
/// </summary>
public class HookDeliveryGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task TryAccept_FirstDeliveryPassesAndDuplicateIsSuppressed()
    {
        var guard = new HookDeliveryGuard();

        bool first = guard.TryAccept("session-1", "tool-1", T0);
        bool duplicate = guard.TryAccept("session-1", "tool-1", T0.AddSeconds(5));

        await Assert.That(first).IsTrue();
        await Assert.That(duplicate).IsFalse();
    }

    [Test]
    public async Task TryAccept_DedupeKeyIncludesBothSessionAndToolCall()
    {
        var guard = new HookDeliveryGuard();

        await Assert.That(guard.TryAccept("s1", "t1", T0)).IsTrue();
        await Assert.That(guard.TryAccept("s2", "t1", T0)).IsTrue();
        await Assert.That(guard.TryAccept("s1", "t2", T0)).IsTrue();
    }

    [Test]
    public async Task TryAccept_RateLimitStopsAtSixtyPerMinute()
    {
        var guard = new HookDeliveryGuard();
        int accepted = 0;
        for (int i = 0; i < 65; i++)
        {
            if (guard.TryAccept("session", $"tool-{i}", T0))
                accepted++;
        }

        await Assert.That(accepted).IsEqualTo(HookDeliveryGuard.MaxPerMinute);
    }

    [Test]
    public async Task TryAccept_WindowAdvanceFreesCapacityForNewResults()
    {
        var guard = new HookDeliveryGuard();
        for (int i = 0; i < HookDeliveryGuard.MaxPerMinute; i++)
            guard.TryAccept("session", $"tool-{i}", T0);

        bool insideWindow = guard.TryAccept("session", "inside", T0.AddSeconds(59));
        bool afterWindow = guard.TryAccept("session", "after", T0.AddMinutes(1));

        await Assert.That(insideWindow).IsFalse();
        await Assert.That(afterWindow).IsTrue();
    }

    [Test]
    public async Task TryAccept_ExactWindowBoundaryEvictsOldestHit()
    {
        var guard = new HookDeliveryGuard();
        for (int i = 0; i < HookDeliveryGuard.MaxPerMinute; i++)
            guard.TryAccept("session", $"tool-{i}", T0);

        await Assert.That(guard.TryAccept("session", "boundary", T0.AddMinutes(1))).IsTrue();
    }

    [Test]
    public async Task ResultKey_EqualityIsValueBased()
    {
        var a = new HookDeliveryGuard.ResultKey("s", "t");
        var b = new HookDeliveryGuard.ResultKey("s", "t");
        var c = new HookDeliveryGuard.ResultKey("s", "u");

        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a).IsNotEqualTo(c);
    }
}
