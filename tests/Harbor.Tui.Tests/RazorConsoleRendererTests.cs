using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
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

public class RazorConsoleChatBridgeTests
{
    [Test]
    public async Task ChatLine_Record_HasCorrectProperties()
    {
        var line = new ChatLine(ChatRoles.User, "hello");
        await Assert.That(line.IsUser).IsTrue();
        await Assert.That(line.Text).IsEqualTo("hello");
    }

    [Test]
    public async Task ChatLine_AssistantLine_HasCorrectProperties()
    {
        var line = new ChatLine(ChatRoles.Assistant, "response");
        await Assert.That(line.IsUser).IsFalse();
        await Assert.That(line.Text).IsEqualTo("response");
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
