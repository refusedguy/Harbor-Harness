using Harbor.Abstractions.Models;
using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2686 K05. Session retention is a pure projection: the planner takes a
///     session list, thresholds and a reference time. These tests pin the
///     classification rules and the fail-closed argument parsing without a
///     store or a clock.
/// </summary>
public class SessionRetentionPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Parse_Defaults_AreConservative()
    {
        SessionMaintenanceArgs args = SessionRetentionPlanner.Parse([]).Value;

        await Assert.That(args.Options.MaxAgeDays).IsEqualTo(SessionRetentionPlanner.DefaultMaxAgeDays);
        await Assert.That(args.Options.KeepRecent).IsEqualTo(SessionRetentionPlanner.DefaultKeepRecent);
        await Assert.That(args.Apply).IsFalse();
    }

    [Test]
    public async Task Parse_ReadsFlagsInAnyOrder()
    {
        SessionMaintenanceArgs args = SessionRetentionPlanner.Parse(
            ["--apply", "--keep-recent", "2", "--older-than-days", "14"]).Value;

        await Assert.That(args.Options.MaxAgeDays).IsEqualTo(14);
        await Assert.That(args.Options.KeepRecent).IsEqualTo(2);
        await Assert.That(args.Apply).IsTrue();
    }

    [Test]
    public async Task Parse_RejectsBadNumbersAndUnknownFlags()
    {
        await Assert.That(SessionRetentionPlanner.Parse(["--older-than-days", "0"]).IsFailure).IsTrue();
        await Assert.That(SessionRetentionPlanner.Parse(["--older-than-days", "-1"]).IsFailure).IsTrue();
        await Assert.That(SessionRetentionPlanner.Parse(["--older-than-days", "abc"]).IsFailure).IsTrue();
        await Assert.That(SessionRetentionPlanner.Parse(["--older-than-days"]).IsFailure).IsTrue();
        await Assert.That(SessionRetentionPlanner.Parse(["--keep-recent", "-1"]).IsFailure).IsTrue();
        await Assert.That(SessionRetentionPlanner.Parse(["--purge"]).IsFailure).IsTrue();
    }

    [Test]
    public async Task Build_KeepsTheMostRecentSessionsRegardlessOfAge()
    {
        Session[] sessions =
        [
            Make("a", 100),
            Make("b", 90),
            Make("c", 80),
        ];

        var plan = SessionRetentionPlanner.Build(
            sessions, new SessionRetentionOptions(30, 2), Now).Value;

        await Assert.That(plan.Keep.Count).IsEqualTo(2);
        await Assert.That(plan.Keep[0].Session.Id).IsEqualTo("c");
        await Assert.That(plan.Keep[0].Reason).IsEqualTo("recent");
        await Assert.That(plan.Keep[1].Session.Id).IsEqualTo("b");
        await Assert.That(plan.Delete.Count).IsEqualTo(1);
        await Assert.That(plan.Delete[0].Session.Id).IsEqualTo("a");
        await Assert.That(plan.Delete[0].Reason).Contains("inactive");
    }

    [Test]
    public async Task Build_DeletesOnlySessionsPastTheThreshold()
    {
        Session[] sessions =
        [
            Make("old", 40),
            Make("boundary", 30),
            Make("fresh", 5),
        ];

        var plan = SessionRetentionPlanner.Build(
            sessions, new SessionRetentionOptions(30, 0), Now).Value;

        await Assert.That(plan.Delete.Count).IsEqualTo(1);
        await Assert.That(plan.Delete[0].Session.Id).IsEqualTo("old");
        await Assert.That(plan.Keep.Count).IsEqualTo(2);
        await Assert.That(plan.Keep.Any(d => d.Reason == "within retention window")).IsTrue();
    }

    [Test]
    public async Task Build_IsDeterministicAndKeepsEverybodyWhenNothingIsStale()
    {
        Session[] sessions = [Make("z", 3), Make("a", 2), Make("m", 1)];
        var options = new SessionRetentionOptions(30, 1);

        SessionRetentionPlan first = SessionRetentionPlanner.Build(sessions, options, Now).Value;
        SessionRetentionPlan second = SessionRetentionPlanner.Build(sessions, options, Now).Value;

        await Assert.That(first.Delete.Count).IsEqualTo(0);
        await Assert.That(first.Keep.Count).IsEqualTo(3);
        await Assert.That(first.Keep[0].Session.Id).IsEqualTo("m");
        await Assert.That(string.Join(",", first.Keep.Select(d => d.Session.Id)))
            .IsEqualTo(string.Join(",", second.Keep.Select(d => d.Session.Id)));
    }

    [Test]
    public async Task Build_RejectsOutOfRangeThresholds()
    {
        Session[] sessions = [Make("a", 1)];

        await Assert.That(SessionRetentionPlanner.Build(
            sessions, new SessionRetentionOptions(0, 1), Now).IsFailure).IsTrue();
        await Assert.That(SessionRetentionPlanner.Build(
            sessions, new SessionRetentionOptions(30, -1), Now).IsFailure).IsTrue();
    }

    [Test]
    public async Task Render_BoundsTheReportAndFlattensTitles()
    {
        var sessions = new List<Session>();
        for (int i = 0; i < SessionRetentionPlanner.RenderCap + 3; i++)
        {
            string id = "s" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Session session = Make(id, 100 - i);
            if (i == SessionRetentionPlanner.RenderCap + 2) session = session with { Title = "line1\nline2" };
            sessions.Add(session);
        }

        SessionRetentionPlan plan = SessionRetentionPlanner.Build(
            sessions, new SessionRetentionOptions(30, 0), Now).Value;
        string report = plan.Render();

        await Assert.That(report.Contains("more.")).IsTrue();
        await Assert.That(report.Split("  DELETE ").Length - 1)
            .IsEqualTo(SessionRetentionPlanner.RenderCap);
        await Assert.That(report.Contains("line1 line2")).IsTrue();
        await Assert.That(report.Contains("line1\nline2")).IsFalse();
    }

    /// <summary>A session with a stable id and an age relative to <see cref="Now"/>.</summary>
    private static Session Make(string id, int daysAgo) =>
        Session.Create("/work", "code", "kilocode", "model", "title-" + id) with
        {
            Id = id,
            UpdatedAt = Now.AddDays(-daysAgo)
        };
}
