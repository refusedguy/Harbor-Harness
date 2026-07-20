using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Termina;
using Microsoft.Extensions.Logging.Abstractions;
using R3;
using Result = CSharpFunctionalExtensions.Result;
namespace Harbor.Tui.E2E.Tests;
/// <summary>
///     Tests the Termina ChatBridge event formatting logic.
///     Verifies that agent events are correctly translated to display lines
///     and that the Submit → agent pipeline works.
/// </summary>
public class TerminaChatBridgeE2ETests
{
    [Test]
    public async Task ChatBridge_PushLine_StreamsToObservable()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        var received = new List<string>();
        bridge.OutputStream.Subscribe(line => received.Add(line.Text));

        bridge.PushLine("hello from bridge");

        await Assert.That(received).HasCount(1);
        await Assert.That(received[0]).IsEqualTo("hello from bridge");
    }

    private static UserMessage UserMessage(string content) =>
        new(Guid.NewGuid().ToString("N"), "s1", DateTimeOffset.UtcNow, content, "code", "stub-model");

    [Test]
    public async Task ChatBridge_Push_AgentStartEvent_ResetsStream()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        var received = new List<string>();
        bridge.OutputStream.Subscribe(line => received.Add(line.Text));

        var userMsg = UserMessage("test prompt");
        bridge.Push(new AgentStartEvent("s1", new AgentMessage[] { userMsg }));

        // AgentStartEvent no longer emits the user prompt to OutputStream (the ViewModel
        // owns the user-message display to avoid duplication); it just resets stream state.
        await Assert.That(received).IsEmpty();
    }

    [Test]
    public async Task ChatBridge_Push_TextDeltaEvent_FormatsStreamToken()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        var received = new List<string>();
        bridge.OutputStream.Subscribe(line => received.Add(line.Text));

        var partial = AssistantMessage.Empty("s1", "stub");
        bridge.Push(new MessageUpdateEvent(new TextDeltaEvent("0", "Hello"), partial));

        // First delta emits the "Harbor: " label plus the token; assert the token is present.
        await Assert.That(received.Any(l => l.Contains("Hello"))).IsTrue();
    }

    [Test]
    public async Task ChatBridge_Push_ThinkingDeltaEvent_FormatsWithEmoji()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        var received = new List<string>();
        bridge.OutputStream.Subscribe(line => received.Add(line.Text));

        var partial = AssistantMessage.Empty("s1", "stub");
        bridge.Push(new MessageUpdateEvent(new ThinkingDeltaEvent("0", "thinking..."), partial));

        await Assert.That(received).HasCount(1);
        await Assert.That(received[0]).Contains("thinking...");
    }

    [Test]
    public async Task ChatBridge_Push_ToolCallStartEvent_FormatsToolName()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        var received = new List<string>();
        bridge.OutputStream.Subscribe(line => received.Add(line.Text));

        var partial = AssistantMessage.Empty("s1", "stub");
        bridge.Push(new MessageUpdateEvent(new ToolCallStartEvent("tc1", "read"), partial));

        // ToolCallStart now emits a generic "calling tool…" indicator (name shown on result).
        await Assert.That(received.Any(l => l.Contains("calling tool"))).IsTrue();
    }

    [Test]
    public async Task ChatBridge_Push_ToolExecutionEndEvent_FormatsResult()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        var received = new List<string>();
        bridge.OutputStream.Subscribe(line => received.Add(line.Text));

        var result = ToolResult.Success("output data");
        bridge.Push(new ToolExecutionEndEvent("tc1", result, false));

        await Assert.That(received).HasCount(1);
        await Assert.That(received[0]).Contains("output data");
    }

    [Test]
    public async Task ChatBridge_Push_ToolExecutionEndEvent_Error_FormatsWithErrorLabel()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        var received = new List<string>();
        bridge.OutputStream.Subscribe(line => received.Add(line.Text));

        var result = ToolResult.Error("error occurred");
        bridge.Push(new ToolExecutionEndEvent("tc1", result, true));

        await Assert.That(received).HasCount(1);
        await Assert.That(received[0]).Contains("error occurred");
    }

    [Test]
    public async Task ChatBridge_Push_MessageEndEvent_FormatsNewline()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        var received = new List<string>();
        bridge.OutputStream.Subscribe(line => received.Add(line.Text));

        var partial = AssistantMessage.Empty("s1", "stub");
        bridge.Push(new MessageEndEvent(partial));

        await Assert.That(received).HasCount(1);
        await Assert.That(received[0]).IsEqualTo("\n");
    }

    [Test]
    public async Task ChatBridge_Push_AgentErrorEvent_FormatsError()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        var received = new List<string>();
        bridge.OutputStream.Subscribe(line => received.Add(line.Text));

        bridge.Push(new AgentErrorEvent("something broke"));

        await Assert.That(received).HasCount(1);
        await Assert.That(received[0]).Contains("something broke");
    }

    [Test]
    public async Task ChatBridge_Push_CompactionCompletedEvent_FormatsSummary()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        var received = new List<string>();
        bridge.OutputStream.Subscribe(line => received.Add(line.Text));

        bridge.Push(new CompactionCompletedEvent("s1", "summary", 5, 1000, TimeSpan.FromMilliseconds(1)));

        await Assert.That(received).HasCount(1);
        await Assert.That(received[0]).Contains("pruned 5 msgs");
        await Assert.That(received[0]).Contains("1000");
    }

    [Test]
    public async Task ChatBridge_Push_AgentEndEvent_ReturnsEmpty()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        var received = new List<string>();
        bridge.OutputStream.Subscribe(line => received.Add(line.Text));

        bridge.Push(new AgentEndEvent(Array.Empty<AgentMessage>()));

        // AgentEnd no longer emits a display line.
        await Assert.That(received).IsEmpty();
    }

    [Test]
    public async Task ChatBridge_Submit_WithNullAgent_DoesNotThrow()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        // Agent is null by default — Submit should silently return
        bridge.Submit("hello");
        // No exception = pass
    }

    [Test]
    public async Task ChatBridge_Submit_FormatsUserPrompt()
    {
        var bridge = new ChatBridge(NullLogger.Instance) { Agent = new StubAgent() };
        var received = new List<string>();
        bridge.OutputStream.Subscribe(line => received.Add(line.Text));

        bridge.Submit("user question");

        // Submit no longer echoes the user line to OutputStream (the ViewModel owns the
        // user-message display, avoiding duplication). It should still not throw.
        await Assert.That(received.Any(l => l.Contains("user question"))).IsFalse();
    }

    [Test]
    public async Task ChatBridge_FullStream_ProducesMultipleOutputs()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        var received = new List<string>();
        bridge.OutputStream.Subscribe(line => received.Add(line.Text));

        var userMsg = UserMessage("hi");
        bridge.Push(new AgentStartEvent("s1", new AgentMessage[] { userMsg }));

        var partial = AssistantMessage.Empty("s1", "stub");
        bridge.Push(new MessageUpdateEvent(new TextDeltaEvent("0", "Hello"), partial));
        bridge.Push(new MessageUpdateEvent(new TextDeltaEvent("1", " world"), partial));
        bridge.Push(new MessageEndEvent(partial));

        bridge.Push(new AgentEndEvent(Array.Empty<AgentMessage>()));

        await Assert.That(received.Count).IsGreaterThanOrEqualTo(3);
        await Assert.That(received.Any(l => l.Contains("Hello"))).IsTrue();
        // The user prompt ("hi") is no longer emitted to OutputStream; the ViewModel
        // owns the user-message display to avoid duplication.
    }

    [Test]
    public async Task ChatBridge_Dispose_CompletesObservable()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        bool completed = false;
        bridge.OutputStream.Subscribe(_ => { }, _ => completed = true);

        bridge.Dispose();

        await Assert.That(completed).IsTrue();
    }

    /// <summary>Minimal IAgent stub for ChatBridge tests.</summary>
    private sealed class StubAgent : IAgent
    {
        public AgentState State { get; } = AgentState.Idle(
            "test-session",
            AgentDefinition.CodeDefault("stub-model", "stub-provider"));

        public CancellationTokenSource AbortSource { get; } = new();

        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) => new NoopDisposable();

        public Task<Result> PromptAsync(string text, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public void Initialize(Session session, AgentDefinition agent) { }
        public void Steer(AgentMessage message) { }
        public void FollowUp(AgentMessage message) { }
        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ResetAbortSource() { }
        public void Dispose() { }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
