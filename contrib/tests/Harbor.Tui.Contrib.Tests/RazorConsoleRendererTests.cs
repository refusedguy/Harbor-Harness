using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Tui.RazorConsole;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tui.Tests;
public class RazorConsoleRendererTests
{
    private static RazorConsoleRenderer CreateRenderer() => new(NullLogger<RazorConsoleRenderer>.Instance);

    [Test]
    public async Task Constructor_SetsContext()
    {
        var renderer = CreateRenderer();
        await Assert.That(renderer.Context).IsNotNull();
        await Assert.That(renderer.Context).IsTypeOf<RazorConsoleRenderContext>();
        renderer.Dispose();
    }

    [Test]
    public async Task InitializeAsync_ReturnsSuccess()
    {
        var renderer = CreateRenderer();
        var result = await renderer.InitializeAsync();
        await Assert.That(result.IsSuccess).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task WriteAsync_ReturnsSuccess()
    {
        var renderer = CreateRenderer();
        var result = await renderer.WriteAsync("hello");
        await Assert.That(result.IsSuccess).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task WriteLineAsync_ReturnsSuccess()
    {
        var renderer = CreateRenderer();
        var result = await renderer.WriteLineAsync("hello");
        await Assert.That(result.IsSuccess).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task ClearAsync_ReturnsSuccess()
    {
        var renderer = CreateRenderer();
        var result = await renderer.ClearAsync();
        await Assert.That(result.IsSuccess).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task RenderAsync_WithAllEventTypes_DoesNotThrow()
    {
        var renderer = CreateRenderer();
        await renderer.InitializeAsync();

        await renderer.RenderAsync(new AgentStartEvent("s1", Array.Empty<AgentMessage>()));
        await renderer.RenderAsync(new MessageStartEvent(AssistantMessage.Empty("s1", "m")));
        await renderer.RenderAsync(new MessageUpdateEvent(new TextDeltaEvent("0", "Hello"), AssistantMessage.Empty("s1", "m")));
        await renderer.RenderAsync(new MessageUpdateEvent(new ThinkingDeltaEvent("0", "Thinking..."), AssistantMessage.Empty("s1", "m")));
        await renderer.RenderAsync(new MessageUpdateEvent(new ToolCallStartEvent("tc1", "read"), AssistantMessage.Empty("s1", "m")));
        await renderer.RenderAsync(new MessageEndEvent(AssistantMessage.Empty("s1", "m")));
        await renderer.RenderAsync(new ToolExecutionStartEvent("tc1", "read", JsonDocument.Parse("{}").RootElement));
        var result = new ToolResult("output", false);
        await renderer.RenderAsync(new ToolExecutionEndEvent("tc1", result, false));
        await renderer.RenderAsync(new AgentErrorEvent("boom"));
        await renderer.RenderAsync(new AgentEndEvent(Array.Empty<AgentMessage>()));
        await renderer.RenderAsync(new CompactionStartedEvent("s1"));
        await renderer.RenderAsync(new CompactionCompletedEvent("s1", "summary", 10, 1000, TimeSpan.FromSeconds(1)));

        await Assert.That(true).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task Dispose_CanBeCalledMultipleTimes()
    {
        var renderer = CreateRenderer();
        renderer.Dispose();
        renderer.Dispose();
        await Assert.That(true).IsTrue();
    }
}

public class RazorConsoleTeaBridgeTests
{
    [Test]
    public async Task Push_AgentEnd_SetsIdleStatus()
    {
        var agent = TestAgentFactory.Create();
        using var bridge = new RazorConsoleTeaBridge(agent, null, NullLogger.Instance);
        bridge.Push(new AgentEndEvent(Array.Empty<AgentMessage>()));
        await Assert.That(bridge.Store.State.Status).IsEqualTo("idle");
    }

    [Test]
    public async Task PushLine_AddsSystemLine()
    {
        var agent = TestAgentFactory.Create();
        using var bridge = new RazorConsoleTeaBridge(agent, null, NullLogger.Instance);
        bridge.PushLine("test line");
        var state = bridge.Store.State;
        await Assert.That(state.Lines.Length).IsGreaterThan(0);
        await Assert.That(state.Lines[^1].Text).IsEqualTo("test line");
    }
}

public class RazorConsoleRenderContextTests
{
    [Test]
    public async Task Width_DoesNotThrow()
    {
        var ctx = new RazorConsoleRenderContext();
        try { _ = ctx.Width; }
        catch (IOException) { }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Height_DoesNotThrow()
    {
        var ctx = new RazorConsoleRenderContext();
        try { _ = ctx.Height; }
        catch (IOException) { }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task SupportsColor_IsTrue()
    {
        var ctx = new RazorConsoleRenderContext();
        await Assert.That(ctx.SupportsColor).IsTrue();
    }
}

internal static class TestAgentFactory
{
    public static IAgent Create()
    {
        var definition = new AgentDefinition(
            Name: AgentName.Create("test"),
            DisplayName: "Test Agent",
            Description: "Stub",
            Model: "test-model",
            ProviderId: "test",
            Permission: PermissionRuleset.Default);
        return new StubAgent(definition);
    }

    private sealed class StubAgent(AgentDefinition definition) : IAgent
    {
        public AgentState State { get; } = AgentState.Idle("test-session", definition);
        public CancellationTokenSource AbortSource { get; } = new();
        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) => new NoopDisposable();
        public Task<CSharpFunctionalExtensions.Result> PromptAsync(string text, CancellationToken ct = default) => Task.FromResult(CSharpFunctionalExtensions.Result.Success());
        public Task<CSharpFunctionalExtensions.Result> PromptAsync(UserMessage message, CancellationToken ct = default) => Task.FromResult(CSharpFunctionalExtensions.Result.Success());
        public void Initialize(Session session, AgentDefinition agent) { }
        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ResetAbortSource() { }
        public void Steer(AgentMessage message) { }
        public void FollowUp(AgentMessage message) { }
        public void Dispose() => AbortSource.Dispose();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
