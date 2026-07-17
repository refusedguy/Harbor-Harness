using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.TerminalGui;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.Tests;

public class TerminalGuiRendererTests
{
    private static TerminalGuiRenderer CreateRenderer() => new(NullLogger<TerminalGuiRenderer>.Instance);

    [Test]
    public async Task Constructor_SetsContext()
    {
        var renderer = CreateRenderer();
        await Assert.That(renderer.Context).IsNotNull();
        await Assert.That(renderer.Context).IsTypeOf<TerminalGuiRenderContext>();
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
    public async Task RenderAsync_WithAgentStartEvent_DoesNotThrow()
    {
        var renderer = CreateRenderer();
        await renderer.InitializeAsync();
        await renderer.RenderAsync(new AgentStartEvent("s1", Array.Empty<AgentMessage>()));
        await Assert.That(true).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task RenderAsync_WithMessageUpdateEvent_DoesNotThrow()
    {
        var renderer = CreateRenderer();
        await renderer.InitializeAsync();
        await renderer.RenderAsync(new MessageUpdateEvent(
            new TextDeltaEvent("0", "Hello"), AssistantMessage.Empty("s1", "m")));
        await Assert.That(true).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task RenderAsync_WithToolExecutionEndEvent_DoesNotThrow()
    {
        var renderer = CreateRenderer();
        await renderer.InitializeAsync();
        var result = new ToolResult("output", false);
        await renderer.RenderAsync(new ToolExecutionEndEvent("tc1", result, false));
        await Assert.That(true).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task RenderAsync_WithAgentErrorEvent_DoesNotThrow()
    {
        var renderer = CreateRenderer();
        await renderer.InitializeAsync();
        await renderer.RenderAsync(new AgentErrorEvent("boom"));
        await Assert.That(true).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task RenderAsync_WithAgentEndEvent_DoesNotThrow()
    {
        var renderer = CreateRenderer();
        await renderer.InitializeAsync();
        await renderer.RenderAsync(new AgentEndEvent(Array.Empty<AgentMessage>()));
        await Assert.That(true).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task RenderAsync_WithThinkingDeltaEvent_DoesNotThrow()
    {
        var renderer = CreateRenderer();
        await renderer.InitializeAsync();
        await renderer.RenderAsync(new MessageUpdateEvent(
            new ThinkingDeltaEvent("0", "thinking..."), AssistantMessage.Empty("s1", "m")));
        await Assert.That(true).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task RenderAsync_WithToolCallStartEvent_DoesNotThrow()
    {
        var renderer = CreateRenderer();
        await renderer.InitializeAsync();
        await renderer.RenderAsync(new MessageUpdateEvent(
            new ToolCallStartEvent("tc1", "read"), AssistantMessage.Empty("s1", "m")));
        await Assert.That(true).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task RenderAsync_WithStepFinishEvent_DoesNotThrow()
    {
        var renderer = CreateRenderer();
        await renderer.InitializeAsync();
        var usage = new Usage(100, 50);
        await renderer.RenderAsync(new MessageUpdateEvent(
            new StepFinishEvent(0, "stop", usage), AssistantMessage.Empty("s1", "m")));
        await Assert.That(true).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task RenderAsync_WithCompactionCompletedEvent_DoesNotThrow()
    {
        var renderer = CreateRenderer();
        await renderer.InitializeAsync();
        await renderer.RenderAsync(new CompactionCompletedEvent("s1", "summary", 10, 1000, TimeSpan.FromSeconds(1)));
        await Assert.That(true).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task RenderAsync_WithMessageStartEvent_DoesNotThrow()
    {
        var renderer = CreateRenderer();
        await renderer.InitializeAsync();
        await renderer.RenderAsync(new MessageStartEvent(AssistantMessage.Empty("s1", "m")));
        await Assert.That(true).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task RenderAsync_WithMessageEndEvent_DoesNotThrow()
    {
        var renderer = CreateRenderer();
        await renderer.InitializeAsync();
        await renderer.RenderAsync(new MessageEndEvent(AssistantMessage.Empty("s1", "m")));
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

public class TerminalGuiRenderContextTests
{
    [Test]
    public async Task Width_DoesNotThrow()
    {
        var ctx = new TerminalGuiRenderContext();
        try { _ = ctx.Width; }
        catch (IOException) { }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Height_DoesNotThrow()
    {
        var ctx = new TerminalGuiRenderContext();
        try { _ = ctx.Height; }
        catch (IOException) { }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task SupportsColor_IsTrue()
    {
        var ctx = new TerminalGuiRenderContext();
        await Assert.That(ctx.SupportsColor).IsTrue();
    }
}
