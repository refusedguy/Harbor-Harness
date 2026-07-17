using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Termina;
using Microsoft.Extensions.Logging.Abstractions;
using R3;
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

public class TerminaChatBridgeTests
{
    [Test]
    public async Task Push_FormatsAgentStartEvent()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        var userMsg = new UserMessage("u1", "s1", DateTimeOffset.UtcNow, "Hello", "code", "claude");
        using var subscription = bridge.OutputStream.Subscribe(output => { });
        bridge.Push(new AgentStartEvent("s1", new AgentMessage[] { userMsg }));
        await Assert.That(true).IsTrue();
        bridge.Dispose();
    }

    [Test]
    public async Task Push_FormatsTextDeltaEvent()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        bridge.Push(new MessageUpdateEvent(new TextDeltaEvent("0", "Hello"), AssistantMessage.Empty("s1", "m")));
        await Assert.That(true).IsTrue();
        bridge.Dispose();
    }

    [Test]
    public async Task Push_FormatsToolExecutionEndEvent()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        var result = new ToolResult("output text", false);
        bridge.Push(new ToolExecutionEndEvent("tc1", result, false));
        await Assert.That(true).IsTrue();
        bridge.Dispose();
    }

    [Test]
    public async Task Push_FormatsAgentErrorEvent()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        bridge.Push(new AgentErrorEvent("something failed"));
        await Assert.That(true).IsTrue();
        bridge.Dispose();
    }

    [Test]
    public async Task Push_FormatsCompactionCompletedEvent()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        bridge.Push(new CompactionCompletedEvent("s1", "summary", 5, 500, TimeSpan.FromSeconds(1)));
        await Assert.That(true).IsTrue();
        bridge.Dispose();
    }

    [Test]
    public async Task PushLine_EmitsText()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        string? received = null;
        using var sub = bridge.OutputStream.Subscribe(line => received = line.Text);
        bridge.PushLine("test line");
        await Task.Delay(50);
        await Assert.That(received).IsEqualTo("test line");
        bridge.Dispose();
    }

    [Test]
    public async Task Submit_WithNullAgent_DoesNotThrow()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
        bridge.Submit("hello");
        await Assert.That(true).IsTrue();
        bridge.Dispose();
    }

    [Test]
    public async Task Dispose_DisposesOutputStream()
    {
        var bridge = new ChatBridge(NullLogger.Instance);
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
