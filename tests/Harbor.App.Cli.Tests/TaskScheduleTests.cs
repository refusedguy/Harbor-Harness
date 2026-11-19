using System.Text;
using CSharpFunctionalExtensions;
using Harbor.App.Cli.Commands;
using Harbor.Tools.Builtin;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2674 G11 / #3395. The schedule resolver, argument parser, store codec
///     and webhook proof are pure; pin the contracts here without a clock, a
///     file or an HTTP listener. Cron schedules are parsed by the shared
///     <see cref="CronScheduleParser" />, so these tests cover the real
///     consumer path rather than a private copy of the grammar.
/// </summary>
public class TaskScheduleTests
{
    private static readonly DateTimeOffset Reference =
        new(2026, 11, 19, 10, 37, 0, TimeSpan.Zero);

    [Test]
    public async Task ResolveNextFire_CronStepExpressionReturnsNextMatchingMinute()
    {
        Result<DateTimeOffset?> resolved = TaskScheduleVerb.ResolveNextFire("*/5 * * * *", Reference);

        await Assert.That(resolved.IsSuccess).IsTrue();
        DateTimeOffset? next = resolved.Value;
        await Assert.That(next.HasValue).IsTrue();
        await Assert.That(next!.Value).IsEqualTo(new DateTimeOffset(2026, 11, 19, 10, 40, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task ResolveNextFire_DelegatesCronToSharedCronScheduleParser()
    {
        CronSchedule? schedule = CronScheduleParser.Parse("*/15 * * * *");
        Result<DateTimeOffset?> resolved = TaskScheduleVerb.ResolveNextFire("*/15 * * * *", Reference);

        await Assert.That(schedule).IsNotNull();
        await Assert.That(resolved.IsSuccess).IsTrue();
        await Assert.That(resolved.Value).IsEqualTo(schedule!.NextOccurrence(Reference));
    }

    [Test]
    public async Task ResolveNextFire_CronRangesListsAndStepsResolveNextDate()
    {
        Result<DateTimeOffset?> resolved = TaskScheduleVerb.ResolveNextFire("0 9-17/4 1,15 1,7 1-5", Reference);

        await Assert.That(resolved.IsSuccess).IsTrue();
        DateTimeOffset? next = resolved.Value;
        await Assert.That(next.HasValue).IsTrue();
        // Month is restricted to January/July; the next match after
        // 2026-11-19 is Fri 2027-01-01 at the first allowed hour/minute.
        await Assert.That(next!.Value).IsEqualTo(new DateTimeOffset(2027, 1, 1, 9, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task ResolveNextFire_CronNormalizesSundaySeven()
    {
        Result<DateTimeOffset?> seven = TaskScheduleVerb.ResolveNextFire("0 0 * * 7", Reference);
        Result<DateTimeOffset?> zero = TaskScheduleVerb.ResolveNextFire("0 0 * * 0", Reference);

        await Assert.That(seven.IsSuccess).IsTrue();
        await Assert.That(zero.IsSuccess).IsTrue();
        DateTimeOffset? s = seven.Value;
        DateTimeOffset? z = zero.Value;
        await Assert.That(s.HasValue).IsTrue();
        await Assert.That(s!.Value).IsEqualTo(z!.Value);
        await Assert.That(s.Value.DayOfWeek).IsEqualTo(DayOfWeek.Sunday);
    }

    [Test]
    public async Task ResolveNextFire_RejectsMalformedCronExpressions()
    {
        string[] malformed =
        [
            "", "* * * *", "* * * * * *", "60 * * * *", "* 24 * * *", "* * 0 * *",
            "* * * 13 *", "*/0 * * * *", "5-2 * * * *", "a * * * *", "1,,2 * * * *",
        ];

        foreach (string expression in malformed)
            await Assert.That(TaskScheduleVerb.ResolveNextFire(expression, Reference).IsFailure).IsTrue();
    }

    [Test]
    public async Task ResolveNextFire_CronDailyAtMidnightIsStrictlyAfterReference()
    {
        Result<DateTimeOffset?> resolved = TaskScheduleVerb.ResolveNextFire("0 0 * * *", Reference);

        await Assert.That(resolved.IsSuccess).IsTrue();
        DateTimeOffset? next = resolved.Value;
        await Assert.That(next.HasValue).IsTrue();
        await Assert.That(next!.Value).IsEqualTo(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task TaskArgs_Parse_ScheduleReadsAllFlags()
    {
        Result<TaskRequest> parsed = TaskArgs.Parse(
        [
            "schedule", "--cron", "*/10 * * * *", "--prompt", "Rebuild the docs",
            "--name", "docs-watch", "--webhook", "https://example.com/hook",
        ]);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Action).IsEqualTo("schedule");
        await Assert.That(parsed.Value.Name).IsEqualTo("docs-watch");
        await Assert.That(parsed.Value.Cron).IsEqualTo("*/10 * * * *");
        await Assert.That(parsed.Value.Prompt).IsEqualTo("Rebuild the docs");
        await Assert.That(parsed.Value.Webhook).IsEqualTo("https://example.com/hook");
    }

    [Test]
    public async Task TaskArgs_Parse_DerivesNameWhenMissing()
    {
        Result<TaskRequest> parsed = TaskArgs.Parse(["schedule", "--cron", "* * * * *", "--prompt", "Sweep the FLOOR!"]);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Name).IsEqualTo("sweep-the-floor");
        await Assert.That(parsed.Value.Webhook).IsNull();
    }

    [Test]
    public async Task TaskArgs_Parse_ListRemoveAndNext()
    {
        Result<TaskRequest> list = TaskArgs.Parse(["list"]);
        Result<TaskRequest> remove = TaskArgs.Parse(["remove", "docs-watch"]);
        Result<TaskRequest> next = TaskArgs.Parse(["next", "docs-watch"]);

        await Assert.That(list.IsSuccess).IsTrue();
        await Assert.That(list.Value.Action).IsEqualTo("list");
        await Assert.That(remove.IsSuccess).IsTrue();
        await Assert.That(remove.Value.Name).IsEqualTo("docs-watch");
        await Assert.That(next.IsSuccess).IsTrue();
        await Assert.That(next.Value.Action).IsEqualTo("next");
    }

    [Test]
    public async Task TaskArgs_Parse_RejectsBadInput()
    {
        await Assert.That(TaskArgs.Parse([]).IsFailure).IsTrue();
        await Assert.That(TaskArgs.Parse(["bogus"]).IsFailure).IsTrue();
        await Assert.That(TaskArgs.Parse(["schedule"]).IsFailure).IsTrue();
        await Assert.That(TaskArgs.Parse(["schedule", "--cron", "* * * * *"]).IsFailure).IsTrue();
        await Assert.That(TaskArgs.Parse(["schedule", "--prompt", "x"]).IsFailure).IsTrue();
        await Assert.That(TaskArgs.Parse(["schedule", "--cron", "* * * * *", "--prompt", "x", "--name", "bad name"]).IsFailure).IsTrue();
        await Assert.That(TaskArgs.Parse(["schedule", "--cron", "* * * * *", "--prompt", "x", "--webhook", "ftp://x"]).IsFailure).IsTrue();
        await Assert.That(TaskArgs.Parse(["schedule", "--cron", "* * * * *", "--prompt", "x", "--nope"]).IsFailure).IsTrue();
        await Assert.That(TaskArgs.Parse(["list", "--cron", "* * * * *"]).IsFailure).IsTrue();
        await Assert.That(TaskArgs.Parse(["remove"]).IsFailure).IsTrue();
        await Assert.That(TaskArgs.Parse(["remove", "a", "b"]).IsFailure).IsTrue();
    }

    [Test]
    public async Task TaskStore_RoundTrip_PreservesAllFields()
    {
        var tasks = new List<ScheduledTask>
        {
            new("docs-watch", "*/10 * * * *", "Rebuild the docs", "https://example.com/hook", "2026-11-19T00:00:00.0000000+00:00"),
            new("nightly", "0 0 * * *", "Nightly sweep", null, "2026-11-18T00:00:00.0000000+00:00"),
        };

        string json = TaskStore.Serialize(tasks);
        Result<IReadOnlyList<ScheduledTask>> parsed = TaskStore.Parse(json);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Count).IsEqualTo(2);
        await Assert.That(parsed.Value[0].Name).IsEqualTo("docs-watch");
        await Assert.That(parsed.Value[0].Webhook).IsEqualTo("https://example.com/hook");
        await Assert.That(parsed.Value[1].Name).IsEqualTo("nightly");
        await Assert.That(parsed.Value[1].Webhook).IsNull();
    }

    [Test]
    public async Task TaskStore_Parse_HandlesEmptyAndRejectsMalformed()
    {
        Result<IReadOnlyList<ScheduledTask>> empty = TaskStore.Parse("");
        Result<IReadOnlyList<ScheduledTask>> objectRoot = TaskStore.Parse("{}");
        Result<IReadOnlyList<ScheduledTask>> missingField = TaskStore.Parse("[{\"name\":\"x\"}]");
        Result<IReadOnlyList<ScheduledTask>> broken = TaskStore.Parse("[");

        await Assert.That(empty.IsSuccess).IsTrue();
        await Assert.That(empty.Value.Count).IsEqualTo(0);
        await Assert.That(objectRoot.IsFailure).IsTrue();
        await Assert.That(missingField.IsFailure).IsTrue();
        await Assert.That(broken.IsFailure).IsTrue();
    }

    [Test]
    public async Task ResolveNextFire_WiresWakeupParserIntoTaskSchedule()
    {
        DateTimeOffset now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

        Result<DateTimeOffset?> after30s = TaskScheduleVerb.ResolveNextFire("30s", now);
        await Assert.That(after30s.IsSuccess).IsTrue();
        await Assert.That(after30s.Value!.Value).IsEqualTo(now.AddSeconds(30));

        Result<DateTimeOffset?> after2h = TaskScheduleVerb.ResolveNextFire("2h", now);
        await Assert.That(after2h.IsSuccess).IsTrue();
        await Assert.That(after2h.Value!.Value).IsEqualTo(now.AddHours(2));

        var absolute = new DateTimeOffset(2026, 11, 20, 9, 30, 0, TimeSpan.Zero);
        Result<DateTimeOffset?> atDate = TaskScheduleVerb.ResolveNextFire("2026-11-20T09:30:00+00:00", now);
        await Assert.That(atDate.IsSuccess).IsTrue();
        await Assert.That(atDate.Value!.Value).IsEqualTo(absolute);

        await Assert.That(TaskScheduleVerb.ResolveNextFire("999999999d", now).IsFailure).IsTrue();
    }

    [Test]
    public async Task WebhookSignature_SignIsStableAndVerifyAcceptsIt()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"event\":\"task.fire\"}");
        string signature = WebhookSignature.Sign("secret", payload);

        await Assert.That(signature.StartsWith(WebhookSignature.HeaderPrefix, StringComparison.Ordinal)).IsTrue();
        await Assert.That(WebhookSignature.Verify("secret", payload, signature)).IsTrue();
        await Assert.That(WebhookSignature.Sign("secret", payload)).IsEqualTo(signature);
    }

    [Test]
    public async Task WebhookSignature_VerifyRejectsTampering()
    {
        byte[] payload = Encoding.UTF8.GetBytes("payload");
        string signature = WebhookSignature.Sign("secret", payload);
        byte[] tampered = Encoding.UTF8.GetBytes("payloax");

        await Assert.That(WebhookSignature.Verify("secret", tampered, signature)).IsFalse();
        await Assert.That(WebhookSignature.Verify("other-secret", payload, signature)).IsFalse();
        await Assert.That(WebhookSignature.Verify("secret", payload, null)).IsFalse();
        await Assert.That(WebhookSignature.Verify("secret", payload, string.Empty)).IsFalse();
        await Assert.That(WebhookSignature.Verify("secret", payload, "deadbeef")).IsFalse();
    }
}
