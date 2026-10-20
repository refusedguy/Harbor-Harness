using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Application.Observability;
using Harbor.TestKit;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     Issue #1745 — the harness-side oracle of what a turn changed in the
///     workspace.
/// </summary>
/// <remarks>
///     <para>
///         The oracle's whole claim is that it counts what the WORLD says, not
///         what the agent says. These tests hold that claim down from both
///         sides: a turn that changed a file reports it, a turn that changed
///         nothing reports nothing, and a turn whose assistant message CLAIMS
///         a change reports nothing either — the prose is not an input.
///     </para>
///     <para>
///         The tools are simulated the way the harness sees them: a start
///         event, the file really changing on disk, an end event. The oracle
///         never asks the tool what it did; it reads the disk.
///     </para>
/// </remarks>
public class TurnChangeOracleTests
{
    private const string SessionId = "s1";

    // ── A turn with changes ────────────────────────────────────────────────

    [Test]
    public async Task TurnThatCreatesAFile_ReportsItAsCreated()
    {
        using var temp = new TempDirScope("harbor-1745");
        var bus = new FakeEventBus();
        using var oracle = new TurnChangeOracle(bus) { Root = temp.Path };

        await RunTurnAsync(bus, temp.Path, 1, new ToolCall("write", "notes.md", "hello"));

        TurnChangeReport report = await LastReportAsync(oracle);
        await Assert.That(report.TurnIndex).IsEqualTo(1);
        await Assert.That(report.Files.Count).IsEqualTo(1);
        await Assert.That(report.Files[0].Path).IsEqualTo("notes.md");
        await Assert.That(report.Files[0].Kind).IsEqualTo(WorkspaceChangeKind.Created);
        await Assert.That(report.Files[0].ToolName).IsEqualTo("write");
        await Assert.That(report.IsComplete).IsTrue();
    }

    [Test]
    public async Task TurnThatRewritesAFile_ReportsItAsModified()
    {
        using var temp = new TempDirScope("harbor-1745");
        string existing = temp.Combine("notes.md");
        await File.WriteAllTextAsync(existing, "before");

        var bus = new FakeEventBus();
        using var oracle = new TurnChangeOracle(bus) { Root = temp.Path };

        await RunTurnAsync(bus, temp.Path, 1, new ToolCall("write", "notes.md", "after"));

        TurnChangeReport report = await LastReportAsync(oracle);
        await Assert.That(report.Files.Count).IsEqualTo(1);
        await Assert.That(report.Files[0].Kind).IsEqualTo(WorkspaceChangeKind.Modified);
        await Assert.That(await File.ReadAllTextAsync(existing)).IsEqualTo("after");
    }

    // ── A turn without changes ──────────────────────────────────────────────

    [Test]
    public async Task TurnThatOnlyReads_ReportsNoFilesAtAll()
    {
        using var temp = new TempDirScope("harbor-1745");
        var bus = new FakeEventBus();
        using var oracle = new TurnChangeOracle(bus) { Root = temp.Path };

        await RunTurnAsync(bus, temp.Path, 1, new ToolCall("read", "notes.md", null));

        TurnChangeReport report = await LastReportAsync(oracle);
        await Assert.That(report.Files.Count).IsEqualTo(0);
        await Assert.That(report.UnobservedTools.Count).IsEqualTo(0);
        await Assert.That(report.IsComplete).IsTrue();
    }

    [Test]
    public async Task TurnThatWritesTheSameBytes_ReportsThePathAsUnchanged()
    {
        using var temp = new TempDirScope("harbor-1745");
        string existing = temp.Combine("notes.md");
        await File.WriteAllTextAsync(existing, "same");

        var bus = new FakeEventBus();
        using var oracle = new TurnChangeOracle(bus) { Root = temp.Path };

        await RunTurnAsync(bus, temp.Path, 1, new ToolCall("write", "notes.md", "same"));

        TurnChangeReport report = await LastReportAsync(oracle);
        await Assert.That(report.Files.Count).IsEqualTo(1);
        await Assert.That(report.Files[0].Kind).IsEqualTo(WorkspaceChangeKind.Unchanged)
            .Because(
                "the tool ran and the file says otherwise. This is the verdict the oracle exists "
                + "for: «the agent said it wrote the file» is not «the file changed».");
    }

    // ── The agent's own words ───────────────────────────────────────────────

    [Test]
    public async Task AssistantTextClaimingAChange_MovesNothing()
    {
        using var temp = new TempDirScope("harbor-1745");
        var bus = new FakeEventBus();
        using var oracle = new TurnChangeOracle(bus) { Root = temp.Path };

        await bus.PublishAsync(new TurnStartEvent(1, SessionId));

        // The model says it edited three files. It did not call a tool.
        AssistantMessage claimed = AssistantMessage.Empty(SessionId, "test-model")
            .AppendText("I edited src/a.cs, src/b.cs and src/c.rs.");
        await bus.PublishAsync(new MessageEndEvent(claimed));

        await bus.PublishAsync(new TurnEndEvent(
            claimed, [], SessionId));

        TurnChangeReport report = await LastReportAsync(oracle);
        await Assert.That(report.Files.Count).IsEqualTo(0)
            .Because(
                "the oracle reads the filesystem, not the transcript. A claim in the assistant's text "
                + "is not a change, and treating it as one is the exact failure this oracle is for.");
        await Assert.That(report.IsComplete).IsTrue();
    }

    [Test]
    public async Task ToolThatFailedWithoutTouchingTheFile_ReportsNoChange()
    {
        using var temp = new TempDirScope("harbor-1745");
        var bus = new FakeEventBus();
        using var oracle = new TurnChangeOracle(bus) { Root = temp.Path };

        await RunTurnAsync(bus, temp.Path, 1, new ToolCall("write", "notes.md", null));

        TurnChangeReport report = await LastReportAsync(oracle);
        await Assert.That(report.Files.Count).IsEqualTo(1);
        await Assert.That(report.Files[0].Kind).IsEqualTo(WorkspaceChangeKind.Unchanged);
    }

    // ── Several turns in a row ─────────────────────────────────────────────

    [Test]
    public async Task ConsecutiveTurns_EachGetTheirOwnReport()
    {
        using var temp = new TempDirScope("harbor-1745");
        var bus = new FakeEventBus();
        using var oracle = new TurnChangeOracle(bus) { Root = temp.Path };

        await RunTurnAsync(bus, temp.Path, 1, new ToolCall("write", "first.md", "one"));
        await RunTurnAsync(bus, temp.Path, 2, new ToolCall("write", "second.md", "two"));

        IReadOnlyList<TurnChangeReport> reports = oracle.Reports;
        await Assert.That(reports.Count).IsEqualTo(2);
        await Assert.That(reports[0].TurnIndex).IsEqualTo(1);
        await Assert.That(reports[0].Files[0].Path).IsEqualTo("first.md");
        await Assert.That(reports[1].TurnIndex).IsEqualTo(2);
        await Assert.That(reports[1].Files[0].Path).IsEqualTo("second.md");
        await Assert.That(reports[1].Sequence).IsGreaterThan(reports[0].Sequence)
            .Because("the stamp is what lets a reader ask for the reports it has not shown yet.");
    }

    [Test]
    public async Task TurnThatChangesNothingAfterAPreviousChange_ReportsNoChangeOfItsOwn()
    {
        using var temp = new TempDirScope("harbor-1745");
        var bus = new FakeEventBus();
        using var oracle = new TurnChangeOracle(bus) { Root = temp.Path };

        await RunTurnAsync(bus, temp.Path, 1, new ToolCall("write", "first.md", "one"));
        await RunTurnAsync(bus, temp.Path, 2, new ToolCall("write", "first.md", "one"));

        IReadOnlyList<TurnChangeReport> reports = oracle.Reports;
        await Assert.That(reports.Count).IsEqualTo(2);
        await Assert.That(reports[1].Files.Count).IsEqualTo(1);
        await Assert.That(reports[1].Files[0].Kind).IsEqualTo(WorkspaceChangeKind.Unchanged)
            .Because(
                "the second turn wrote the same bytes the first one wrote. The workspace did not "
                + "change in the second turn, and the report says so.");
    }

    // ── What the oracle cannot see ──────────────────────────────────────────

    [Test]
    public async Task ToolTheOracleCannotSeeThrough_MakesTheTurnIncomplete()
    {
        using var temp = new TempDirScope("harbor-1745");
        var bus = new FakeEventBus();
        using var oracle = new TurnChangeOracle(bus) { Root = temp.Path };

        await RunTurnAsync(bus, temp.Path, 1, new ToolCall("bash", null, null));

        TurnChangeReport report = await LastReportAsync(oracle);
        await Assert.That(report.UnobservedTools.Count).IsEqualTo(1);
        await Assert.That(report.UnobservedTools[0]).IsEqualTo("bash");
        await Assert.That(report.IsComplete).IsFalse()
            .Because(
                "«bash ran» is not «nothing changed». The report says what it saw and that it could "
                + "not see everything.");
    }

    [Test]
    public async Task PathOutsideTheWorkspace_IsReportedAsUnverified()
    {
        using var temp = new TempDirScope("harbor-1745");
        var bus = new FakeEventBus();
        using var oracle = new TurnChangeOracle(bus) { Root = temp.Path };

        await RunTurnAsync(bus, temp.Path, 1, new ToolCall("write", "../escaped.md", null));

        TurnChangeReport report = await LastReportAsync(oracle);
        await Assert.That(report.Files.Count).IsEqualTo(1);
        await Assert.That(report.Files[0].Kind).IsEqualTo(WorkspaceChangeKind.Unverified)
            .Because("the oracle reads inside the workspace it was pointed at, and nowhere else.");
    }

    // ── The plumbing ────────────────────────────────────────────────────────

    [Test]
    public async Task OracleWithoutABus_ReportsNothingAndDoesNotThrow()
    {
        using var oracle = new TurnChangeOracle(null);

        await Assert.That(oracle.Reports.Count).IsEqualTo(0);
        await Assert.That(oracle.LastReport).IsNull();
    }

    // ── The driver ──────────────────────────────────────────────────────────

    /// <summary>
    ///     One tool call the model made.
    /// </summary>
    /// <param name="ToolName">The tool the model called.</param>
    /// <param name="Path">The path the tool declared, when it declared one.</param>
    /// <param name="Content">
    ///     The content the tool wrote, or null when the tool wrote nothing (a
    ///     read, a failed write).
    /// </param>
    private sealed record ToolCall(string ToolName, string? Path, string? Content);

    private static async Task<TurnChangeReport> LastReportAsync(TurnChangeOracle oracle)
    {
        TurnChangeReport? report = oracle.LastReport;
        await Assert.That(report).IsNotNull().Because("the turn published a turn-end event");
        return report!;
    }

    /// <summary>
    ///     Run one turn the way the harness sees it: the turn opens, each tool
    ///     call announces itself, the file really changes on disk, the tool
    ///     finishes, and the turn closes.
    /// </summary>
    private static async Task RunTurnAsync(
        FakeEventBus bus,
        string root,
        int turnIndex,
        params ToolCall[] calls)
    {
        await bus.PublishAsync(new TurnStartEvent(turnIndex, SessionId));

        List<ToolCallPart> parts = [];
        List<ToolResultEntry> results = [];
        int callIndex = 0;

        foreach (ToolCall call in calls)
        {
            callIndex++;
            string id = $"tc{callIndex}";
            using var argsDoc = JsonDocument.Parse(ArgsJson(call));
            parts.Add(ToolCallPart.Create(id, call.ToolName, argsDoc.RootElement));

            await bus.PublishAsync(ToolExecutionStartEvent.Create(id, call.ToolName, argsDoc.RootElement));

            if (call.Content is not null && call.Path is not null)
            {
                string full = Path.Combine(root, call.Path);
                string? parent = Path.GetDirectoryName(full);
                if (parent is not null)
                {
                    Directory.CreateDirectory(parent);
                }

                await File.WriteAllTextAsync(full, call.Content);
            }

            await bus.PublishAsync(new ToolExecutionEndEvent(id, ToolResult.Success("ok"), false));
            results.Add(new ToolResultEntry(id, call.ToolName, "ok", false));
        }

        AssistantMessage message = AssistantMessage.Empty(SessionId, "test-model");
        foreach (ToolCallPart part in parts)
        {
            message = message.AppendToolCall(part);
        }

        var toolResults = new ToolResultMessage("m-results", SessionId, DateTimeOffset.UtcNow, results);
        await bus.PublishAsync(new TurnEndEvent(message, [toolResults], SessionId));
    }

    /// <summary>
    ///     The tool arguments as JSON. Hand-written rather than serialized: the
    ///     test tree does not take the reflection-based serializer.
    /// </summary>
    private static string ArgsJson(ToolCall call)
    {
        if (call.Path is null)
        {
            return "{}";
        }

        string escaped = call.Path.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"{{\"path\":\"{escaped}\"}}";
    }
}
