using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Terminal.Abstractions;
using Harbor.Tui.RazorConsole;
using Harbor.Tui.SpectreTui;
using Harbor.Tui.Termina;
using Harbor.Tui.TerminalGui;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.E2E.Tests;

/// <summary>
///     E2E tests for the 4 experimental interactive renderers.
///     Tests the actual event-processing pipelines (UiStore, ChatBridge)
///     with real assertions on state transitions and output.
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

    // ── SpectreTui (UiStore pipeline) ──

    [Test]
    public async Task SpectreTui_Initialize_Dispose_NoCrash()
    {
        var renderer = new SpectreTuiRenderer(NullLogger<SpectreTuiRenderer>.Instance);
        var init = await renderer.InitializeAsync();
        await Assert.That(init.IsSuccess).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task SpectreTui_WriteWriteLineClear_ReturnSuccess()
    {
        var renderer = new SpectreTuiRenderer(NullLogger<SpectreTuiRenderer>.Instance);
        await Assert.That((await renderer.InitializeAsync()).IsSuccess).IsTrue();
        await Assert.That((await renderer.WriteAsync("hi")).IsSuccess).IsTrue();
        await Assert.That((await renderer.WriteLineAsync("line")).IsSuccess).IsTrue();
        await Assert.That((await renderer.ClearAsync()).IsSuccess).IsTrue();
        renderer.Dispose();
    }

    [Test]
    public async Task SpectreTui_UiStore_HelloStream_ProducesTranscriptAndStatus()
    {
        var store = new UiStore();

        foreach (var evt in BuildHelloStream())
            store.Dispatch(evt);

        var state = store.State;
        await Assert.That(state.IsAgentRunning).IsFalse();
        await Assert.That(state.Status).IsEqualTo("idle");
        await Assert.That(state.Lines.Length).IsGreaterThan(0);
        var allText = string.Join("", state.Lines.Select(l => l.Text));
        await Assert.That(allText).Contains("Hello");
    }

    [Test]
    public async Task SpectreTui_UiStore_ToolCallStream_TracksToolAndText()
    {
        var store = new UiStore();

        foreach (var evt in BuildToolCallStream())
            store.Dispatch(evt);

        var state = store.State;
        await Assert.That(state.IsAgentRunning).IsFalse();
        await Assert.That(state.Lines.Length).IsGreaterThan(0);
        var allText = string.Join("", state.Lines.Select(l => l.Text));
        await Assert.That(allText).Contains("read");
        await Assert.That(allText).Contains("Here is the file.");
    }

    [Test]
    public async Task SpectreTui_UiStore_ErrorStream_SetsErrorStatus()
    {
        var store = new UiStore();

        foreach (var evt in BuildErrorStream())
            store.Dispatch(evt);

        var state = store.State;
        await Assert.That(state.IsAgentRunning).IsFalse();
        var allText = string.Join("", state.Lines.Select(l => l.Text));
        await Assert.That(allText).Contains("something went wrong");
    }

    [Test]
    public async Task SpectreTui_UiStore_Streaming_SetsIsStreamingDuringMessage()
    {
        var store = new UiStore();
        var partial = AssistantMessage.Empty("s1", "stub-1");

        store.Dispatch(new AgentStartEvent("s1", Array.Empty<AgentMessage>()));
        await Assert.That(store.State.IsAgentRunning).IsTrue();

        store.Dispatch(new MessageStartEvent(partial));
        await Assert.That(store.State.IsStreaming).IsTrue();

        store.Dispatch(new MessageUpdateEvent(new TextDeltaEvent("0", "tok"), partial));
        await Assert.That(store.State.Active.TextBuffer).Contains("tok");

        store.Dispatch(new MessageEndEvent(partial));
        await Assert.That(store.State.IsStreaming).IsFalse();
    }

    [Test]
    public async Task SpectreTui_ReadLineAsync_ReturnsSuccess()
    {
        var renderer = new SpectreTuiRenderer(NullLogger<SpectreTuiRenderer>.Instance);
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

    // ── Termina (UiStore pipeline) ──

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
    public async Task Termina_UiStore_HelloStream_ProducesTranscript()
    {
        var store = new UiStore();
        foreach (var evt in BuildHelloStream())
            store.Dispatch(evt);

        var state = store.State;
        await Assert.That(state.Status).IsEqualTo("idle");
        await Assert.That(state.Lines.Length).IsGreaterThan(0);
        var allText = string.Join("", state.Lines.Select(l => l.Text));
        await Assert.That(allText).Contains("Hello");
    }

    [Test]
    public async Task Termina_UiStore_ToolCallStream_TracksToolAndText()
    {
        var store = new UiStore();
        foreach (var evt in BuildToolCallStream())
            store.Dispatch(evt);

        var state = store.State;
        var allText = string.Join("", state.Lines.Select(l => l.Text));
        await Assert.That(allText).Contains("read");
        await Assert.That(allText).Contains("Here is the file.");
    }

    [Test]
    public async Task Termina_UiStore_ErrorStream_SetsErrorStatus()
    {
        var store = new UiStore();
        foreach (var evt in BuildErrorStream())
            store.Dispatch(evt);

        var state = store.State;
        var allText = string.Join("", state.Lines.Select(l => l.Text));
        await Assert.That(allText).Contains("something went wrong");
    }

    [Test]
    public async Task Termina_SetSlashHandler_DoesNotThrow()
    {
        var renderer = new TerminaRenderer(NullLogger<TerminaRenderer>.Instance);
        IInteractiveTuiRenderer interactive = renderer;
        interactive.SetSlashHandler(_ => Task.CompletedTask);
        renderer.Dispose();
    }

    // ── RazorConsole (UiStore pipeline) ──

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
    public async Task RazorConsole_UiStore_HelloStream_ProducesTranscript()
    {
        var store = new UiStore();
        foreach (var evt in BuildHelloStream())
            store.Dispatch(evt);

        var state = store.State;
        await Assert.That(state.Status).IsEqualTo("idle");
        await Assert.That(state.Lines.Length).IsGreaterThan(0);
        var allText = string.Join("", state.Lines.Select(l => l.Text));
        await Assert.That(allText).Contains("Hello");
    }

    [Test]
    public async Task RazorConsole_UiStore_ToolCallStream_TracksToolAndText()
    {
        var store = new UiStore();
        foreach (var evt in BuildToolCallStream())
            store.Dispatch(evt);

        var state = store.State;
        var allText = string.Join("", state.Lines.Select(l => l.Text));
        await Assert.That(allText).Contains("read");
        await Assert.That(allText).Contains("Here is the file.");
    }

    [Test]
    public async Task RazorConsole_UiStore_ErrorStream_SetsErrorStatus()
    {
        var store = new UiStore();
        foreach (var evt in BuildErrorStream())
            store.Dispatch(evt);

        var state = store.State;
        var allText = string.Join("", state.Lines.Select(l => l.Text));
        await Assert.That(allText).Contains("something went wrong");
    }

    [Test]
    public async Task RazorConsole_UiStore_Streaming_SetsIsStreamingDuringMessage()
    {
        var store = new UiStore();
        var partial = AssistantMessage.Empty("s1", "stub-1");

        store.Dispatch(new AgentStartEvent("s1", Array.Empty<AgentMessage>()));
        await Assert.That(store.State.Status).IsEqualTo("running");

        store.Dispatch(new MessageStartEvent(partial));
        await Assert.That(store.State.IsStreaming).IsTrue();

        store.Dispatch(new MessageUpdateEvent(new TextDeltaEvent("0", "partial"), partial));
        await Assert.That(store.State.Active.TextBuffer).IsEqualTo("partial");

        store.Dispatch(new MessageEndEvent(partial));
        await Assert.That(store.State.IsStreaming).IsFalse();
    }

    [Test]
    public async Task RazorConsole_SetSlashHandler_DoesNotThrow()
    {
        var renderer = new RazorConsoleRenderer(NullLogger<RazorConsoleRenderer>.Instance);
        IInteractiveTuiRenderer interactive = renderer;
        interactive.SetSlashHandler(_ => Task.CompletedTask);
        renderer.Dispose();
    }

    private static IAgent CreateStubAgent()
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

        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener)
            => new NoopDisposable();

        public Task<CSharpFunctionalExtensions.Result> PromptAsync(string text, CancellationToken ct = default)
            => Task.FromResult(CSharpFunctionalExtensions.Result.Success());

        public Task<CSharpFunctionalExtensions.Result> PromptAsync(UserMessage message, CancellationToken ct = default)
            => Task.FromResult(CSharpFunctionalExtensions.Result.Success());

        public void Initialize(Session session, AgentDefinition agent) { }
        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ResetAbortSource() { }
        public void Steer(AgentMessage message) { }
        public void FollowUp(AgentMessage message) { }
        public void Dispose() => AbortSource.Dispose();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
