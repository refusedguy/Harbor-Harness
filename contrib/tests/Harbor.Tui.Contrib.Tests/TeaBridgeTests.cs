using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.RazorConsole;
using Harbor.Tui.Termina;
using Harbor.Tui.Termina.Views;
using Harbor.Tui.TerminalGui;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StatusBarView = Harbor.Tui.TerminalGui.Views.StatusBarView;
namespace Harbor.Tui.Tests;
/// <summary>
///     TEA-integration tests for the three renderer bridges (Termina,
///     Terminal.Gui, RazorConsole). Each test verifies that the bridge
///     constructs without throwing AND that agent events flow through the
///     shared <see cref="UiStore" /> into the immutable
///     <see cref="UiState" /> — the single source of truth per §FP-005.
/// </summary>
public class TeaBridgeTests
{
    private static IAgent MockAgent()
    {
        var definition = AgentDefinition.CodeDefault(
            "claude-3-5-sonnet",
            "anthropic");
        var state = AgentState.Idle("s1", definition);
        var mock = new Mock<IAgent>();
        mock.SetupGet(a => a.State).Returns(state);
        mock.SetupGet(a => a.AbortSource).Returns(new CancellationTokenSource());
        return mock.Object;
    }

    // ── Termina ─────────────────────────────────────────────────────────

    [Test]
    public async Task Termina_TeaBridge_Constructs_And_Dispatches_TextDelta()
    {
        var agent = MockAgent();
        var bridge = new TerminaTeaBridge(agent, null, NullLogger.Instance);
        try
        {
            await Assert.That(bridge.Store).IsNotNull();
            await Assert.That(bridge.Store.State.Model).IsEqualTo("claude-3-5-sonnet");
            await Assert.That(bridge.Store.State.Provider).IsEqualTo("anthropic");

            bridge.Push(new MessageStartEvent(AssistantMessage.Empty("s1", "m")));
            bridge.Push(new MessageUpdateEvent(new TextDeltaEvent("0", "Hello"), AssistantMessage.Empty("s1", "m")));
            bridge.Push(new MessageEndEvent(AssistantMessage.Empty("s1", "m")));

            await Assert.That(bridge.Store.State.Lines.Length).IsEqualTo(1);
            await Assert.That(bridge.Store.State.Lines[0].Role).IsEqualTo(ChatRole.Assistant);
            await Assert.That(bridge.Store.State.Lines[0].Text).IsEqualTo("Hello");
        }
        finally { bridge.Dispose(); }
    }

    [Test]
    public async Task Termina_TeaBridge_KeyHandler_DispatchesChars()
    {
        var agent = MockAgent();
        var bridge = new TerminaTeaBridge(agent, null, NullLogger.Instance);
        try
        {
            bridge.HandleKey(new ConsoleKeyInfo('h', ConsoleKey.H, false, false, false));
            bridge.HandleKey(new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false));
            await Assert.That(bridge.Store.State.Input.Text).IsEqualTo("hi");
        }
        finally { bridge.Dispose(); }
    }

    [Test]
    public async Task Termina_ChatView_Projects_Transcript()
    {
        var agent = MockAgent();
        var bridge = new TerminaTeaBridge(agent, null, NullLogger.Instance);
        try
        {
            bridge.Push(new MessageUpdateEvent(new TextDeltaEvent("0", "Hello **world**"), AssistantMessage.Empty("s1", "m")));
            bridge.Push(new MessageEndEvent(AssistantMessage.Empty("s1", "m")));

            var view = new ChatView();
            var projector = new DefaultUiProjector();
            var lines = view.Build(projector.Project(bridge.Store.State));
            await Assert.That(lines.Count).IsGreaterThan(0);
            await Assert.That(lines.Any(l => l.Contains("assistant"))).IsTrue();
        }
        finally { bridge.Dispose(); }
    }

    // ── Terminal.Gui ────────────────────────────────────────────────────

    [Test]
    public async Task TerminalGui_TeaBridge_Constructs_And_Dispatches_TextDelta()
    {
        var agent = MockAgent();
        var bridge = new TerminalGuiTeaBridge(agent, null, NullLogger.Instance);
        try
        {
            await Assert.That(bridge.Store).IsNotNull();
            await Assert.That(bridge.Store.State.AgentName).IsEqualTo("code");

            bridge.Push(new MessageUpdateEvent(new TextDeltaEvent("0", "Test"), AssistantMessage.Empty("s1", "m")));
            bridge.Push(new MessageEndEvent(AssistantMessage.Empty("s1", "m")));
            await Assert.That(bridge.Store.State.Lines.Length).IsEqualTo(1);
            await Assert.That(bridge.Store.State.Lines[0].Text).IsEqualTo("Test");
        }
        finally { bridge.Dispose(); }
    }

    [Test]
    public async Task TerminalGui_StatusBarView_Projects_State()
    {
        var agent = MockAgent();
        var bridge = new TerminalGuiTeaBridge(agent, null, NullLogger.Instance);
        try
        {
            bridge.Push(new AgentStartEvent("s1", Array.Empty<AgentMessage>()));
            var view = new StatusBarView();
            var projector = new DefaultUiProjector();
            string text = view.Build(projector.Project(bridge.Store.State));
            await Assert.That(text).Contains("claude-3-5-sonnet");
            await Assert.That(text).Contains("anthropic");
            await Assert.That(text).Contains("code");
        }
        finally { bridge.Dispose(); }
    }

    // ── RazorConsole ────────────────────────────────────────────────────

    [Test]
    public async Task RazorConsole_TeaBridge_Constructs_And_Dispatches_TextDelta()
    {
        var agent = MockAgent();
        var bridge = new RazorConsoleTeaBridge(agent, null, NullLogger.Instance);
        try
        {
            await Assert.That(bridge.Store).IsNotNull();

            bridge.Push(new MessageUpdateEvent(new TextDeltaEvent("0", "Razor"), AssistantMessage.Empty("s1", "m")));
            bridge.Push(new MessageEndEvent(AssistantMessage.Empty("s1", "m")));
            await Assert.That(bridge.Store.State.Lines.Length).IsEqualTo(1);
            await Assert.That(bridge.Store.State.Lines[0].Text).IsEqualTo("Razor");
        }
        finally { bridge.Dispose(); }
    }

    [Test]
    public async Task RazorConsole_ChatView_Projects_Transcript_WithMarkup()
    {
        var agent = MockAgent();
        var bridge = new RazorConsoleTeaBridge(agent, null, NullLogger.Instance);
        try
        {
            bridge.Push(new MessageUpdateEvent(new TextDeltaEvent("0", "bold"), AssistantMessage.Empty("s1", "m")));
            bridge.Push(new MessageEndEvent(AssistantMessage.Empty("s1", "m")));

            var view = new RazorConsole.Views.ChatView();
            var projector = new DefaultUiProjector();
            var lines = view.Build(projector.Project(bridge.Store.State));
            await Assert.That(lines.Count).IsGreaterThan(0);
            // The Spectre markup wrapper for the assistant role should appear.
            await Assert.That(lines.Any(l => l.Contains("[white]"))).IsTrue();
        }
        finally { bridge.Dispose(); }
    }

    [Test]
    public async Task RazorConsole_TeaBridge_Toast_Queue_Roundtrips()
    {
        var agent = MockAgent();
        var bridge = new RazorConsoleTeaBridge(agent, null, NullLogger.Instance);
        try
        {
            bridge.Toast("hello");
            bridge.Toast("world");
            await Assert.That(bridge.DequeueToast()).IsEqualTo("hello");
            await Assert.That(bridge.DequeueToast()).IsEqualTo("world");
            await Assert.That(bridge.DequeueToast()).IsNull();
        }
        finally { bridge.Dispose(); }
    }
}
