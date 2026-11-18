using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2674 H8. <c>harbor issue</c> is a thin gh CLI wrapper: the thing worth
///     pinning is what argv reaches gh and what the user sees, not the process
///     plumbing (already owned by the drained proxy seam).
/// </summary>
public class IssueVerbTests
{
    [Test]
    public async Task Parse_List_UsesBoundedDefaults()
    {
        var parsed = IssueArgs.Parse(["list"]);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Action).IsEqualTo("list");
        await Assert.That(parsed.Value.Limit).IsEqualTo(IssueArgs.DefaultLimit);
        await Assert.That(parsed.Value.State).IsNull();
        await Assert.That(parsed.Value.Repo).IsNull();
    }

    [Test]
    public async Task Parse_List_ReadsLimitStateAndRepo()
    {
        var parsed = IssueArgs.Parse(["list", "--limit", "7", "--state", "all", "--repo", "refusedguy/Harbor-Harness"]);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Limit).IsEqualTo(7);
        await Assert.That(parsed.Value.State).IsEqualTo("all");
        await Assert.That(parsed.Value.Repo).IsEqualTo("refusedguy/Harbor-Harness");
    }

    [Test]
    public async Task Parse_ReadAndComment_RequirePositiveNumber()
    {
        await Assert.That(IssueArgs.Parse(["read"]).IsFailure).IsTrue();
        await Assert.That(IssueArgs.Parse(["read", "0"]).IsFailure).IsTrue();
        await Assert.That(IssueArgs.Parse(["comment", "12"]).IsFailure).IsTrue();

        var read = IssueArgs.Parse(["read", "12", "--repo", "o/r"]);
        await Assert.That(read.IsSuccess).IsTrue();
        await Assert.That(read.Value.Number).IsEqualTo(12);
    }

    [Test]
    public async Task Parse_Comment_AcceptsExactlyOneBodySource()
    {
        await Assert.That(IssueArgs.Parse(["comment", "5", "--body", "thanks"]).IsSuccess).IsTrue();
        await Assert.That(IssueArgs.Parse(["comment", "5", "--body-file", "reply.txt"]).IsSuccess).IsTrue();
        await Assert.That(IssueArgs.Parse(["comment", "5", "--body", "a", "--body-file", "b"]).IsFailure).IsTrue();
    }

    [Test]
    public async Task Parse_RejectsBadFlagsAndRepoShapes()
    {
        await Assert.That(IssueArgs.Parse([]).IsFailure).IsTrue();
        await Assert.That(IssueArgs.Parse(["bogus"]).IsFailure).IsTrue();
        await Assert.That(IssueArgs.Parse(["list", "--limit", "0"]).IsFailure).IsTrue();
        await Assert.That(IssueArgs.Parse(["list", "--limit", "101"]).IsFailure).IsTrue();
        await Assert.That(IssueArgs.Parse(["list", "--state", "merged"]).IsFailure).IsTrue();
        await Assert.That(IssueArgs.Parse(["list", "--nope"]).IsFailure).IsTrue();
        await Assert.That(IssueArgs.Parse(["read", "1", "--body", "x"]).IsFailure).IsTrue();
        await Assert.That(IssueArgs.Parse(["read", "1", "--repo", "../etc"]).IsFailure).IsTrue();
        await Assert.That(IssueArgs.Parse(["list", "--repo", "a/b/c"]).IsFailure).IsTrue();
        await Assert.That(IssueArgs.Parse(["read", "1", "2"]).IsFailure).IsTrue();
    }

    [Test]
    public async Task GhCommand_List_IsShellFreeAndCarriesEveryFlag()
    {
        var parsed = IssueArgs.Parse(["list", "--limit", "7", "--state", "closed", "--repo", "o/r"]);
        IReadOnlyList<string> argv = IssueGhCommand.Build(parsed.Value);

        await Assert.That(argv).IsEquivalentTo(new[]
        {
            "issue", "list", "--limit", "7", "--state", "closed",
            "--json", "number,title,state,url", "--repo", "o/r",
        });
    }

    [Test]
    public async Task GhCommand_ReadAndComment_UseTheRightSubcommands()
    {
        var read = IssueArgs.Parse(["read", "42"]);
        await Assert.That(IssueGhCommand.Build(read.Value)).IsEquivalentTo(new[]
        {
            "issue", "view", "42", "--json", "number,title,state,url,body,comments",
        });

        var comment = IssueArgs.Parse(["comment", "42", "--body", "a b"]);
        await Assert.That(IssueGhCommand.Build(comment.Value)).IsEquivalentTo(new[]
        {
            "issue", "comment", "42", "--body", "a b",
        });
    }

    [Test]
    public async Task ParseList_ProjectsAndNormalizesState()
    {
        const string json = """
            [{"number":3,"title":"First","state":"OPEN","url":"https://example/i/3"},
             {"number":4,"title":"Second","state":"CLOSED","url":"https://example/i/4"}]
            """;

        var parsed = IssueProjection.ParseList(json);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Count).IsEqualTo(2);
        await Assert.That(parsed.Value[0].Number).IsEqualTo(3);
        await Assert.That(parsed.Value[1].State).IsEqualTo("closed");
        string text = IssueProjection.FormatList(parsed.Value);
        await Assert.That(text).Contains("#3");
        await Assert.That(text).Contains("https://example/i/4");
    }

    [Test]
    public async Task ParseDetail_IncludesAuthorAndBody()
    {
        const string json = """
            {"number":9,"title":"Bug","state":"OPEN","url":"https://example/i/9",
             "body":"details","comments":[{"author":{"login":"alice"},"body":"confirmed"}]}
            """;

        var parsed = IssueProjection.ParseDetail(json);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Comments.Count).IsEqualTo(1);
        await Assert.That(parsed.Value.Comments[0].Author).IsEqualTo("alice");
        string text = IssueProjection.FormatDetail(parsed.Value);
        await Assert.That(text).Contains("details");
        await Assert.That(text).Contains("[alice] confirmed");
    }

    [Test]
    public async Task Projection_FailsClosedOnMalformedAndEmptyPayloads()
    {
        await Assert.That(IssueProjection.ParseList("").IsFailure).IsTrue();
        await Assert.That(IssueProjection.ParseList("{}").IsFailure).IsTrue();
        await Assert.That(IssueProjection.ParseList("{not json").IsFailure).IsTrue();
        await Assert.That(IssueProjection.ParseDetail("null").IsFailure).IsTrue();
        await Assert.That(IssueProjection.ParseDetail("[1,2]").IsFailure).IsTrue();
    }

    [Test]
    public async Task Projection_CapsTextSoOneIssueCannotFloodStdout()
    {
        string longBody = new('x', IssueProjection.MaxTextChars + 500);
        string json = $"{{\"number\":1,\"title\":\"t\",\"state\":\"OPEN\",\"url\":\"u\",\"body\":\"{longBody}\"}}";

        var parsed = IssueProjection.ParseDetail(json);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Body.Length).IsEqualTo(IssueProjection.MaxTextChars + 1);
    }
}
