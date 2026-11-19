using CSharpFunctionalExtensions;
using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2666: the watch steer contract. The forge is injected, so these tests
///     never start gh and never touch a real session store. The link store is
///     a temporary JSONL file owned by the test.
/// </summary>
public class PrSteerTests
{
    private static SessionPullRequestLink Link(int number = 42, string sessionId = "session-abc") =>
        new(sessionId, "refusedguy/Harbor-Harness", number, "open",
            $"https://github.com/refusedguy/Harbor-Harness/pull/{number}",
            "/work/tree", "2026-11-19T00:00:00Z");

    private static ForgePullRequestStatus Status(params string[] failed) =>
        new(42, "MERGEABLE", "CLEAN",
            failed.Select(name => new ForgeCheck(name, "COMPLETED", "FAILURE")).ToArray());

    private static WatchDecision Decide(ForgePullRequestStatus status) =>
        PullRequestWatchProjection.Decide(status, WatchState.Initial);

    private static string TempPath() => Path.Combine(
        Path.GetTempPath(), "pr-steer-" + Guid.NewGuid().ToString("N") + ".jsonl");

    [Test]
    public async Task Build_RendersTheFailedChecksDirective()
    {
        WatchDecision decision = Decide(Status("zeta", "alpha"));

        Result<WatchSteerDirective> steer = PullRequestWatchSteer.Build(
            Link(), decision, 42);

        await Assert.That(steer.IsSuccess).IsTrue();
        WatchSteerDirective directive = steer.Value;
        await Assert.That(directive.SessionId).IsEqualTo("session-abc");
        await Assert.That(directive.Operation).IsEqualTo("message");
        await Assert.That(directive.Reason).IsEqualTo("failed-checks");
        await Assert.That(directive.Signature).IsEqualTo("c=0|f=alpha,zeta");
        await Assert.That(directive.PrNumber).IsEqualTo(42);
        await Assert.That(directive.Instruction).Contains("Pull request #42");
        await Assert.That(directive.Instruction).Contains("alpha");
        await Assert.That(directive.Instruction).Contains("zeta");
    }

    [Test]
    public async Task Build_AddsConflictGuidance()
    {
        ForgePullRequestStatus conflicted = Status("test") with { Mergeable = "CONFLICT" };

        WatchSteerDirective directive = PullRequestWatchSteer.Build(
            Link(), Decide(conflicted), 42).Value;

        await Assert.That(directive.Reason).IsEqualTo("conflict+failed-checks");
        await Assert.That(directive.Instruction).Contains("merge conflict");
        await Assert.That(directive.Instruction).Contains("test");
    }

    [Test]
    public async Task Build_CapsTheInstructionAndTheListedChecks()
    {
        var names = new string[PullRequestWatchProjection.MaxChecks];
        for (int i = 0; i < names.Length; i++)
            names[i] = "check-" + i.ToString("D3") + new string('x', 400);

        WatchSteerDirective directive = PullRequestWatchSteer.Build(
            Link(), Decide(Status(names)), 42).Value;

        await Assert.That(directive.Instruction.Length)
            .IsEqualTo(PullRequestWatchSteer.MaxInstructionChars);
        await Assert.That(directive.Instruction).Contains("check-000");
        await Assert.That(directive.Instruction.Contains("check-099")).IsFalse();
    }

    [Test]
    public async Task Build_FailsClosedOnBadInputs()
    {
        WatchDecision wake = Decide(Status("build"));
        WatchDecision quiet = Decide(new ForgePullRequestStatus(42, "MERGEABLE", "CLEAN", []));

        await Assert.That(PullRequestWatchSteer.Build(null, wake, 42).IsFailure).IsTrue();
        await Assert.That(PullRequestWatchSteer.Build(Link(sessionId: "  "), wake, 42).IsFailure).IsTrue();
        await Assert.That(PullRequestWatchSteer.Build(Link(), wake, 7).IsFailure).IsTrue();
        await Assert.That(PullRequestWatchSteer.Build(Link(), quiet, 42).IsFailure).IsTrue();
    }

    [Test]
    public async Task SteerAsync_UsesTheInjectedForgeAndTheLinkedSession()
    {
        string path = TempPath();
        var store = new PullRequestLinkStore(path);
        await Assert.That(store.Add(Link()).IsSuccess).IsTrue();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        try
        {
            int exit = await PrSteerRunner.SteerAsync(
                ["42"], CancellationToken.None,
                new FakeForge(Status("lint", "build")), store, stdout, stderr);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(stderr.ToString()).IsEmpty();
            string json = stdout.ToString();
            await Assert.That(json).Contains("\"sessionId\":\"session-abc\"");
            await Assert.That(json).Contains("\"operation\":\"message\"");
            await Assert.That(json).Contains("\"reason\":\"failed-checks\"");
            await Assert.That(json).Contains("lint");
            await Assert.That(json).Contains("build");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task SteerAsync_PrintsAQuietDecisionWhenThereIsNoWake()
    {
        string path = TempPath();
        var store = new PullRequestLinkStore(path);
        await Assert.That(store.Add(Link()).IsSuccess).IsTrue();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        try
        {
            int exit = await PrSteerRunner.SteerAsync(
                ["42"], CancellationToken.None,
                new FakeForge(new ForgePullRequestStatus(42, "MERGEABLE", "CLEAN", [])),
                store, stdout, stderr);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(stderr.ToString()).IsEmpty();
            await Assert.That(stdout.ToString()).Contains("\"shouldWake\":false");
            await Assert.That(stdout.ToString()).Contains("\"reason\":\"no-signal\"");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task SteerAsync_FailsWhenNoSessionIsLinked()
    {
        string path = TempPath();
        var store = new PullRequestLinkStore(path);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        try
        {
            int exit = await PrSteerRunner.SteerAsync(
                ["42"], CancellationToken.None,
                new FakeForge(Status("build")), store, stdout, stderr);

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(stdout.ToString()).IsEmpty();
            await Assert.That(stderr.ToString()).Contains("no session is linked");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class FakeForge(ForgePullRequestStatus status) : IForgeClient
    {
        public string Id => "fake";

        public Task<Result<IReadOnlyList<ForgePullRequest>>> ListOpenPullRequestsAsync(
            int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<ForgePullRequest>>(Array.Empty<ForgePullRequest>()));

        public Task<Result<ForgePullRequest?>> GetPullRequestAsync(
            int number, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<ForgePullRequest?>(null));

        public Task<Result<ForgePullRequestStatus>> GetStatusAsync(
            int number, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(status));
    }
}
