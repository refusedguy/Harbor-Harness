using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     #401 remainder (B2 core), pinned: the Accepted boundary also holds for
///     calls already in flight. #997 closed the start of NEW calls (both dispatch
///     loops consult the run token); a call that is already running keeps its
///     token via <c>ITool.ExecuteAsync</c>, and a tool that ignores it still
///     reports success — the file it kept writing after Stop reads as work done.
/// </summary>
public class AcceptedGraceAbandonedTests
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private static TestSessionContext NewSession() =>
        new(Session.Create("/tmp/harbor-401b-grace", "code", "test", "test-model"));

    private static ToolCallPart Call(string id, string tool = "writer") =>
        new(id, tool, EmptyArgs);

    /// <summary>
    ///     In-flight tool that ignores its token: signals start, waits for the
    ///     test's release (never the token), then reports success. The
    ///     "keeps writing after Stop" shape.
    /// </summary>
    private sealed class WriteAfterStopTool(
        TaskCompletionSource started,
        TaskCompletionSource release) : ITool
    {
        public ToolName Name => ToolName.Create("writer");

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => "Writer";

        public string Description => "Signals start, waits for release, then succeeds.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public async Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return ToolResult.Success("wrote-file");
        }
    }

    /// <summary>
    ///     A success that lands after the Accepted boundary must not read as
    ///     work done: it is reported Abandoned (error), never silently kept.
    ///     Deterministic via TCS gating — no sleeps; the 10s cap is a hang
    ///     guard, not an assertion clock.
    /// </summary>
    [Test]
    public async Task InFlightSuccess_AfterAcceptedStop_IsMarkedAbandoned()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new WriteAfterStopTool(started, release);
        var bus = new FakeEventBus();
        var agents = new FakeAgentRegistry(TestAgents.AllowAll());
        var dispatcher = new ToolDispatcher(
            new FakeToolRegistry(tool),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            bus,
            NullLogger<ToolDispatcher>.Instance,
            coordinator: null);
        var session = NewSession();

        Task<ToolResultMessage> run = dispatcher.ExecuteAsync(
            [Call("w1")], session, AssistantMessage.Empty(session.Session.Id, "m"),
            TestAgents.AllowAll(), cts.Token);

        await started.Task; // the call is provably in flight
        await cts.CancelAsync(); // the user pressed Stop — the Accepted boundary
        release.TrySetResult(); // the stuck tool finishes anyway

        ToolResultMessage result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(result.Results.Count).IsEqualTo(1);
        await Assert.That(result.Results[0].IsError).IsTrue();
        await Assert.That(result.Results[0].Output).Contains("abandoned");
        await Assert.That(result.Results[0].Output).Contains(ToolDispatcher.AcceptedStopReason);
    }

    /// <summary>
    ///     Calls that ignore the token AND never finish: after the grace period
    ///     the run still terminates with every straggler reported Abandoned.
    ///     The 50ms grace is the injected mechanism parameter, not a sleep in
    ///     an assertion; entry is sequenced by TCS, so the cancel provably
    ///     lands while both calls are in flight.
    /// </summary>
    private sealed class StuckTool(TaskCompletionSource bothStarted) : ITool
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ToolName Name => ToolName.Create("writer");

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => "Writer";

        public string Description => "Never finishes, ignores cancellation.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public async Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 2)
            {
                bothStarted.TrySetResult();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return ToolResult.Success("never");
        }
    }

    [Test]
    public async Task StuckCalls_GraceExpires_ReportsAbandonedAndTerminates()
    {
        using var cts = new CancellationTokenSource();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new StuckTool(bothStarted);
        var bus = new FakeEventBus();
        var agents = new FakeAgentRegistry(TestAgents.AllowAll());
        var dispatcher = new ToolDispatcher(
            new FakeToolRegistry(tool),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            bus,
            NullLogger<ToolDispatcher>.Instance,
            coordinator: null,
            abandonGrace: TimeSpan.FromMilliseconds(50));
        var session = NewSession();

        Task<ToolResultMessage> run = dispatcher.ExecuteAsync(
            [Call("g1"), Call("g2")], session, AssistantMessage.Empty(session.Session.Id, "m"),
            TestAgents.AllowAll(), cts.Token);

        await bothStarted.Task; // both calls provably in flight
        await cts.CancelAsync(); // the Accepted boundary

        // Terminating here IS the assertion that the grace holds: without it
        // this await never returns.
        ToolResultMessage result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(result.Results.Count).IsEqualTo(2);
        await Assert.That(tool.Calls).IsEqualTo(2);
        foreach (ToolResultEntry entry in result.Results)
        {
            await Assert.That(entry.IsError).IsTrue();
            await Assert.That(entry.Output).Contains("abandoned");
            await Assert.That(entry.Output).Contains(ToolDispatcher.AcceptedStopReason);
        }
    }

    /// <summary>
    ///     The Abandoned mark survives into the read-model: per-call completion
    ///     state distinguishes a call the stop outlived from a malfunction.
    /// </summary>
    [Test]
    public async Task Reconstruct_AbandonedEntry_ReportsAbandonedCompletion()
    {
        const string sessionId = "s-401b";
        var t0 = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        AgentMessage assistant = new AssistantMessage(
            "a1", sessionId, t0,
            new ContentPart[]
            {
                new ToolCallPart("tc-ab", "write", EmptyArgs),
                new ToolCallPart("tc-ok", "write", EmptyArgs),
            },
            StopReason.ToolUse, new Usage(10, 5), "m");
        AgentMessage results = new ToolResultMessage(
            "t1", sessionId, t0.AddSeconds(1),
            new[]
            {
                new ToolResultEntry("tc-ab", "write",
                    "Tool 'write' did not finish within 50ms of grace after the accepted stop "
                    + "(accepted-stop); call abandoned after accepted-stop.", true),
                new ToolResultEntry("tc-ok", "write", "file contents", false),
            });

        var outcome = RunOutcome.Reconstruct(
            RunId.New(), sessionId, new[] { assistant, results }, cancelled: true);

        await Assert.That(outcome.StopReason).IsEqualTo(RunStopReason.Stopped);
        await Assert.That(outcome.ToolCalls.Count).IsEqualTo(2);
        await Assert.That(outcome.ToolCalls[0].ToolCallId).IsEqualTo("tc-ab");
        await Assert.That(outcome.ToolCalls[0].IsError).IsTrue();
        await Assert.That(outcome.ToolCalls[0].Completion).IsEqualTo(ToolCallCompletion.Abandoned);
        await Assert.That(outcome.ToolCalls[1].Completion).IsEqualTo(ToolCallCompletion.Completed);
    }
}
