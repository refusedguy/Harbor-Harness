using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Tui.RazorConsole;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.E2E.Tests;

/// <summary>
///     Tests the ChatBridge event formatting logic shared by RazorConsole.
///     Verifies that agent events are correctly translated to user-visible
///     text lines (role prefixes, tool names, error messages, etc.).
/// </summary>
public class RazorConsoleChatBridgeE2ETests
{
    /// <summary>
    ///     Create a minimal IAgent stub via the Abstractions types.
    ///     We test ChatBridge.ApplyEvent indirectly by calling PushLine
    ///     and verifying the Messages list.
    /// </summary>
    [Test]
    public async Task ChatBridge_PushLine_UserMessage_AppearsInMessages()
    {
        var stubAgent = new StubAgent();
        var bridge = new Harbor.Tui.RazorConsole.ChatBridge(stubAgent, null, NullLogger.Instance);

        bridge.PushLine("user", "hello from user");

        await Assert.That(bridge.Messages).HasCount(1);
        await Assert.That(bridge.Messages[0].IsUser).IsTrue();
        await Assert.That(bridge.Messages[0].Text).IsEqualTo("hello from user");
    }

    [Test]
    public async Task ChatBridge_PushLine_AssistantMessage_AppearsInMessages()
    {
        var stubAgent = new StubAgent();
        var bridge = new Harbor.Tui.RazorConsole.ChatBridge(stubAgent, null, NullLogger.Instance);

        bridge.PushLine("assistant", "hello from assistant");

        await Assert.That(bridge.Messages).HasCount(1);
        await Assert.That(bridge.Messages[0].IsUser).IsFalse();
        await Assert.That(bridge.Messages[0].Text).IsEqualTo("hello from assistant");
    }

    [Test]
    public async Task ChatBridge_ApplyEvent_AgentStart_PopulatesMessages()
    {
        var stubAgent = new StubAgent();
        var bridge = new Harbor.Tui.RazorConsole.ChatBridge(stubAgent, null, NullLogger.Instance);

        var userMsg = new UserMessage(Guid.NewGuid().ToString("N"), "s1", DateTimeOffset.UtcNow, "test prompt", "code", "stub-model");
        bridge.ApplyEvent(new AgentStartEvent("s1", new AgentMessage[] { userMsg }));

        await Assert.That(bridge.Messages).HasCount(1);
        await Assert.That(bridge.Messages[0].IsUser).IsTrue();
        await Assert.That(bridge.Messages[0].Text).IsEqualTo("test prompt");
        await Assert.That(bridge.Status).IsEqualTo("running");
    }

    [Test]
    public async Task ChatBridge_ApplyEvent_MessageEnd_CompletesStreaming()
    {
        var stubAgent = new StubAgent();
        var bridge = new Harbor.Tui.RazorConsole.ChatBridge(stubAgent, null, NullLogger.Instance);

        var partial = AssistantMessage.Empty("s1", "stub");
        bridge.ApplyEvent(new MessageStartEvent(partial));
        bridge.ApplyEvent(new MessageUpdateEvent(new TextDeltaEvent("0", "Hello world"), partial));
        bridge.ApplyEvent(new MessageEndEvent(partial));

        var assistantMessages = bridge.Messages.Where(m => !m.IsUser).ToList();
        await Assert.That(assistantMessages).IsNotEmpty();
        await Assert.That(assistantMessages.Any(m => m.Text.Contains("Hello world"))).IsTrue();
    }

    [Test]
    public async Task ChatBridge_ApplyEvent_ToolExecutionEnd_AddsResultMessage()
    {
        var stubAgent = new StubAgent();
        var bridge = new Harbor.Tui.RazorConsole.ChatBridge(stubAgent, null, NullLogger.Instance);

        var result = ToolResult.Success("file contents here");
        bridge.ApplyEvent(new ToolExecutionEndEvent("tc1", result, false));

        var toolMessages = bridge.Messages.Where(m => !m.IsUser).ToList();
        await Assert.That(toolMessages).IsNotEmpty();
        await Assert.That(toolMessages.Any(m => m.Text.Contains("file contents here"))).IsTrue();
    }

    [Test]
    public async Task ChatBridge_ApplyEvent_AgentError_SetsErrorStatus()
    {
        var stubAgent = new StubAgent();
        var bridge = new Harbor.Tui.RazorConsole.ChatBridge(stubAgent, null, NullLogger.Instance);

        bridge.ApplyEvent(new AgentErrorEvent("boom"));

        await Assert.That(bridge.Status).IsEqualTo("error");
        var errorMessages = bridge.Messages.Where(m => !m.IsUser).ToList();
        await Assert.That(errorMessages.Any(m => m.Text.Contains("boom"))).IsTrue();
    }

    [Test]
    public async Task ChatBridge_ApplyEvent_AgentEnd_SetsIdleStatus()
    {
        var stubAgent = new StubAgent();
        var bridge = new Harbor.Tui.RazorConsole.ChatBridge(stubAgent, null, NullLogger.Instance);

        bridge.ApplyEvent(new AgentErrorEvent("err"));
        await Assert.That(bridge.Status).IsEqualTo("error");

        bridge.ApplyEvent(new AgentEndEvent(Array.Empty<AgentMessage>()));
        await Assert.That(bridge.Status).IsEqualTo("idle");
    }

    [Test]
    public async Task ChatBridge_QuitRequested_OnExit()
    {
        var stubAgent = new StubAgent();
        var bridge = new Harbor.Tui.RazorConsole.ChatBridge(stubAgent, null, NullLogger.Instance);

        await bridge.SendAsync("exit");

        await Assert.That(bridge.QuitRequested).IsTrue();
    }

    [Test]
    public async Task ChatBridge_EmptyInput_DoesNothing()
    {
        var stubAgent = new StubAgent();
        var bridge = new Harbor.Tui.RazorConsole.ChatBridge(stubAgent, null, NullLogger.Instance);

        await bridge.SendAsync("");
        await bridge.SendAsync("   ");

        await Assert.That(bridge.Messages).IsEmpty();
    }

    [Test]
    public async Task ChatBridge_MultipleEvents_BuildsTranscript()
    {
        var stubAgent = new StubAgent();
        var bridge = new Harbor.Tui.RazorConsole.ChatBridge(stubAgent, null, NullLogger.Instance);

        var userMsg = new UserMessage(Guid.NewGuid().ToString("N"), "s1", DateTimeOffset.UtcNow, "first prompt", "code", "stub-model");
        bridge.ApplyEvent(new AgentStartEvent("s1", new AgentMessage[] { userMsg }));

        var partial = AssistantMessage.Empty("s1", "stub");
        bridge.ApplyEvent(new MessageStartEvent(partial));
        bridge.ApplyEvent(new MessageUpdateEvent(new TextDeltaEvent("0", "Response"), partial));
        bridge.ApplyEvent(new MessageEndEvent(partial));

        bridge.ApplyEvent(new AgentEndEvent(Array.Empty<AgentMessage>()));

        await Assert.That(bridge.Messages.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(bridge.Status).IsEqualTo("idle");
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
        public void Dispose() { }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
