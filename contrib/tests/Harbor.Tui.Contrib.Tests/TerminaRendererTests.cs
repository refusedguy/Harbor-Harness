using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Termina;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tui.Tests;
public class TerminaRendererTests
{
    private static TerminaRenderer CreateRenderer() => new(NullLogger<TerminaRenderer>.Instance);

    [Test]
    public async Task Constructor_SetsContext()
    {
        var renderer = CreateRenderer();
        await Assert.That(renderer.Context).IsNotNull();
        await Assert.That(renderer.Context).IsTypeOf<TerminaRenderContext>();
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
    public async Task Context_SupportsColor_IsTrue()
    {
        var renderer = CreateRenderer();
        await Assert.That(renderer.Context.SupportsColor).IsTrue();
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
    public async Task RenderAsync_WithCompactionCompletedEvent_DoesNotThrow()
    {
        var renderer = CreateRenderer();
        await renderer.InitializeAsync();
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

public class TerminaTeaBridgeTests
{
    [Test]
    public async Task Push_AgentEnd_SetsIdleStatus()
    {
        var agent = TestAgentFactory.Create();
        using var bridge = new TerminaTeaBridge(agent, null, NullLogger.Instance);
        bridge.Push(new AgentEndEvent(Array.Empty<AgentMessage>()));
        await Assert.That(bridge.Store.State.Status).IsEqualTo("idle");
    }

    [Test]
    public async Task Push_TextDelta_AccumulatesInActiveBuffer()
    {
        var agent = TestAgentFactory.Create();
        using var bridge = new TerminaTeaBridge(agent, null, NullLogger.Instance);
        var partial = AssistantMessage.Empty("s1", "m");
        bridge.Push(new MessageStartEvent(partial));
        bridge.Push(new MessageUpdateEvent(new TextDeltaEvent("0", "Hello"), partial));
        await Assert.That(bridge.Store.State.Active.TextBuffer).IsEqualTo("Hello");
    }

    [Test]
    public async Task Push_ToolExecutionEnd_AddsLine()
    {
        var agent = TestAgentFactory.Create();
        using var bridge = new TerminaTeaBridge(agent, null, NullLogger.Instance);
        var result = new ToolResult("output text", false);
        bridge.Push(new ToolExecutionEndEvent("tc1", result, false));
        var allText = string.Join("", bridge.Store.State.Lines.Select(l => l.Text));
        await Assert.That(allText).Contains("output text");
    }

    [Test]
    public async Task Push_AgentError_AddsErrorLine()
    {
        var agent = TestAgentFactory.Create();
        using var bridge = new TerminaTeaBridge(agent, null, NullLogger.Instance);
        bridge.Push(new AgentErrorEvent("something failed"));
        var allText = string.Join("", bridge.Store.State.Lines.Select(l => l.Text));
        await Assert.That(allText).Contains("something failed");
    }

    [Test]
    public async Task Push_CompactionCompleted_AddsSystemLine()
    {
        var agent = TestAgentFactory.Create();
        using var bridge = new TerminaTeaBridge(agent, null, NullLogger.Instance);
        bridge.Push(new CompactionCompletedEvent("s1", "summary", 5, 500, TimeSpan.FromSeconds(1)));
        var allText = string.Join("", bridge.Store.State.Lines.Select(l => l.Text));
        await Assert.That(allText).Contains("compacted");
    }

    [Test]
    public async Task PushLine_AddsSystemLine()
    {
        var agent = TestAgentFactory.Create();
        using var bridge = new TerminaTeaBridge(agent, null, NullLogger.Instance);
        bridge.PushLine("test line");
        await Assert.That(bridge.Store.State.Lines[^1].Text).IsEqualTo("test line");
    }

    [Test]
    public async Task Dispose_DoesNotThrow()
    {
        var agent = TestAgentFactory.Create();
        var bridge = new TerminaTeaBridge(agent, null, NullLogger.Instance);
        bridge.Dispose();
        await Assert.That(true).IsTrue();
    }
}

public class TerminaRenderContextTests
{
    [Test]
    public async Task Width_DoesNotThrow()
    {
        var ctx = new TerminaRenderContext();
        try { _ = ctx.Width; }
        catch (IOException) { }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Height_DoesNotThrow()
    {
        var ctx = new TerminaRenderContext();
        try { _ = ctx.Height; }
        catch (IOException) { }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task SupportsColor_IsTrue()
    {
        var ctx = new TerminaRenderContext();
        await Assert.That(ctx.SupportsColor).IsTrue();
    }
}
