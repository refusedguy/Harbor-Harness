namespace Harbor.TestKit.Tests;

/// <summary>
///     The manual clock. The properties worth pinning are the ones the eight
///     hand-written copies disagreed on: whether the timer fires when it is
///     created, and what the recorded delays are.
/// </summary>
public class ManualTimeProviderTests
{
    [Test]
    public async Task Does_Not_Move_On_Its_Own()
    {
        var clock = new ManualTimeProvider();

        DateTimeOffset first = clock.GetUtcNow();
        await Task.Delay(5);
        DateTimeOffset second = clock.GetUtcNow();

        await Assert.That(second).IsEqualTo(first)
            .Because("a clock the test does not drive is a clock the test cannot make a deadline exact with");
    }

    [Test]
    public async Task Advance_Moves_The_Wall_Clock()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);

        clock.Advance(TimeSpan.FromSeconds(90));

        await Assert.That(clock.GetUtcNow()).IsEqualTo(DateTimeOffset.UnixEpoch.AddSeconds(90));
    }

    [Test]
    public async Task AdvanceMs_Moves_The_Tick_Clock()
    {
        var clock = new ManualTimeProvider();

        clock.AdvanceMs(250);

        await Assert.That(clock.GetTimestamp()).IsEqualTo(250)
            .Because("one tick is one millisecond, so a duration in ticks and a duration in milliseconds are the same number and every deadline is exact integer arithmetic");
    }

    [Test]
    public async Task The_Tick_Frequency_Is_One_Kilohertz()
    {
        var clock = new ManualTimeProvider();

        await Assert.That(clock.TimestampFrequency).IsEqualTo(1_000);
    }

    [Test]
    public async Task Records_The_Delays_It_Was_Asked_For()
    {
        var clock = new ManualTimeProvider();

        using ITimer timer = clock.CreateTimer(_ => { }, null, TimeSpan.FromMilliseconds(80), TimeSpan.Zero);

        await Assert.That(clock.Delays.Count).IsEqualTo(1);
        await Assert.That(clock.Delays[0]).IsEqualTo(TimeSpan.FromMilliseconds(80));
    }

    [Test]
    public async Task A_Frozen_Timer_Does_Not_Fire_When_It_Is_Created()
    {
        var clock = new ManualTimeProvider();
        bool fired = false;

        using ITimer timer = clock.CreateTimer(_ => fired = true, null, TimeSpan.Zero, TimeSpan.Zero);

        await Assert.That(fired).IsFalse()
            .Because("a grace timer that fires the moment it is created is not a grace timer");
    }

    [Test]
    public async Task A_Recording_Timer_Fires_When_It_Is_Created()
    {
        var clock = new ManualTimeProvider(fireTimerOnCreate: true);
        bool fired = false;

        using ITimer timer = clock.CreateTimer(_ => fired = true, null, TimeSpan.Zero, TimeSpan.Zero);

        await Assert.That(fired).IsTrue()
            .Because("a retry policy that observes a Task.Delay must see an already-elapsed delay, or the backoff it measures is the scheduler's, not the policy's");
    }
}
