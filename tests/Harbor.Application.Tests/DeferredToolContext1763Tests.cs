using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TestSessionContext = Harbor.TestKit.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     #1763: a tool has TWO exits. The ordinary one returns a result. The
///     second one hands the loop a <em>deferred context</em> — context the
///     TOOL produced, which the loop injects into the history AFTER the
///     result it belongs to.
/// </summary>
/// <remarks>
///     <para>
///         The order is the contract, and it is not cosmetic. A context
///         describes what a tool saw, so the model must read it after the
///         result: before it, the context would read as the reason for a
///         result the model has not seen; between two results, it would break
///         the tool_call/tool_result pairing every provider insists on
///         (#1751).
///     </para>
///     <para>
///         The third case is the one that decides whether the feature is
///         safe: a tool that PROMISED a context and never delivered one. The
///         result stands on its own, the run continues, and the miss is
///         reported — a tool may not hold the loop hostage over a context
///         that is not coming.
///     </para>
/// </remarks>
public class DeferredToolContext1763Tests
{
    /// <summary>
    ///     A tool that returns whatever result the test scripted — the seam
    ///     the deferred-context exit is exercised through.
    /// </summary>
    /// <param name="result">The result the call produces.</param>
    /// <param name="name">The tool's name.</param>
    private sealed class ScriptedResultTool(Func<ToolResult> result, string name = "counter") : ITool
    {
        public ToolName Name => ToolName.Create(name);

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => name;

        public string Description => "Returns the result the test scripted.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result());
    }

    private static AgentLoop NewLoop(ScriptedLlmClient client, params ITool[] tools)
    {
        var agent = TestAgents.AllowAll();
        var agents = new FakeAgentRegistry(agent);
        return new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(tools),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(new ManualTimeProvider(fireTimerOnCreate: true)),
            new FakeEventBus(),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
    }

    private static TestSessionContext NewSession() =>
        new(Session.Create("/tmp/harbor-1763-deferred-context", "code", "test", "test-model"));

    /// <summary>
    ///     A client that calls a tool once and then answers with text.
    /// </summary>
    /// <param name="toolName">The tool the first turn asks for.</param>
    private static ScriptedLlmClient ToolThenText(string toolName = "counter") => new(
    [
        [
            TestEvents.ToolCallStart(toolName: toolName),
            TestEvents.ToolCallDelta(),
            TestEvents.StepFinish(finishReason: "tool_use", usage: new Usage(3, 4))
        ],
        [
            TestEvents.TextDelta(delta: "done"),
            TestEvents.StepFinish(usage: new Usage(1, 1))
        ]
    ]);

    /// <summary>
    ///     The text of the deferred-context message the model reads, or
    ///     <see langword="null" /> when the request carries none.
    /// </summary>
    /// <param name="request">The request the model was sent.</param>
    /// <returns>The framed context, or null.</returns>
    private static string? DeferredContextText(LlmRequest request)
    {
        for (int i = 0; i < request.Messages.Count; i++)
        {
            if (request.Messages[i] is LlmUserMessage user)
            {
                for (int b = 0; b < user.Content.Count; b++)
                {
                    if (user.Content[b] is LlmTextBlock text && text.Text.Contains(DeferredToolContext.Marker, StringComparison.Ordinal))
                    {
                        return text.Text;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     The index of the tool-result message in the request, or -1.
    /// </summary>
    /// <param name="request">The request the model was sent.</param>
    /// <returns>The index of the first tool-result message.</returns>
    private static int ToolResultIndex(LlmRequest request)
    {
        for (int i = 0; i < request.Messages.Count; i++)
        {
            if (request.Messages[i] is LlmToolResultMessage)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    ///     The index of the deferred-context message in the request, or -1.
    /// </summary>
    /// <param name="request">The request the model was sent.</param>
    /// <returns>The index of the deferred-context message.</returns>
    private static int DeferredContextIndex(LlmRequest request)
    {
        for (int i = 0; i < request.Messages.Count; i++)
        {
            if (request.Messages[i] is LlmUserMessage user)
            {
                for (int b = 0; b < user.Content.Count; b++)
                {
                    if (user.Content[b] is LlmTextBlock text && text.Text.Contains(DeferredToolContext.Marker, StringComparison.Ordinal))
                    {
                        return i;
                    }
                }
            }
        }

        return -1;
    }

    [Test]
    public async Task OrdinaryResult_ReachesTheModel_WithNoDeferredContext()
    {
        // The first exit: the tool returns a result and nothing else. The
        // model must see the result and NOTHING that looks like an
        // observation — otherwise every tool call would grow a phantom turn.
        var tool = new ScriptedResultTool(() => ToolResult.Success("plain output"));
        var client = ToolThenText();
        var loop = NewLoop(client, tool);
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(client.Requests.Count).IsEqualTo(2);

        var second = client.Requests[1];
        await Assert.That(ToolResultIndex(second)).IsGreaterThanOrEqualTo(0)
            .Because("the tool result is the answer to the tool call and must be in the next request.");
        await Assert.That(DeferredContextIndex(second)).IsEqualTo(-1)
            .Because("the tool handed over no context, so the model must see none.");
    }

    [Test]
    public async Task DeferredContext_ReachesTheModel_AfterTheResult()
    {
        // The second exit: the tool returns a result AND a deferred context.
        // The context must land after the result — that is the whole promise
        // of the feature, and the order is what the model reads.
        var tool = new ScriptedResultTool(() => ToolResult.Success("plain output") with
        {
            DeferredContexts = new[]
            {
                new DeferredToolContext("3 warnings in the changed files", "linter")
            }
        });
        var client = ToolThenText();
        var loop = NewLoop(client, tool);
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(client.Requests.Count).IsEqualTo(2);

        var second = client.Requests[1];
        int resultIndex = ToolResultIndex(second);
        int contextIndex = DeferredContextIndex(second);
        await Assert.That(resultIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(contextIndex).IsGreaterThan(resultIndex)
            .Because("the context describes what the tool saw — the model must read the result first.");

        string? text = DeferredContextText(second);
        await Assert.That(text).IsNotNull();
        await Assert.That(text!).Contains(DeferredToolContext.Marker);
        await Assert.That(text!).Contains("linter")
            .Because("the context keeps its own source, so the model can tell it from the user's words.");
        await Assert.That(text!).Contains("3 warnings in the changed files");
    }

    [Test]
    public async Task DeferredContextThatNeverArrived_LeavesTheResultStanding_AndTheRunContinues()
    {
        // The tool PROMISED a context (an empty list is the promise) and
        // delivered none — its producer failed, timed out, or was cancelled.
        // The result must stand on its own, the model must see no context,
        // and the run must reach its normal end.
        var tool = new ScriptedResultTool(() => ToolResult.Success("plain output") with
        {
            DeferredContexts = Array.Empty<DeferredToolContext>()
        });
        var client = ToolThenText();
        var loop = NewLoop(client, tool);
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue()
            .Because("a context that never arrived is not a reason to fail the run.");
        await Assert.That(client.Requests.Count).IsEqualTo(2)
            .Because("the run continued past the tool turn and reached the text turn.");

        var second = client.Requests[1];
        await Assert.That(ToolResultIndex(second)).IsGreaterThanOrEqualTo(0)
            .Because("the result is the whole truth when the context is missing.");
        await Assert.That(DeferredContextIndex(second)).IsEqualTo(-1)
            .Because("no context arrived, so none may be invented.");
    }

    [Test]
    public async Task DeferredContext_SurvivesTheSession_AsPartOfTheResult()
    {
        // The context is persisted with the result it belongs to, so a
        // session that is reloaded still carries it.
        var tool = new ScriptedResultTool(() => ToolResult.Success("plain output") with
        {
            DeferredContexts = new[]
            {
                new DeferredToolContext("3 warnings in the changed files", "linter")
            }
        });
        var client = ToolThenText();
        var loop = NewLoop(client, tool);
        var session = NewSession();

        await loop.RunAsync(session, TestAgents.AllowAll());

        var results = session.Messages.OfType<ToolResultMessage>().ToList();
        await Assert.That(results.Count).IsEqualTo(1);
        var entry = results[0].Results.Single();
        await Assert.That(entry.DeferredContexts).IsNotNull();
        var contexts = entry.DeferredContexts!;
        await Assert.That(contexts.Count).IsEqualTo(1);
        await Assert.That(contexts[0].Content).IsEqualTo("3 warnings in the changed files");
        await Assert.That(contexts[0].Source).IsEqualTo("linter");
    }

    [Test]
    public async Task TwoCalls_DeferredContextsFollowEveryResult_InCallOrder()
    {
        // Two tool calls in one turn, each with a context. The contexts come
        // after BOTH results — never between them, which would break the
        // tool_call/tool_result pairing every provider insists on (#1751) —
        // and among themselves they keep the call order.
        var first = new ScriptedResultTool(() => ToolResult.Success("first output") with
        {
            DeferredContexts = new[] { new DeferredToolContext("context A", "tool_a") }
        }, "tool_a");
        var second = new ScriptedResultTool(() => ToolResult.Success("second output") with
        {
            DeferredContexts = new[] { new DeferredToolContext("context B", "tool_b") }
        }, "tool_b");

        var client = new ScriptedLlmClient(
        [
            [
                TestEvents.ToolCallStart(id: "call-1", toolName: "tool_a"),
                TestEvents.ToolCallDelta(id: "call-1"),
                TestEvents.ToolCallStart(id: "call-2", toolName: "tool_b"),
                TestEvents.ToolCallDelta(id: "call-2"),
                TestEvents.StepFinish(finishReason: "tool_use", usage: new Usage(3, 4))
            ],
            [
                TestEvents.TextDelta(delta: "done"),
                TestEvents.StepFinish(usage: new Usage(1, 1))
            ]
        ]);
        var loop = NewLoop(client, first, second);
        var session = NewSession();

        var result = await loop.RunAsync(session, TestAgents.AllowAll());

        await Assert.That(result.IsSuccess).IsTrue();

        var second2 = client.Requests[1];
        int lastResultIndex = -1;
        for (int i = 0; i < second2.Messages.Count; i++)
        {
            if (second2.Messages[i] is LlmToolResultMessage)
            {
                lastResultIndex = i;
            }
        }

        await Assert.That(lastResultIndex).IsGreaterThanOrEqualTo(0);
        int contextIndex = DeferredContextIndex(second2);
        await Assert.That(contextIndex).IsGreaterThan(lastResultIndex)
            .Because("a context between two results would break the tool_call/tool_result pairing.");

        string? text = DeferredContextText(second2);
        await Assert.That(text).IsNotNull();
        await Assert.That(text!).Contains("context A");
        await Assert.That(text!).Contains("context B");
        await Assert.That(text!.IndexOf("context A", StringComparison.Ordinal))
            .IsLessThan(text!.IndexOf("context B", StringComparison.Ordinal))
            .Because("the contexts keep the call order.");
    }

    [Test]
    public async Task Converter_FramesTheContext_SoItDoesNotReadLikeTheUser()
    {
        // The framing is what keeps the context from reading as the user's
        // own words: the marker opens it and the source names it.
        var converter = new MessageConverter();
        var entry = new ToolResultEntry("call-1", "linter", "3 warnings", false)
        {
            DeferredContexts = new[]
            {
                new DeferredToolContext(
                    "file a.cs: 3 warnings",
                    "linter",
                    new[] { new DeferredContextMetadata("kind", "diagnostics") })
            }
        };
        var message = TestMessages.ToolResult([entry]);

        var converted = converter.ToLlmMessages([message]);

        await Assert.That(converted.Count).IsEqualTo(2);
        await Assert.That(converted[0]).IsTypeOf<LlmToolResultMessage>();
        await Assert.That(converted[1]).IsTypeOf<LlmUserMessage>();

        var user = (LlmUserMessage)converted[1];
        var text = (LlmTextBlock)user.Content[0];
        await Assert.That(text.Text).Contains(DeferredToolContext.Marker);
        await Assert.That(text.Text).Contains("source=linter");
        await Assert.That(text.Text).Contains("kind=diagnostics");
        await Assert.That(text.Text).Contains("file a.cs: 3 warnings");
    }

    [Test]
    public async Task Converter_LeavesTheOrdinaryResultAlone()
    {
        // A result with no deferred context must produce exactly the messages
        // it produced before the feature existed — no extra turn, no framing.
        var converter = new MessageConverter();
        var entry = new ToolResultEntry("call-1", "linter", "3 warnings", false);
        var message = TestMessages.ToolResult([entry]);

        var converted = converter.ToLlmMessages([message]);

        await Assert.That(converted.Count).IsEqualTo(1);
        await Assert.That(converted[0]).IsTypeOf<LlmToolResultMessage>();
    }
}
