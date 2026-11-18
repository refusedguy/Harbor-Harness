using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2686 J03. Session ↔ pull request links are a pure projection plus an
///     append-only JSONL store. These tests pin the fields the report names,
///     the reverse lookup and the idempotent write, without starting a
///     process or touching the real Harbor home.
/// </summary>
public class PullRequestLinkTests
{
    [Test]
    public async Task Build_CarriesSessionRepoNumberStateAndUrl()
    {
        var pr = new ForgePullRequest(2686, "Link PRs to sessions", "MERGED", "https://github.com/o/r/pull/2686");

        Result<SessionPullRequestLink> link = PullRequestLinkProjection.Build(
            "session-42", null, pr, "/worktrees/pr2686", "2026-11-19T00:00:00.000Z");

        await Assert.That(link.IsSuccess).IsTrue();
        await Assert.That(link.Value.SessionId).IsEqualTo("session-42");
        await Assert.That(link.Value.Repo).IsEqualTo("o/r");
        await Assert.That(link.Value.Number).IsEqualTo(2686);
        await Assert.That(link.Value.State).IsEqualTo("closed");
        await Assert.That(link.Value.Url).IsEqualTo("https://github.com/o/r/pull/2686");
        await Assert.That(link.Value.Worktree).IsEqualTo("/worktrees/pr2686");
    }

    [Test]
    public async Task Build_ExplicitRepoOverridesTheUrl()
    {
        var pr = new ForgePullRequest(7, "Title", "OPEN", "https://example.test/7");

        Result<SessionPullRequestLink> link = PullRequestLinkProjection.Build(
            "s", "owner/repo", pr, "/w", "at");

        await Assert.That(link.IsSuccess).IsTrue();
        await Assert.That(link.Value.Repo).IsEqualTo("owner/repo");
    }

    [Test]
    public async Task Build_RejectsEmptySessionAndUnusableRepo()
    {
        var pr = new ForgePullRequest(1, "Title", "OPEN", "https://example.test/1");

        await Assert.That(PullRequestLinkProjection.Build(" ", null, pr, "/w", "at").IsFailure).IsTrue();
        await Assert.That(PullRequestLinkProjection.Build("s", null, pr, "/w", "at").IsFailure).IsTrue();
        await Assert.That(PullRequestLinkProjection.Build("s", "../etc", pr, "/w", "at").IsFailure).IsTrue();
    }

    [Test]
    public async Task FormatLineAndParseLine_RoundTripEveryField()
    {
        var link = new SessionPullRequestLink(
            "session-1", "o/r", 12, "open", "https://github.com/o/r/pull/12", "/wt", "2026-11-19T00:00:00Z");

        string line = PullRequestLinkProjection.FormatLine(link);
        Result<SessionPullRequestLink> parsed = PullRequestLinkProjection.ParseLine(line);

        await Assert.That(line.Contains('\n')).IsFalse();
        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value).IsEqualTo(link);
    }

    [Test]
    public async Task ParseLine_FailsClosedOnMalformedOrMissingFields()
    {
        await Assert.That(PullRequestLinkProjection.ParseLine("not json").IsFailure).IsTrue();
        await Assert.That(PullRequestLinkProjection.ParseLine("[]").IsFailure).IsTrue();
        await Assert.That(PullRequestLinkProjection.ParseLine("{\"session\":\"s\"}").IsFailure).IsTrue();
    }

    [Test]
    public async Task Filter_AnswersBothTheSessionAndReverseLookups()
    {
        var links = new[]
        {
            new SessionPullRequestLink("s1", "o/r", 1, "open", "u1", "w1", "t1"),
            new SessionPullRequestLink("s2", "o/r", 1, "closed", "u2", "w2", "t2"),
            new SessionPullRequestLink("s2", "o/other", 2, "open", "u3", "w3", "t3"),
        };

        IReadOnlyList<SessionPullRequestLink> bySession =
            PullRequestLinkProjection.Filter(links, "s2", null, null);
        IReadOnlyList<SessionPullRequestLink> byPullRequest =
            PullRequestLinkProjection.Filter(links, null, "o/r", 1);
        IReadOnlyList<SessionPullRequestLink> byAll =
            PullRequestLinkProjection.Filter(links, "s2", "o/r", 1);

        await Assert.That(bySession.Count).IsEqualTo(2);
        await Assert.That(byPullRequest.Count).IsEqualTo(2);
        await Assert.That(byAll.Count).IsEqualTo(1);
        await Assert.That(byAll[0].SessionId).IsEqualTo("s2");
    }

    [Test]
    public async Task Format_CapsTheOutputAtMaxLinks()
    {
        var links = new List<SessionPullRequestLink>();
        for (int i = 0; i < PullRequestLinkProjection.MaxLinks + 5; i++)
        {
            links.Add(new SessionPullRequestLink("s", "o/r", i + 1, "open", "u", "w", "t"));
        }

        using JsonDocument document = JsonDocument.Parse(PullRequestLinkProjection.Format(links));

        await Assert.That(document.RootElement.GetArrayLength()).IsEqualTo(PullRequestLinkProjection.MaxLinks);
    }

    [Test]
    public async Task Store_AddIsIdempotentAndKeepsBothLookupDirections()
    {
        string root = Path.Combine(Path.GetTempPath(), "harbor-prlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new PullRequestLinkStore(Path.Combine(root, "links.jsonl"));
            var first = new SessionPullRequestLink("s1", "o/r", 10, "open", "u1", "w1", "t1");
            var second = new SessionPullRequestLink("s2", "o/r", 10, "open", "u2", "w2", "t2");

            await Assert.That(store.Add(first).IsSuccess).IsTrue();
            await Assert.That(store.Add(first).IsSuccess).IsTrue();
            await Assert.That(store.Add(second).IsSuccess).IsTrue();

            Result<IReadOnlyList<SessionPullRequestLink>> all = store.ReadAll();
            await Assert.That(all.IsSuccess).IsTrue();
            await Assert.That(all.Value.Count).IsEqualTo(2);
            await Assert.That(PullRequestLinkProjection.Filter(all.Value, null, "o/r", 10).Count).IsEqualTo(2);
            await Assert.That(PullRequestLinkProjection.Filter(all.Value, "s1", null, null).Count).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Store_MissingFileMeansNoLinks()
    {
        string root = Path.Combine(Path.GetTempPath(), "harbor-prlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new PullRequestLinkStore(Path.Combine(root, "absent", "links.jsonl"));

            Result<IReadOnlyList<SessionPullRequestLink>> all = store.ReadAll();

            await Assert.That(all.IsSuccess).IsTrue();
            await Assert.That(all.Value).IsEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
