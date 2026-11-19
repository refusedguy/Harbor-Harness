// #2680 F109. Behaviour tests for the cron schedule parser: these pin the
// observable parse result, field matching and next-occurrence resolution
// without starting a timer, reading the local clock or writing a store.

using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

public class CronScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Parse_Wildcard_MatchesEveryMinute()
    {
        var schedule = CronScheduleParser.Parse("* * * * *");

        await Assert.That(schedule).IsNotNull();
        await Assert.That(schedule!.Expression).IsEqualTo("* * * * *");
        await Assert.That(schedule!.Matches(Now)).IsTrue();
        await Assert.That(schedule!.Matches(new DateTimeOffset(2026, 2, 8, 3, 47, 0, TimeSpan.Zero))).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("* * * *")]
    [Arguments("* * * * * *")]
    [Arguments("60 * * * *")]
    [Arguments("* 24 * * *")]
    [Arguments("* * 0 * *")]
    [Arguments("* * 32 * *")]
    [Arguments("* * * 13 *")]
    [Arguments("* * * 0 *")]
    [Arguments("* * * * 8")]
    [Arguments("5-1 * * * *")]
    [Arguments("*/0 * * * *")]
    [Arguments("*/-2 * * * *")]
    [Arguments("* /5 * * *")]
    [Arguments("1-2-3 * * * *")]
    [Arguments("1,,2 * * * *")]
    [Arguments("?,* * * *")]
    public async Task Parse_RejectsMalformedExpressions(string? expression)
    {
        await Assert.That(CronScheduleParser.Parse(expression)).IsNull();
    }

    [Test]
    public async Task TryParse_ReportsFailureWithNullSchedule()
    {
        bool ok = CronScheduleParser.TryParse("not a cron", out CronSchedule? schedule);

        await Assert.That(ok).IsFalse();
        await Assert.That(schedule).IsNull();
    }

    [Test]
    public async Task Parse_TrimsSurroundingWhitespaceAndCollapsesInternal()
    {
        var schedule = CronScheduleParser.Parse("  */15\t9-17   *\t*   MON-FRI  ");

        await Assert.That(schedule).IsNotNull();
        await Assert.That(schedule!.Expression).IsEqualTo("*/15\t9-17   *\t*   MON-FRI");
    }

    [Test]
    public async Task Parse_StepMatchesFixedMinuteOffsets()
    {
        var schedule = CronScheduleParser.Parse("*/15 * * * *");

        await Assert.That(schedule).IsNotNull();
        await Assert.That(schedule!.Matches(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero))).IsTrue();
        await Assert.That(schedule!.Matches(new DateTimeOffset(2026, 11, 19, 12, 15, 0, TimeSpan.Zero))).IsTrue();
        await Assert.That(schedule!.Matches(new DateTimeOffset(2026, 11, 19, 12, 14, 0, TimeSpan.Zero))).IsFalse();
        await Assert.That(schedule!.Matches(new DateTimeOffset(2026, 11, 19, 12, 59, 0, TimeSpan.Zero))).IsFalse();
    }

    [Test]
    public async Task Parse_ListAndRangeWithStep_MatchOnlySelectedValues()
    {
        var schedule = CronScheduleParser.Parse("1-10/3,30 9 * * *");

        await Assert.That(schedule).IsNotNull();
        await Assert.That(schedule!.Matches(new DateTimeOffset(2026, 11, 19, 9, 1, 0, TimeSpan.Zero))).IsTrue();
        await Assert.That(schedule!.Matches(new DateTimeOffset(2026, 11, 19, 9, 4, 0, TimeSpan.Zero))).IsTrue();
        await Assert.That(schedule!.Matches(new DateTimeOffset(2026, 11, 19, 9, 7, 0, TimeSpan.Zero))).IsTrue();
        await Assert.That(schedule!.Matches(new DateTimeOffset(2026, 11, 19, 9, 10, 0, TimeSpan.Zero))).IsTrue();
        await Assert.That(schedule!.Matches(new DateTimeOffset(2026, 11, 19, 9, 30, 0, TimeSpan.Zero))).IsTrue();
        await Assert.That(schedule!.Matches(new DateTimeOffset(2026, 11, 19, 9, 2, 0, TimeSpan.Zero))).IsFalse();
        await Assert.That(schedule!.Matches(new DateTimeOffset(2026, 11, 19, 10, 1, 0, TimeSpan.Zero))).IsFalse();
    }

    [Test]
    public async Task Parse_MonthAndWeekdayNamesAreCaseInsensitive()
    {
        var upper = CronScheduleParser.Parse("0 9 * jan mon");
        var lower = CronScheduleParser.Parse("0 9 * JAN MON");

        await Assert.That(upper).IsNotNull();
        await Assert.That(lower).IsNotNull();
        await Assert.That(upper!.Matches(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero))).IsTrue();
        await Assert.That(lower!.Matches(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero))).IsTrue();
        await Assert.That(lower!.Matches(new DateTimeOffset(2026, 2, 2, 9, 0, 0, TimeSpan.Zero))).IsFalse();
    }

    [Test]
    public async Task Parse_SundaySevenAliasesZero()
    {
        var zero = CronScheduleParser.Parse("0 9 * * 0");
        var seven = CronScheduleParser.Parse("0 9 * * 7");

        await Assert.That(zero).IsNotNull();
        await Assert.That(seven).IsNotNull();
        var sunday = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
        await Assert.That(zero!.Matches(sunday)).IsTrue();
        await Assert.That(seven!.Matches(sunday)).IsTrue();
        await Assert.That(seven!.Matches(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero))).IsFalse();
    }

    [Test]
    public async Task Parse_RestrictedDayFieldsAreOrEd()
    {
        var schedule = CronScheduleParser.Parse("0 9 19 * MON");

        await Assert.That(schedule).IsNotNull();
        // Thursday the 19th matches by day of month.
        await Assert.That(schedule!.Matches(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero))).IsTrue();
        // Monday the 16th matches by day of week.
        await Assert.That(schedule!.Matches(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero))).IsTrue();
        // Tuesday the 17th matches neither.
        await Assert.That(schedule!.Matches(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero))).IsFalse();
    }

    [Test]
    public async Task NextOccurrence_IsStrictlyAfterAndMinuteAligned()
    {
        var schedule = CronScheduleParser.Parse("* * * * *");

        await Assert.That(schedule).IsNotNull();
        var after = new DateTimeOffset(2026, 11, 19, 12, 0, 30, TimeSpan.Zero);
        DateTimeOffset? next = schedule!.NextOccurrence(after);

        await Assert.That(next).IsNotNull();
        await Assert.That(next!.Value).IsEqualTo(new DateTimeOffset(2026, 11, 19, 12, 1, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task NextOccurrence_FindsNextMatchingMinute()
    {
        var schedule = CronScheduleParser.Parse("*/15 * * * *");

        await Assert.That(schedule).IsNotNull();
        DateTimeOffset? next = schedule!.NextOccurrence(Now.AddMinutes(2));

        await Assert.That(next).IsNotNull();
        await Assert.That(next!.Value).IsEqualTo(new DateTimeOffset(2026, 11, 19, 12, 15, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task NextOccurrence_FindsNextMatchingWeekday()
    {
        var schedule = CronScheduleParser.Parse("0 9 * * MON");

        await Assert.That(schedule).IsNotNull();
        DateTimeOffset? next = schedule!.NextOccurrence(Now);

        await Assert.That(next).IsNotNull();
        await Assert.That(next!.Value).IsEqualTo(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task NextOccurrence_ReturnsNullForImpossibleDate()
    {
        var schedule = CronScheduleParser.Parse("0 0 30 2 *");

        await Assert.That(schedule).IsNotNull();
        await Assert.That(schedule!.NextOccurrence(Now)).IsNull();
    }

    [Test]
    public async Task NextOccurrence_DoesNotConsultLocalClock()
    {
        var schedule = CronScheduleParser.Parse("0 0 1 1 *");
        var after = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.FromHours(5));

        await Assert.That(schedule).IsNotNull();
        DateTimeOffset? next = schedule!.NextOccurrence(after);

        await Assert.That(next).IsNotNull();
        await Assert.That(next!.Value.Offset).IsEqualTo(TimeSpan.FromHours(5));
        await Assert.That(next!.Value.Month).IsEqualTo(1);
        await Assert.That(next!.Value.Day).IsEqualTo(1);
        await Assert.That(next!.Value.Hour).IsEqualTo(0);
    }
}
