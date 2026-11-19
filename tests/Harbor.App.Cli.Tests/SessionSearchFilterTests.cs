using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2675 item 010: syntax of the session-search filter language. Assertions
///     target the parsed tokens; selecting sessions is covered by
///     <see cref="SessionFacetedHistoryAdapterTests"/>.
/// </summary>
public class SessionSearchFilterTests
{
    [Test]
    public async Task Empty_Expression_Has_No_Tokens()
    {
        var parsed = SessionSearchFilter.Parse("   ");

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.IsEmpty).IsTrue();
        await Assert.That(parsed.Value.Tokens.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Bare_Value_Stays_Legacy_SessionId_Substring()
    {
        var parsed = SessionSearchFilter.Parse("abc123");

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Tokens.Count).IsEqualTo(1);
        await Assert.That(parsed.Value.Tokens[0].Key).IsEqualTo("id");
        await Assert.That(parsed.Value.Tokens[0].Value).IsEqualTo("abc123");
    }

    [Test]
    public async Task Two_Bare_Values_Are_Ambiguous()
    {
        var parsed = SessionSearchFilter.Parse("abc def");

        await Assert.That(parsed.IsFailure).IsTrue();
        await Assert.That(parsed.Error.Contains("Ambiguous session-id filter", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Different_Keys_Become_Separate_Tokens()
    {
        var parsed = SessionSearchFilter.Parse("label:bug agent:code status:done");

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Tokens.Select(t => t.Key)).IsEquivalentTo(["label", "agent", "status"]);
        await Assert.That(parsed.Value.Tokens.Select(t => t.Value)).IsEquivalentTo(["bug", "code", "done"]);
    }

    [Test]
    public async Task Tokens_Are_Normalised_To_Lower_Case()
    {
        var parsed = SessionSearchFilter.Parse("LABEL:Bug agent:CODE");

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Tokens[0].Key).IsEqualTo("label");
        await Assert.That(parsed.Value.Tokens[0].Value).IsEqualTo("bug");
        await Assert.That(parsed.Value.Tokens[1].Value).IsEqualTo("code");
    }

    [Test]
    public async Task Every_Token_Gets_A_Unique_Slot()
    {
        var parsed = SessionSearchFilter.Parse("after:2026-01-01 before:2027-01-01 agent:code");

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Tokens.Select(t => t.Slot)).IsEquivalentTo([0, 1, 2]);
    }

    [Test]
    public async Task Quoted_Value_Keeps_Spaces()
    {
        var parsed = SessionSearchFilter.Parse("label:\"bug triage\"");

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Tokens[0].Value).IsEqualTo("bug triage");
    }

    [Test]
    public async Task Date_Tokens_Carry_Parsed_Instant()
    {
        var parsed = SessionSearchFilter.Parse("after:2026-01-01 before:2027-01-01");

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Tokens[0].Date).IsNotNull();
        await Assert.That(parsed.Value.Tokens[0].Date!.Value.Year).IsEqualTo(2026);
        await Assert.That(parsed.Value.Tokens[1].Date!.Value.Year).IsEqualTo(2027);
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
}
