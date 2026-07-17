using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.RazorConsole;
using Harbor.Tui.SpectreTui;
using Harbor.Tui.TerminalGui;
using Harbor.Tui.Termina;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.E2E.Tests;

/// <summary>
///     E2E tests for the 4 experimental interactive renderers.
///     Tests lifecycle, non-interactive paths, and event-through-renderer
///     resilience (rendering events without starting the interactive loop).
/// </summary>
public class InteractiveRendererE2ETests
{
    private static IEnumerable<AgentEvent> BuildHelloStream()
    {
        var partial = AssistantMessage.Empty("s1", "stub-1");
        yield return new AgentStartEvent("s1", Array.Empty<AgentMessage>());
        yield return new MessageStartEvent(partial);
        yield return new MessageUpdateEvent(new TextDeltaEvent("0", "Hello"), partial);
        yield return new MessageEndEvent(partial);
        yield return new AgentEndEvent(Array.Empty<AgentMessage>());
    }

    private static IEnumerable<AgentEvent> BuildToolCallStream()
    {
        var partial = AssistantMessage.Empty("s2", "stub-2");
        var args = JsonDocument.Parse("""{"path":"src/foo.cs"}""").RootElement;
        yield return new AgentStartEvent("s2", Array.Empty<AgentMessage>());
        yield return new MessageStartEvent(partial);
        yield return new MessageUpdateEvent(new ToolCallStartEvent("tc1", "read"), partial);
        yield return new ToolExecutionStartEvent("tc1", "read", args);
        yield return new ToolExecutionEndEvent("tc1", new ToolResult("file contents", false), false);
        yield return new MessageUpdateEvent(new TextDeltaEvent("1", "Here is the file."), partial);
        yield return new MessageEndEvent(partial);
        yield return new AgentEndEvent(Array.Empty<AgentMessage>());
    }

    private static IEnumerable<AgentEvent> BuildErrorStream()
    {
        yield return new AgentErrorEvent("something went wrong");
        yield return new AgentEndEvent(Array.Empty<AgentMessage>());
    }

    // ── SpectreTui ──

    [Test]
    public async Task SpectreTui_Initialize_Dispose_NoCrash()
    {
        var renderer = new SpectreTui.SpectreTuiRenderer(NullLogger<SpectreTui.SpectreTuiRenderer>.Instance);
        var init = await renderer.InitializeAsync();
        await Assert.That(init.IsSuccess).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task SpectreTui_WriteWriteLineClear_ReturnSuccess()
    {
        var renderer = new SpectreTui.SpectreTuiRenderer(NullLogger<SpectreTui.SpectreTuiRenderer>.Instance);
        await Assert.That((await renderer.InitializeAsync()).IsSuccess).IsTrue();
        await Assert.That((await renderer.WriteAsync("hi")).IsSuccess).IsTrue();
        await Assert.That((await renderer.WriteLineAsync("line")).IsSuccess).IsTrue();
        await Assert.That((await renderer.ClearAsync()).IsSuccess).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task SpectreTui_RenderAsync_HelloStream_NoCrash()
    {
        var renderer = new SpectreTui.SpectreTuiRenderer(NullLogger<SpectreTui.SpectreTuiRenderer>.Instance);
        await renderer.InitializeAsync();
        foreach (var evt in BuildHelloStream())
        {
            await renderer.RenderAsync(evt);
        }
        renderer.Dispose();
    }

    [Test]
    public async Task SpectreTui_RenderAsync_ToolCallStream_NoCrash()
    {
        var renderer = new SpectreTui.SpectreTuiRenderer(NullLogger<SpectreTui.SpectreTuiRenderer>.Instance);
        await renderer.InitializeAsync();
        foreach (var evt in BuildToolCallStream())
        {
            await renderer.RenderAsync(evt);
        }
        renderer.Dispose();
    }

    [Test]
    public async Task SpectreTui_RenderAsync_ErrorStream_NoCrash()
    {
        var renderer = new SpectreTui.SpectreTuiRenderer(NullLogger<SpectreTui.SpectreTuiRenderer>.Instance);
        await renderer.InitializeAsync();
        foreach (var evt in BuildErrorStream())
        {
            await renderer.RenderAsync(evt);
        }
        renderer.Dispose();
    }

    [Test]
    public async Task SpectreTui_ReadLineAsync_ReturnsSuccess()
    {
        var renderer = new SpectreTui.SpectreTuiRenderer(NullLogger<SpectreTui.SpectreTuiRenderer>.Instance);
        var result = await renderer.ReadLineAsync("? ");
        await Assert.That(result.IsSuccess).IsTrue();
        renderer.Dispose();
    }

    // ── TerminalGui ──

    [Test]
    public async Task TerminalGui_Initialize_Dispose_NoCrash()
    {
        var renderer = new TerminalGuiRenderer(NullLogger<TerminalGuiRenderer>.Instance);
        var init = await renderer.InitializeAsync();
        await Assert.That(init.IsSuccess).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task TerminalGui_WriteWriteLineClear_ReturnSuccess()
    {
        var renderer = new TerminalGuiRenderer(NullLogger<TerminalGuiRenderer>.Instance);
        await renderer.InitializeAsync();
        await Assert.That((await renderer.WriteAsync("hi")).IsSuccess).IsTrue();
        await Assert.That((await renderer.WriteLineAsync("line")).IsSuccess).IsTrue();
        await Assert.That((await renderer.ClearAsync()).IsSuccess).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task TerminalGui_RenderAsync_HelloStream_NoCrash()
    {
        var renderer = new TerminalGuiRenderer(NullLogger<TerminalGuiRenderer>.Instance);
        await renderer.InitializeAsync();
        foreach (var evt in BuildHelloStream())
        {
            await renderer.RenderAsync(evt);
        }
        renderer.Dispose();
    }

    [Test]
    public async Task TerminalGui_RenderAsync_ToolCallStream_NoCrash()
    {
        var renderer = new TerminalGuiRenderer(NullLogger<TerminalGuiRenderer>.Instance);
        await renderer.InitializeAsync();
        foreach (var evt in BuildToolCallStream())
        {
            await renderer.RenderAsync(evt);
        }
        renderer.Dispose();
    }

    [Test]
    public async Task TerminalGui_RenderAsync_ErrorStream_NoCrash()
    {
        var renderer = new TerminalGuiRenderer(NullLogger<TerminalGuiRenderer>.Instance);
        await renderer.InitializeAsync();
        foreach (var evt in BuildErrorStream())
        {
            await renderer.RenderAsync(evt);
        }
        renderer.Dispose();
    }

    [Test]
    public async Task TerminalGui_SetSlashHandler_DoesNotThrow()
    {
        var renderer = new TerminalGuiRenderer(NullLogger<TerminalGuiRenderer>.Instance);
        IInteractiveTuiRenderer interactive = renderer;
        interactive.SetSlashHandler(_ => Task.CompletedTask);
        renderer.Dispose();
    }

    // ── Termina ──

    [Test]
    public async Task Termina_Initialize_Dispose_NoCrash()
    {
        var renderer = new TerminaRenderer(NullLogger<TerminaRenderer>.Instance);
        var init = await renderer.InitializeAsync();
        await Assert.That(init.IsSuccess).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task Termina_WriteWriteLineClear_ReturnSuccess()
    {
        var renderer = new TerminaRenderer(NullLogger<TerminaRenderer>.Instance);
        await renderer.InitializeAsync();
        await Assert.That((await renderer.WriteAsync("hi")).IsSuccess).IsTrue();
        await Assert.That((await renderer.WriteLineAsync("line")).IsSuccess).IsTrue();
        await Assert.That((await renderer.ClearAsync()).IsSuccess).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task Termina_RenderAsync_HelloStream_NoCrash()
    {
        var renderer = new TerminaRenderer(NullLogger<TerminaRenderer>.Instance);
        await renderer.InitializeAsync();
        foreach (var evt in BuildHelloStream())
        {
            await renderer.RenderAsync(evt);
        }
        renderer.Dispose();
    }

    [Test]
    public async Task Termina_RenderAsync_ToolCallStream_NoCrash()
    {
        var renderer = new TerminaRenderer(NullLogger<TerminaRenderer>.Instance);
        await renderer.InitializeAsync();
        foreach (var evt in BuildToolCallStream())
        {
            await renderer.RenderAsync(evt);
        }
        renderer.Dispose();
    }

    [Test]
    public async Task Termina_RenderAsync_ErrorStream_NoCrash()
    {
        var renderer = new TerminaRenderer(NullLogger<TerminaRenderer>.Instance);
        await renderer.InitializeAsync();
        foreach (var evt in BuildErrorStream())
        {
            await renderer.RenderAsync(evt);
        }
        renderer.Dispose();
    }

    [Test]
    public async Task Termina_SetSlashHandler_DoesNotThrow()
    {
        var renderer = new TerminaRenderer(NullLogger<TerminaRenderer>.Instance);
        IInteractiveTuiRenderer interactive = renderer;
        interactive.SetSlashHandler(_ => Task.CompletedTask);
        renderer.Dispose();
    }

    // ── RazorConsole ──

    [Test]
    public async Task RazorConsole_Initialize_Dispose_NoCrash()
    {
        var renderer = new RazorConsoleRenderer(NullLogger<RazorConsoleRenderer>.Instance);
        var init = await renderer.InitializeAsync();
        await Assert.That(init.IsSuccess).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task RazorConsole_WriteWriteLineClear_ReturnSuccess()
    {
        var renderer = new RazorConsoleRenderer(NullLogger<RazorConsoleRenderer>.Instance);
        await renderer.InitializeAsync();
        await Assert.That((await renderer.WriteAsync("hi")).IsSuccess).IsTrue();
        await Assert.That((await renderer.WriteLineAsync("line")).IsSuccess).IsTrue();
        await Assert.That((await renderer.ClearAsync()).IsSuccess).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task RazorConsole_RenderAsync_HelloStream_NoCrash()
    {
        var renderer = new RazorConsoleRenderer(NullLogger<RazorConsoleRenderer>.Instance);
        await renderer.InitializeAsync();
        foreach (var evt in BuildHelloStream())
        {
            await renderer.RenderAsync(evt);
        }
        renderer.Dispose();
    }

    [Test]
    public async Task RazorConsole_RenderAsync_ToolCallStream_NoCrash()
    {
        var renderer = new RazorConsoleRenderer(NullLogger<RazorConsoleRenderer>.Instance);
        await renderer.InitializeAsync();
        foreach (var evt in BuildToolCallStream())
        {
            await renderer.RenderAsync(evt);
        }
        renderer.Dispose();
    }

    [Test]
    public async Task RazorConsole_RenderAsync_ErrorStream_NoCrash()
    {
        var renderer = new RazorConsoleRenderer(NullLogger<RazorConsoleRenderer>.Instance);
        await renderer.InitializeAsync();
        foreach (var evt in BuildErrorStream())
        {
            await renderer.RenderAsync(evt);
        }
        renderer.Dispose();
    }

    [Test]
    public async Task RazorConsole_SetSlashHandler_DoesNotThrow()
    {
        var renderer = new RazorConsoleRenderer(NullLogger<RazorConsoleRenderer>.Instance);
        IInteractiveTuiRenderer interactive = renderer;
        interactive.SetSlashHandler(_ => Task.CompletedTask);
        renderer.Dispose();
    }
}
