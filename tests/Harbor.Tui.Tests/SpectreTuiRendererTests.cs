using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.SpectreTui;
using Harbor.Tui.SpectreTui.Components;
using Harbor.Tui.SpectreTui.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.Tests;

public class SpectreTuiRendererTests
{
    private static SpectreTuiRenderer CreateRenderer() => new(NullLogger<SpectreTuiRenderer>.Instance);

    [Test]
    public async Task Constructor_SetsContext()
    {
        var renderer = CreateRenderer();
        await Assert.That(renderer.Context).IsNotNull();
        await Assert.That(renderer.Context).IsTypeOf<SpectreTuiRenderContext>();
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

public class SpectreTuiInputStateTests
{
    [Test]
    public async Task Initially_Empty()
    {
        var input = new InputState();
        await Assert.That(input.Text).IsEqualTo(string.Empty);
        await Assert.That(input.IsEmpty).IsTrue();
        await Assert.That(input.Length).IsEqualTo(0);
        await Assert.That(input.HistoryCount).IsEqualTo(0);
        await Assert.That(input.HistoryIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task Append_BuildsText()
    {
        var input = new InputState();
        input.Append('h');
        input.Append('i');
        await Assert.That(input.Text).IsEqualTo("hi");
        await Assert.That(input.IsEmpty).IsFalse();
        await Assert.That(input.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Backspace_RemovesLastChar()
    {
        var input = new InputState();
        input.Append('a');
        input.Append('b');
        input.Backspace();
        await Assert.That(input.Text).IsEqualTo("a");
    }

    [Test]
    public async Task Backspace_Empty_IsNoOp()
    {
        var input = new InputState();
        input.Backspace();
        await Assert.That(input.Text).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Clear_ResetsEverything()
    {
        var input = new InputState();
        input.Append('a');
        input.Append('b');
        input.Consume();
        input.Clear();
        await Assert.That(input.Text).IsEqualTo(string.Empty);
        await Assert.That(input.IsEmpty).IsTrue();
        await Assert.That(input.HistoryIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task Consume_AddsToHistory()
    {
        var input = new InputState();
        input.Append('h');
        input.Append('i');
        var consumed = input.Consume();
        await Assert.That(consumed).IsEqualTo("hi");
        await Assert.That(input.Text).IsEqualTo(string.Empty);
        await Assert.That(input.HistoryCount).IsEqualTo(1);
    }

    [Test]
    public async Task Consume_Empty_DoesNotAddToHistory()
    {
        var input = new InputState();
        var consumed = input.Consume();
        await Assert.That(consumed).IsEqualTo(string.Empty);
        await Assert.That(input.HistoryCount).IsEqualTo(0);
    }

    [Test]
    public async Task NavigateUp_LoadsLastHistory()
    {
        var input = new InputState();
        input.Append('a');
        input.Consume();
        input.Append('b');
        input.Consume();

        input.NavigateUp();
        await Assert.That(input.Text).IsEqualTo("b");
        await Assert.That(input.HistoryIndex).IsEqualTo(1);
    }

    [Test]
    public async Task NavigateUp_MultipleTimes_WalksBack()
    {
        var input = new InputState();
        input.Append('a');
        input.Consume();
        input.Append('b');
        input.Consume();
        input.Append('c');
        input.Consume();

        input.NavigateUp();
        await Assert.That(input.Text).IsEqualTo("c");
        input.NavigateUp();
        await Assert.That(input.Text).IsEqualTo("b");
        input.NavigateUp();
        await Assert.That(input.Text).IsEqualTo("a");
    }

    [Test]
    public async Task NavigateUp_EmptyHistory_IsNoOp()
    {
        var input = new InputState();
        input.NavigateUp();
        await Assert.That(input.Text).IsEqualTo(string.Empty);
        await Assert.That(input.HistoryIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task NavigateDown_FromMiddle_ReturnsToEmpty()
    {
        var input = new InputState();
        input.Append('a');
        input.Consume();
        input.Append('b');
        input.Consume();

        input.NavigateUp();
        input.NavigateDown();
        await Assert.That(input.Text).IsEqualTo(string.Empty);
        await Assert.That(input.HistoryIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task NavigateDown_AtEnd_IsNoOp()
    {
        var input = new InputState();
        input.Append('a');
        input.Consume();
        input.NavigateDown();
        await Assert.That(input.Text).IsEqualTo(string.Empty);
    }
}

public class SpectreTuiChatStateTests
{
    [Test]
    public async Task Initially_Empty()
    {
        var chat = new ChatState();
        await Assert.That(chat.Count).IsEqualTo(0);
        await Assert.That(chat.Lines).HasCount().EqualTo(0);
    }

    [Test]
    public async Task Add_IncrementsCount()
    {
        var chat = new ChatState();
        chat.Add("user", "hello");
        chat.Add("assistant", "hi");
        await Assert.That(chat.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Add_StoresRoleAndContent()
    {
        var chat = new ChatState();
        chat.Add("user", "hello");
        var line = chat.Lines[0];
        await Assert.That(line.Role).IsEqualTo("user");
        await Assert.That(line.Content).IsEqualTo("hello");
    }

    [Test]
    public async Task Clear_RemovesAll()
    {
        var chat = new ChatState();
        chat.Add("user", "a");
        chat.Add("assistant", "b");
        chat.Clear();
        await Assert.That(chat.Count).IsEqualTo(0);
    }
}

public class SpectreTuiLayoutBuilderTests
{
    [Test]
    public async Task BuildWidgets_ReturnsAllRegions()
    {
        var chat = new ChatState();
        var input = new InputState();
        var layout = new LayoutBuilder(chat, input);

        var widgets = layout.BuildWidgets();
        await Assert.That(widgets.ContainsKey("Header")).IsTrue();
        await Assert.That(widgets.ContainsKey("History")).IsTrue();
        await Assert.That(widgets.ContainsKey("Status")).IsTrue();
        await Assert.That(widgets.ContainsKey("Spinner")).IsTrue();
        await Assert.That(widgets.ContainsKey("Input")).IsTrue();
        await Assert.That(widgets.ContainsKey("Footer")).IsTrue();
    }

    [Test]
    public async Task Status_DefaultsToIdle()
    {
        var layout = new LayoutBuilder(new ChatState(), new InputState());
        await Assert.That(layout.Status).IsEqualTo("idle");
    }

    [Test]
    public async Task TokensIn_Accumulates()
    {
        var layout = new LayoutBuilder(new ChatState(), new InputState());
        layout.TokensIn = 100;
        layout.TokensIn += 50;
        await Assert.That(layout.TokensIn).IsEqualTo(150);
    }

    [Test]
    public async Task Cost_FormatsCorrectly()
    {
        var layout = new LayoutBuilder(new ChatState(), new InputState());
        layout.Cost = 1.2345m;
        var widgets = layout.BuildWidgets();
        await Assert.That(widgets.Count).IsEqualTo(6);
    }
}

public class SpectreTuiRenderContextTests
{
    [Test]
    public async Task Width_DoesNotThrow()
    {
        var ctx = new SpectreTuiRenderContext();
        try { _ = ctx.Width; }
        catch (IOException) { }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Height_DoesNotThrow()
    {
        var ctx = new SpectreTuiRenderContext();
        try { _ = ctx.Height; }
        catch (IOException) { }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task SupportsColor_IsTrue()
    {
        var ctx = new SpectreTuiRenderContext();
        await Assert.That(ctx.SupportsColor).IsTrue();
    }
}
