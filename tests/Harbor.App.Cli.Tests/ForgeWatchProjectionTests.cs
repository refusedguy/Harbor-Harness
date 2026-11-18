using CSharpFunctionalExtensions;
using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2686 J01. Pull-request watching splits into a pure gh JSON projection
///     and a pure wake policy. These tests pin both without starting a process.
/// </summary>
public class ForgeWatchProjectionTests
{
    private const string CleanJson = """
        {
          "number": 42,
          "mergeable": "MERGEABLE",
          "mergeStateStatus": "CLEAN",
          "statusCheckRollup": [
            {"__typename":"CheckRun","name":"build","state":"COMPLETED","conclusion":"SUCCESS"},
            {"__typename":"StatusContext","context":"license","state":"SUCCESS"}
          ]
        }
        """;

    [Test]
    public async Task Parse_ReadsBothRollupShapesAndMergeState()
    {
        Result<ForgePullRequestStatus> parsed = PullRequestWatchProjection.Parse(CleanJson);

        await Assert.That(parsed.IsSuccess).IsTrue();
        ForgePullRequestStatus status = parsed.Value;
        await Assert.That(status.Number).IsEqualTo(42);
        await Assert.That(status.Mergeable).IsEqualTo("MERGEABLE");
        await Assert.That(status.HasConflict).IsFalse();
        await Assert.That(status.Checks.Count).IsEqualTo(2);
        await Assert.That(status.Checks[0].Name).IsEqualTo("build");
        await Assert.That(status.Checks[0].Bucket).IsEqualTo("pass");
        await Assert.That(status.Checks[1].Name).IsEqualTo("license");
        await Assert.That(status.Checks[1].Bucket).IsEqualTo("pass");
    }

    [Test]
    public async Task Parse_MapsFailedConclusionsConflictsAndPendingChecks()
    {
        const string json = """
            {
              "number": 7,
              "mergeable": "CONFLICT",
              "mergeStateStatus": "DIRTY",
              "statusCheckRollup": [
                {"name":"test","conclusion":"FAILURE"},
                {"name":"lint","state":"ERROR"},
                {"name":"deploy","conclusion":null}
              ]
            }
            """;

        ForgePullRequestStatus status = PullRequestWatchProjection.Parse(json).Value;

        await Assert.That(status.HasConflict).IsTrue();
        await Assert.That(status.Checks[0].Bucket).IsEqualTo("fail");
        await Assert.That(status.Checks[1].Bucket).IsEqualTo("fail");
        await Assert.That(status.Checks[2].Bucket).IsEqualTo("pending");
    }

    [Test]
    public async Task Parse_CapsTheRollupAtTheOutputLimit()
    {
        var rows = new List<string>();
        for (int i = 0; i < PullRequestWatchProjection.MaxChecks + 4; i++)
        {
            rows.Add("{\"name\":\"c" + i + "\",\"conclusion\":\"SUCCESS\"}");
        }

        ForgePullRequestStatus status = PullRequestWatchProjection.Parse(
            "{\"number\":1,\"statusCheckRollup\":[" + string.Join(',', rows) + "]}").Value;

        await Assert.That(status.Checks.Count).IsEqualTo(PullRequestWatchProjection.MaxChecks);
    }

    [Test]
    public async Task Parse_FailsClosedOnBadInput()
    {
        await Assert.That(PullRequestWatchProjection.Parse(null).IsFailure).IsTrue();
        await Assert.That(PullRequestWatchProjection.Parse("   ").IsFailure).IsTrue();
        await Assert.That(PullRequestWatchProjection.Parse("{not json").IsFailure).IsTrue();
        await Assert.That(PullRequestWatchProjection.Parse("[]").IsFailure).IsTrue();
        await Assert.That(PullRequestWatchProjection.Parse("{\"number\":0}").IsFailure).IsTrue();
        await Assert.That(PullRequestWatchProjection.Parse("{\"number\":\"42\"}").IsFailure).IsTrue();
    }

    [Test]
    public async Task Decide_WakesOnFailedChecksWithAStableSignature()
    {
        ForgePullRequestStatus status = PullRequestWatchProjection.Parse(CleanJson).Value with
        {
            Checks =
            [
                new ForgeCheck("zeta", "COMPLETED", "FAILURE"),
                new ForgeCheck("alpha", "COMPLETED", "FAILURE"),
                new ForgeCheck("alpha", "COMPLETED", "FAILURE"),
            ],
        };

        WatchDecision decision = PullRequestWatchProjection.Decide(status, WatchState.Initial);

        await Assert.That(decision.ShouldWake).IsTrue();
        await Assert.That(decision.Reason).IsEqualTo("failed-checks");
        await Assert.That(decision.HasConflict).IsFalse();
        await Assert.That(decision.FailedChecks).IsEquivalentTo(new[] { "alpha", "zeta" });
        await Assert.That(decision.Signature).IsEqualTo("c=0|f=alpha,zeta");
    }

    [Test]
    public async Task Decide_DedupesAnUnchangedPollAndStopsAtTheWakeLimit()
    {
        ForgePullRequestStatus status = PullRequestWatchProjection.Parse(CleanJson).Value with
        {
            Checks = [new ForgeCheck("build", "COMPLETED", "FAILURE")],
        };
        WatchDecision first = PullRequestWatchProjection.Decide(status, WatchState.Initial);

        WatchDecision repeated = PullRequestWatchProjection.Decide(
            status, new WatchState(1, first.Signature));
        await Assert.That(repeated.ShouldWake).IsFalse();
        await Assert.That(repeated.Reason).IsEqualTo("unchanged");

        WatchDecision exhausted = PullRequestWatchProjection.Decide(
            status, new WatchState(PullRequestWatchProjection.MaxWakes, first.Signature));
        await Assert.That(exhausted.ShouldWake).IsFalse();
        await Assert.That(exhausted.Reason).IsEqualTo("wake-limit");
    }

    [Test]
    public async Task Decide_DetectsConflictsAndIgnoresCleanPolls()
    {
        ForgePullRequestStatus clean = PullRequestWatchProjection.Parse(CleanJson).Value;
        WatchDecision quiet = PullRequestWatchProjection.Decide(clean, WatchState.Initial);
        await Assert.That(quiet.ShouldWake).IsFalse();
        await Assert.That(quiet.Reason).IsEqualTo("no-signal");

        ForgePullRequestStatus conflicted = clean with { Mergeable = "CONFLICT" };
        WatchDecision wake = PullRequestWatchProjection.Decide(conflicted, WatchState.Initial);
        await Assert.That(wake.ShouldWake).IsTrue();
        await Assert.That(wake.Reason).IsEqualTo("conflict");
        await Assert.That(wake.HasConflict).IsTrue();
        await Assert.That(wake.Signature).IsEqualTo("c=1|f=");
    }

    [Test]
    public async Task WatchArgs_ValidateNumberWakesAndSignature()
    {
        Result<PrWatchArgs> parsed = PrWatchArgs.Parse(
            ["42", "--wakes", "2", "--last", "c=0|f=build"]);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Number).IsEqualTo(42);
        await Assert.That(parsed.Value.WakeCount).IsEqualTo(2);
        await Assert.That(parsed.Value.LastSignature).IsEqualTo("c=0|f=build");

        await Assert.That(PrWatchArgs.Parse([]).IsFailure).IsTrue();
        await Assert.That(PrWatchArgs.Parse(["0"]).IsFailure).IsTrue();
        await Assert.That(PrWatchArgs.Parse(["1", "2"]).IsFailure).IsTrue();
        await Assert.That(PrWatchArgs.Parse(["1", "--wakes", "99"]).IsFailure).IsTrue();
        await Assert.That(PrWatchArgs.Parse(["1", "--last"]).IsFailure).IsTrue();
        await Assert.That(PrWatchArgs.Parse(["1", "--bogus"]).IsFailure).IsTrue();
    }
}
