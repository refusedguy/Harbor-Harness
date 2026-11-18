using Harbor.Abstractions.Models;
using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2675 item 010: the session-search filter language. Assertions target
///     observable parse results and <see cref="SessionSearchFilter.Matches"/>,
///     not how tokens are stored internally.
/// </summary>
public class SessionSearchFilterTests
{
    private static Session Sample() => Session.Create(
        directory: "/work/harbor",
        agentName: "code",
        providerId: "kilocode",
        modelId: "kilocode/tencent/hy3:free",
        title: "bug triage")
        with
        {
            Id = "abc123def456",
            CreatedAt = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero),
            Status = SessionStatus.Done,
            GitBranch = "dev",
        };

    [Test]
    public async Task Empty_Expression_Selects_Everything()
    {
        var parsed = SessionSearchFilter.Parse("   ");

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.IsEmpty).IsTrue();
        await Assert.That(parsed.Value.Matches(Sample())).IsTrue();
    }

    [Test]
    public async Task Bare_Value_Stays_Legacy_SessionId_Substring()
    {
        var parsed = SessionSearchFilter.Parse("abc123");

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Matches(Sample())).IsTrue();
        await Assert.That(parsed.Value.Matches(Sample() with { Id = "other" })).IsFalse();
    }

    [Test]
    public async Task Different_Keys_Are_Anded()
    {
        var parsed = SessionSearchFilter.Parse("label:bug agent:code status:done");

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Matches(Sample())).IsTrue();
        await Assert.That(parsed.Value.Matches(Sample() with { Agent = "plan" })).IsFalse();
        await Assert.That(parsed.Value.Matches(Sample() with { Status = SessionStatus.Working })).IsFalse();
    }

    [Test]
    public async Task Same_Key_May_Repeat_And_Is_Anded()
    {
        var parsed = SessionSearchFilter.Parse("after:2026-01-01 before:2027-01-01");

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Matches(Sample())).IsTrue();
        await Assert.That(parsed.Value.Matches(Sample() with { CreatedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) })).IsFalse();
    }

    [Test]
    public async Task Quoted_Value_Keeps_Spaces()
    {
        var parsed = SessionSearchFilter.Parse("label:\"bug triage\"");

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Matches(Sample())).IsTrue();
        await Assert.That(parsed.Value.Matches(Sample() with { Title = "bug" })).IsFalse();
    }

    [Test]
    public async Task Unterminated_Quote_Fails_Parse()
    {
        var parsed = SessionSearchFilter.Parse("label:\"bug");

        await Assert.That(parsed.IsFailure).IsTrue();
        await Assert.That(parsed.Error.Contains("quote", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }

    [Test]
    public async Task Unknown_Key_Fails_With_Supported_List()
    {
        var parsed = SessionSearchFilter.Parse("visibility:private");

        await Assert.That(parsed.IsFailure).IsTrue();
        await Assert.That(parsed.Error.Contains("Unsupported filter key", StringComparison.Ordinal)).IsTrue();
        await Assert.That(parsed.Error.Contains("after", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Missing_Value_Fails_Parse()
    {
        var parsed = SessionSearchFilter.Parse("label:");

        await Assert.That(parsed.IsFailure).IsTrue();
        await Assert.That(parsed.Error.Contains("needs a value", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Invalid_Date_Fails_Parse()
    {
        var parsed = SessionSearchFilter.Parse("after:yesterday");

        await Assert.That(parsed.IsFailure).IsTrue();
        await Assert.That(parsed.Error.Contains("needs a date", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Repo_And_Branch_Keys_Filter_By_Path_And_Git_Branch()
    {
        var parsed = SessionSearchFilter.Parse("repo:/work/harbor branch:dev");

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Matches(Sample())).IsTrue();
        await Assert.That(parsed.Value.Matches(Sample() with { GitBranch = "feature" })).IsFalse();
    }
}
