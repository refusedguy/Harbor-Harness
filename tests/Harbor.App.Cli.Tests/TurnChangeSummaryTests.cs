using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.App.Cli.Repl;
using Harbor.Application.Observability;
using Harbor.TestKit;
using TUnit.Assertions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Issue #1745 — the line the REPL and <c>harbor ask</c> draw from the
///     oracle's report, and the rule that keeps a quiet turn quiet.
/// </summary>
/// <remarks>
///     <para>
///         The quiet rule is not cosmetic. The REPL appends the line as a system
///         block, and the feed's expand gesture targets the NEWEST collapsible
///         block — so a line on every turn would steal the gesture from the
///         answer the user is trying to expand. A turn where nothing the oracle
///         could measure happened prints nothing, and the gesture stays where
///         it was.
///     </para>
/// </remarks>
public class TurnChangeSummaryTests
{
    [Test]
    public async Task HostWithoutAnOracle_PrintsNothing()
    {
        var summary = new TurnChangeSummary(null);

        await Assert.That(summary.TakeNewLines()).IsEmpty();
    }

    [Test]
    public async Task TurnWithNoToolCalls_PrintsNothing()
    {
        var bus = new FakeEventBus();
        using var oracle = new TurnChangeOracle(bus);
        var summary = new TurnChangeSummary(oracle);

        await RunTurnAsync(bus, []);

        await Assert.That(summary.TakeNewLines()).IsEmpty()
            .Because(
                "a turn that touched no file and ran no tool the oracle cannot see through has "
                + "nothing to report, and the chat must not gain a line for it.");
    }

    [Test]
    public async Task TurnThatDeclaredAWrite_NamesThePath()
    {
        var bus = new FakeEventBus();
        using var oracle = new TurnChangeOracle(bus);
        var summary = new TurnChangeSummary(oracle);

        await RunTurnAsync(bus, [new ToolCall("write", "notes.md")]);

        IReadOnlyList<string> lines = summary.TakeNewLines();
        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("notes.md");
    }

    [Test]
    public async Task TurnThatRanAToolTheOracleCannotSeeThrough_NamesTheTool()
    {
        var bus = new FakeEventBus();
        using var oracle = new TurnChangeOracle(bus);
        var summary = new TurnChangeSummary(oracle);

        await RunTurnAsync(bus, [new ToolCall("bash", null)]);

        IReadOnlyList<string> lines = summary.TakeNewLines();
        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("bash");
    }

    [Test]
    public async Task AReportIsPrintedOnce()
    {
        var bus = new FakeEventBus();
        using var oracle = new TurnChangeOracle(bus);
        var summary = new TurnChangeSummary(oracle);

        await RunTurnAsync(bus, [new ToolCall("write", "notes.md")]);

        await Assert.That(summary.TakeNewLines().Count).IsEqualTo(1);
        await Assert.That(summary.TakeNewLines().Count).IsEqualTo(0)
            .Because("the same turn must not be announced twice.");
    }

    /// <summary>
    ///     One tool call the model made.
    /// </summary>
    private sealed record ToolCall(string ToolName, string? Path);

    /// <summary>
    ///     Run one turn: the turn opens, each tool call announces itself, the
    ///     turn closes. Nothing is written to disk — the oracle reports the
    ///     path it was told about, which is all these tests read.
    /// </summary>
    private static async Task RunTurnAsync(FakeEventBus bus, IReadOnlyList<ToolCall> calls)
    {
        const string session = "s1";
        await bus.PublishAsync(TestEvents.TurnStart(sessionId: session));

        AssistantMessage message = AssistantMessage.Empty(session, "test-model");
        int index = 0;
        foreach (ToolCall call in calls)
        {
            index++;
            string id = $"tc{index}";
            string args = call.Path is null
                ? "{}"
                : $"{{\"path\":\"{call.Path}\"}}";
            using var argsDoc = JsonDocument.Parse(args);
            message = message.AppendToolCall(ToolCallPart.Create(id, call.ToolName, argsDoc.RootElement));
            await bus.PublishAsync(ToolExecutionStartEvent.Create(id, call.ToolName, argsDoc.RootElement));
            await bus.PublishAsync(TestEvents.ToolExecutionEnd(id, ToolResult.Success("ok")));
        }

        await bus.PublishAsync(TestEvents.TurnEnd(message, sessionId: session));
    }
}
