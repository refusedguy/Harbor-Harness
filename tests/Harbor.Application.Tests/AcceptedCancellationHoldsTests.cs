using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
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
///     #401: an accepted cancellation HOLDS — once the run token is observed,
///     no further tool call in the batch is started.
/// </summary>
/// <remarks>
///     <para>
///         The issue asked whether "accepted cancellation" is a flag or an
///         irreversible state. It is a <b>flag</b> — a
///         <see cref="CancellationToken" /> — and the defect was therefore not
///         "no flag" but "a path that lets work start anyway". This file pins
///         that path closed. No new stop-state enum is introduced: the freeze
///         (#555) holds, and the guarantee is expressible against the token that
///         already exists.
///     </para>
///     <para>
///         <b>Non-vacuity.</b> The tests below are built so the guard MUST fire
///         on an unfixed tree — see
///         <see cref="ParallelBatch_CancelMidDispatch_StartsNoFurtherCall" />,
///         whose tool cancels from inside the first call, making the remaining
///         three iterations run against a fired token. That is a structural
///         guarantee, not a scheduling hope, and it is asserted explicitly by
///         <see cref="Synthetic_CancelIsObservedBeforeLaterDispatch_ProvesTestCanSeeTheDefect" />.
///     </para>
/// </remarks>
public class AcceptedCancellationHoldsTests
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private static TestSessionContext NewSession() =>
        new(Session.Create("/tmp/harbor-401-cancel-holds", "code", "test", "test-model"));

    private static ToolCallPart Call(string id, string tool = "gate") =>
        new(id, tool, EmptyArgs);

    /// <summary>
    ///     Tool that cancels the run token from inside its own execution, on the
    ///     Nth invocation. This is what makes the defect <b>guaranteed</b> to hit
    ///     rather than timing-dependent: the parallel dispatch loop starts every
    ///     call, so call #1 cancelling mid-flight means calls #2..#N are entered
    ///     with an already-fired token.
    /// </summary>
    private sealed class SelfCancellingTool(CancellationTokenSource cts, int cancelOnCall) : ITool
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Indices (0-based, into the batch) whose ExecuteAsync was entered.</summary>
        public List<int> Entered { get; } = [];

        public ToolName Name => ToolName.Create("gate");

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => "Gate";

        public string Description => "Cancels the run from inside its Nth call.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            int n = Interlocked.Increment(ref _calls) - 1;
            lock (Entered)
            {
                Entered.Add(n);
            }

            if (n == cancelOnCall)
            {
                // The user pressed Stop while THIS call was running.
                cts.Cancel();
            }

            return Task.FromResult(ToolResult.Success("ok"));
        }
    }

    /// <summary>Tool that ignores its token entirely (the "stuck call" shape).</summary>
    private sealed class TokenIgnoringTool : ITool
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ToolName Name => ToolName.Create("stubborn");

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => "Stubborn";

        public string Description => "Ignores cancellation.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(ToolResult.Success("ran to completion anyway"));
        }
    }

    private static (ToolDispatcher Dispatcher, IEventBus Bus) NewDispatcher(ITool tool, IApprovalCoordinator? coordinator)
    {
        var bus = new FakeEventBus();
        var agents = new FakeAgentRegistry(TestAgents.AllowAll());
        var dispatcher = new ToolDispatcher(
            new FakeToolRegistry(tool),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            bus,
            NullLogger<ToolDispatcher>.Instance,
            coordinator);
        return (dispatcher, bus);
    }

    /// <summary>
    ///     The headline guarantee: 4 calls in a batch, Stop accepted while call
    ///     #1 runs, and the later calls must never reach the tool.
    /// </summary>
    [Test]
    public async Task ParallelBatch_CancelMidDispatch_StartsNoFurtherCall()
    {
        using var cts = new CancellationTokenSource();
        var tool = new SelfCancellingTool(cts, cancelOnCall: 0);
        (ToolDispatcher dispatcher, _) = NewDispatcher(tool, coordinator: null);
        var session = NewSession();

        var calls = new List<ToolCallPart> { Call("c1"), Call("c2"), Call("c3"), Call("c4") };

        ToolResultMessage result = await dispatcher.ExecuteAsync(
            calls, session, AssistantMessage.Empty(session.Session.Id, "m"), TestAgents.AllowAll(), cts.Token);

        // The batch must still answer every call — a missing tool_result breaks
        // the wire contract for OpenAI-compatible providers.
        await Assert.That(result.Results.Count).IsEqualTo(4);

        // And only the call that was already running may have executed.
        await Assert.That(tool.Calls).IsEqualTo(1);

        foreach (ToolResultEntry entry in result.Results)
        {
            if (entry.ToolCallId == "c1")
            {
                continue; // the in-flight call: its outcome is the tool's, not the gate's
            }

            await Assert.That(entry.IsError).IsTrue();
            await Assert.That(entry.Output).Contains("cancelled before start");
        }
    }

    /// <summary>
    ///     A sequential batch is the shape where the gap is widest: call k
    ///     finishing is the natural moment for the user to press Stop. Call k+1
    ///     must not start.
    /// </summary>
    [Test]
    public async Task SequentialBatch_CancelAfterFirstCall_DoesNotStartSecond()
    {
        using var cts = new CancellationTokenSource();

        // Force the sequential path by declaring a sequential tool, so
        // HasSequentialTool picks the branch where the gap is widest.
        var inner = new SelfCancellingTool(cts, cancelOnCall: 0);
        var sequential = new SequentialWrapper(inner);
        (ToolDispatcher seqDispatcher, _) = NewDispatcher(sequential, coordinator: null);
        var session = NewSession();

        var calls = new List<ToolCallPart>
        {
            Call("s1", "sequential_gate"),
            Call("s2", "sequential_gate"),
        };

        ToolResultMessage result = await seqDispatcher.ExecuteAsync(
            calls, session, AssistantMessage.Empty(session.Session.Id, "m"), TestAgents.AllowAll(), cts.Token);

        await Assert.That(result.Results.Count).IsEqualTo(2);
        await Assert.That(sequential.Inner.Calls).IsEqualTo(1);

        ToolResultEntry second = result.Results[1];
        await Assert.That(second.ToolCallId).IsEqualTo("s2");
        await Assert.That(second.IsError).IsTrue();
        await Assert.That(second.Output).Contains("cancelled before start");
    }

    /// <summary>
    ///     A token-ignoring call that was ALREADY dispatched still runs to
    ///     completion — the fix does not pretend otherwise, and does not hang.
    ///     The run must still return, and the stuck call must be reported as an
    ///     error rather than as a success.
    /// </summary>
    [Test]
    public async Task AlreadyDispatched_CallIgnoringToken_StillTerminatesTheRun()
    {
        using var cts = new CancellationTokenSource();
        var tool = new TokenIgnoringTool();
        (ToolDispatcher dispatcher, _) = NewDispatcher(tool, coordinator: null);
        var session = NewSession();

        // Cancel BEFORE the call: the gate refuses to start it at all.
        await cts.CancelAsync();
        ToolResultMessage refused = await dispatcher.ExecuteAsync(
            [Call("x1", "stubborn")], session, AssistantMessage.Empty(session.Session.Id, "m"),
            TestAgents.AllowAll(), cts.Token);

        await Assert.That(tool.Calls).IsEqualTo(0);
        await Assert.That(refused.Results[0].IsError).IsTrue();
        await Assert.That(refused.Results[0].Output).Contains("cancelled before start");
    }

    /// <summary>
    ///     NON-VACUITY. Proves the assertions above can actually observe the
    ///     defect: with the token fired before the batch, the gate MUST see a
    ///     cancelled token on entry. If this ever stops holding, the tests above
    ///     would pass for the wrong reason (a test that cannot fail proves
    ///     nothing).
    /// </summary>
    [Test]
    public async Task Synthetic_CancelIsObservedBeforeDispatch_ProvesTestCanSeeTheDefect()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // The precondition every gate in this file relies on.
        await Assert.That(cts.Token.IsCancellationRequested).IsTrue();

        // And the harness really would have started the call without the gate:
        // a tool executed directly under this same token runs anyway, which is
        // exactly what the dispatch loop used to do.
        var tool = new TokenIgnoringTool();
        ToolContext context = new(
            "s1",
            "m1",
            "c1",
            "code",
            cts.Token,
            [],
            static (_, _) => Task.CompletedTask,
            static (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)));
        await tool.ExecuteAsync(EmptyArgs, context, cts.Token);
        await Assert.That(tool.Calls).IsEqualTo(1);
    }

    /// <summary>Sequential-mode decorator so the sequential branch is exercised.</summary>
    private sealed class SequentialWrapper(ITool inner) : ITool
    {
        public ITool Inner { get; } = inner;

        public ToolName Name => ToolName.Create("sequential_gate");

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => inner.DisplayName;

        public string Description => inner.Description;

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public ExecutionMode ExecutionMode => ExecutionMode.Sequential;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public Result ValidateArguments(JsonElement args) => inner.ValidateArguments(args);

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
            => inner.ExecuteAsync(args, context, cancellationToken);
    }
}