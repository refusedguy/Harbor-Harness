// #2680 F108. Behaviour tests for the wakeup schedule parser: these pin the
// observable parse result and the delay resolution without starting a timer,
// writing a store or consulting the local clock.

using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

public class WakeupScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task ParsesBareSecondsAsRelative()
    {
        var schedule = WakeupScheduleParser.Parse("30");

        await Assert.That(schedule).IsNotNull();
        await Assert.That(schedule!.IsRelative).IsTrue();
        await Assert.That(schedule.Delay).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(schedule.At).IsNull();
    }

    [Test]
    [Arguments("15s", 15)]
    [Arguments("10sec", 10)]
    [Arguments("2m", 120)]
    [Arguments("3h", 10_800)]
    [Arguments("1d", 86_400)]
    public async Task ParsesRelativeUnitSuffixes(string input, int seconds)
    {
        var schedule = WakeupScheduleParser.Parse(input);

        await Assert.That(schedule).IsNotNull();
        await Assert.That(schedule!.IsRelative).IsTrue();
        await Assert.That(schedule.Delay).IsEqualTo(TimeSpan.FromSeconds(seconds));
    }

    [Test]
    [Arguments(" 45S ")]
    [Arguments("2M")]
    [Arguments(" 7 ")]
    public async Task ParsesRelativeIgnoringCaseAndWhitespace(string input)
    {
        var schedule = WakeupScheduleParser.Parse(input);

        await Assert.That(schedule).IsNotNull();
        await Assert.That(schedule!.IsRelative).IsTrue();
    }

    [Test]
    public async Task ParsesAbsoluteIsoTimestamp()
    {
        var schedule = WakeupScheduleParser.Parse("2026-11-20T10:30:00Z");

        await Assert.That(schedule).IsNotNull();
        await Assert.That(schedule!.IsAbsolute).IsTrue();
        await Assert.That(schedule.Delay).IsEqualTo(TimeSpan.Zero);
        await Assert.That(schedule.At).IsNotNull();
    }

    [Test]
    public async Task ParsesAbsoluteTimestampWithOffset()
    {
        var schedule = WakeupScheduleParser.Parse("2026-11-20T10:30:00+03:00");

        await Assert.That(schedule).IsNotNull();
        await Assert.That(schedule!.IsAbsolute).IsTrue();
    }

    [Test]
    public async Task ResolveAtUsesDelayForRelativeAndInstantForAbsolute()
    {
        var relative = WakeupScheduleParser.Parse("2m")!.Value;
        var absolute = WakeupScheduleParser.Parse("2026-11-20T10:30:00Z")!.Value;

        await Assert.That(relative.ComputeAt(Now)).IsEqualTo(Now.AddMinutes(2));
        await Assert.That(absolute.ComputeAt(Now)).IsEqualTo(absolute.At!.Value);
    }

    [Test]
    public async Task ResolveDelayUsesRemainingTimeForAbsolute()
    {
        var future = WakeupScheduleParser.Parse("2026-11-19T12:05:00Z")!.Value;
        var past = WakeupScheduleParser.Parse("2026-11-19T11:00:00Z")!.Value;

        await Assert.That(future.ComputeDelay(Now)).IsEqualTo(TimeSpan.FromMinutes(5));
        await Assert.That(past.ComputeDelay(Now)).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("abc")]
    [Arguments("0")]
    [Arguments("0s")]
    [Arguments("-5s")]
    [Arguments("+5")]
    [Arguments("1.5")]
    [Arguments("1x")]
    [Arguments("m")]
    [Arguments("2 h")]
    public async Task RejectsMalformedInput(string? input)
    {
        await Assert.That(WakeupScheduleParser.Parse(input)).IsNull();
        await Assert.That(WakeupScheduleParser.TryParse(input, out _)).IsFalse();
    }

    [Test]
    public async Task RejectsRelativeDelaysBelowTheMinimum()
    {
        await Assert.That(WakeupScheduleParser.Parse("0s")).IsNull();
        await Assert.That(WakeupScheduleParser.Parse("0m")).IsNull();
    }

    [Test]
    public async Task RejectsRelativeDelaysAboveTheMaximum()
    {
        string tooBig = $"{WakeupScheduleParser.MaxDelaySeconds + 1}s";

        await Assert.That(WakeupScheduleParser.Parse(tooBig)).IsNull();
    }

    [Test]
    public async Task AcceptsRelativeDelayAtTheMaximum()
    {
        var schedule = WakeupScheduleParser.Parse($"{WakeupScheduleParser.MaxDelaySeconds}s");

        await Assert.That(schedule).IsNotNull();
        await Assert.That(schedule!.Delay).IsEqualTo(TimeSpan.FromSeconds(WakeupScheduleParser.MaxDelaySeconds));
    }

    [Test]
    public async Task TryParseWritesDefaultOnFailure()
    {
        bool parsed = WakeupScheduleParser.TryParse("garbage", out WakeupSchedule schedule);

        await Assert.That(parsed).IsFalse();
        await Assert.That(schedule).IsEqualTo(default(WakeupSchedule));
    }
}
