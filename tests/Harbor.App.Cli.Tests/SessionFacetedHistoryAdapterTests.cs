using Harbor.Abstractions.Models;
using Harbor.App.Cli.Commands;
using Harbor.Storage.Memory;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Behaviour of the <c>FacetedHistoryState</c> adapter used by
///     <c>harbor sessions search</c>. The same filter tokens the CLI accepts
///     must select sessions exactly as the old criterion list did, and the
///     plain-text output must stay byte-for-byte stable.
/// </summary>
public class SessionFacetedHistoryAdapterTests
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly MemorySessionStore _store = new();

    private static Session Sample() => Session.Create(
        directory: "/work/harbor",
        agentName: "code",
        providerId: "kilocode",
        modelId: "kilocode/tencent/hy3:free",
        title: "Bug Triage")
    with
    {
        Id = "abc123def456",
        ProjectId = "proj-42",
        CreatedAt = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero),
        Status = SessionStatus.Done,
        GitBranch = "dev",
        ParentSessionId = "parent-9",
        Kind = SessionKind.Subagent,
    };

    private static IReadOnlyList<string> VisibleIds(string expression, params Session[] sessions)
    {
        var parsed = SessionSearchFilter.Parse(expression);
        if (parsed.IsFailure)
            throw new InvalidOperationException(parsed.Error);

        var state = SessionFacetedHistoryAdapter.Build(sessions, parsed.Value);
        return state.Visible().Select(entry => entry.Id).ToList();
    }

    [Test]
    public async Task Id_And_Bare_Value_Filter_By_SessionId_Substring()
    {
        Session sample = Sample();

        await Assert.That(VisibleIds("id:abc123", sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("ABC123", sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("id:zzz", sample)).IsEmpty();
    }

    [Test]
    public async Task Title_Filter_Is_CaseInsensitive_Substring()
    {
        Session sample = Sample();

        await Assert.That(VisibleIds("label:bug", sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("title:TRIAGE", sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("label:plan", sample)).IsEmpty();
    }

    [Test]
    public async Task Facets_Filter_By_CaseInsensitive_Substring()
    {
        Session sample = Sample();

        await Assert.That(VisibleIds("agent:CODE", sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("model:hy3", sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("provider:KILO", sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("project:proj-42", sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("repo:/work/harbor", sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("branch:DEV", sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("parent:parent-9", sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("model:gpt", sample)).IsEmpty();
    }

    [Test]
    public async Task Status_And_Kind_Compare_Exact_Value_CaseInsensitively()
    {
        Session sample = Sample();

        await Assert.That(VisibleIds("status:DONE", sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("kind:subagent", sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("status:working", sample)).IsEmpty();
    }

    [Test]
    public async Task After_And_Before_Bound_CreatedAt()
    {
        Session sample = Sample();

        await Assert.That(VisibleIds("after:2026-01-01 before:2027-01-01", sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("after:2026-07-01", sample)).IsEmpty();
        await Assert.That(VisibleIds("before:2026-01-01", sample)).IsEmpty();
    }

    [Test]
    public async Task Quotes_And_And_Across_Different_Keys()
    {
        Session sample = Sample();

        string expression = "label:\"bug triage\" after:2026-01-01 agent:code";
        await Assert.That(VisibleIds(expression, sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("label:bug agent:plan", sample)).IsEmpty();
    }

    [Test]
    public async Task Repeated_Key_Keeps_Its_And_Meaning()
    {
        Session sample = Sample();

        await Assert.That(VisibleIds("model:hy3 model:kilo", sample)).IsEquivalentTo([sample.Id]);
        await Assert.That(VisibleIds("model:hy3 model:gpt", sample)).IsEmpty();
    }

    [Test]
    public async Task Empty_Filter_Selects_All_In_Input_Order()
    {
        Session a = Sample() with { Id = "a" };
        Session b = Sample() with { Id = "b" };
        var parsed = SessionSearchFilter.Parse(null);

        var state = SessionFacetedHistoryAdapter.Build([a, b], parsed.Value);

        await Assert.That(state.TotalCount).IsEqualTo(2);
        await Assert.That(state.Cursor).IsEqualTo(0);
        await Assert.That(state.Visible().Select(entry => entry.Id)).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task Runner_Output_Is_ByteForByte_Stable()
    {
        _out.NewLine = "\n";
        var when = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var message = new UserMessage("msg-1", "s", when, "needle in text", "code", "test-model");
        Session session = await SessionFixtures.SeedAsync(_store, "Bug Triage", message);

        int exit = await SessionSearchRunner.RunAsync(_out, _err, _store, "needle", "label:bug agent:CODE");

        await Assert.That(exit).IsEqualTo(0);
        string expected =
            $"[Bug Triage] {session.Id} · user · msg-1 @ 2026-06-15 12:00:00\n" +
            "    …needle in text…\n" +
            "\n" +
            "1 match(es) for 'needle'.\n";
        await Assert.That(_out.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task Empty_Result_Output_Is_ByteForByte_Stable()
    {
        _out.NewLine = "\n";
        await SessionFixtures.SeedAsync(
            _store, "Bug Triage",
            new UserMessage("msg-1", "s", DateTimeOffset.UtcNow, "needle in text", "code", "test-model"));

        int exit = await SessionSearchRunner.RunAsync(_out, _err, _store, "needle", "label:missing");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(_out.ToString()).IsEqualTo("No matches for 'needle'.\n");
    }
}
