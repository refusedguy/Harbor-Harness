using System.Collections.Immutable;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.TestKit;
using Harbor.Tui.CellForge;
using Harbor.Tui.CellForge.Generic;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #1202 (epic #1195): per-screen projectors. The renderer owns a
/// <see cref="ScreenProjectorRegistry"/> and projects through it without
/// naming any state slice; chat projection is one <see cref="ChatScreenProjector"/>
/// implementation, and new screens (here: <see cref="GenericScreenProjector"/>)
/// bring their own.
/// </summary>
public class ScreenProjectorRegistryTests
{
    private sealed class StubProjector : IProjector<UiState>
    {
        public StubProjector(string screenId) => ScreenId = screenId;

        public string ScreenId { get; }

        public int Calls { get; private set; }

        public void Project(UiState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            Calls++;
        }
    }

    [Test]
    public async Task Project_Routes_ByScreenId()
    {
        var registry = new ScreenProjectorRegistry();
        var chat = new StubProjector("chat");
        var generic = new StubProjector("generic");
        registry.Register(chat);
        registry.Register(generic);

        await Assert.That(registry.Count).IsEqualTo(2);
        await Assert.That(registry.Project("chat", new UiState())).IsTrue();
        await Assert.That(chat.Calls).IsEqualTo(1);
        await Assert.That(generic.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Project_UnknownId_IsNoOp()
    {
        var registry = new ScreenProjectorRegistry();
        var chat = new StubProjector("chat");
        registry.Register(chat);

        await Assert.That(registry.Project("nope", new UiState())).IsFalse();
        await Assert.That(chat.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Register_SameId_Replaces()
    {
        var registry = new ScreenProjectorRegistry();
        var first = new StubProjector("chat");
        var second = new StubProjector("chat");
        registry.Register(first);
        registry.Register(second);

        await Assert.That(registry.Count).IsEqualTo(1);
        await Assert.That(registry.Project("chat", new UiState())).IsTrue();
        await Assert.That(first.Calls).IsEqualTo(0);
        await Assert.That(second.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task ChatRenderer_ProjectsThroughRegistry_WithoutNamingSlices()
    {
        var backend = new RecordingBackend();
        var status = new StatusBarViewModel();
        var chat = new ChatHistoryViewModel();
        var input = new InputViewModel();
        using var renderer = new CellForgeTuiRenderer(
            NullLogger<CellForgeTuiRenderer>.Instance, backend, status, chat, input);

        await Assert.That(renderer.Projectors.Count).IsEqualTo(1);
        await Assert.That(renderer.Projectors.TryGet(
            ChatScreenProjector.ChatScreenId, out _)).IsTrue();

        renderer.ProjectStateIntoWidgets(new UiState
        {
            Ui = TerminalUiState.Empty with
            {
                Input = new InputModel("draft", ImmutableArray<string>.Empty, -1)
            }
        });

        await Assert.That(input.Text).IsEqualTo("draft");
        await Assert.That(input.Placeholder).IsEqualTo(CellForgeTuiRenderer.IdlePlaceholder);
        await Assert.That(renderer.PromptBuffer.SnapshotText()).IsEqualTo("draft");

        renderer.ProjectStateIntoWidgets(new UiState
        {
            Ui = TerminalUiState.Empty with
            {
                Input = new InputModel("draft", ImmutableArray<string>.Empty, -1)
            },
            Chat = ChatDomainState.Empty with { IsAgentRunning = true }
        });

        await Assert.That(input.Placeholder).IsEqualTo(CellForgeTuiRenderer.BusyPlaceholder);
    }

    [Test]
    public async Task GenericProjector_MirrorsOnly_UiFocus()
    {
        var screen = GenericScreen.Build([
            new GenericNodeSpec { Id = "one", Title = "one" },
            new GenericNodeSpec { Id = "two", Title = "two" },
        ]);
        var projector = new GenericScreenProjector(() => screen);

        await Assert.That(projector.ScreenId).IsEqualTo(GenericScreenProjector.GenericScreenId);

        // A chat-heavy state (agent running) must not break the neutral path:
        // the generic projector reads the Ui half only.
        projector.Project(new UiState
        {
            Ui = TerminalUiState.Empty with { FocusedPanelId = "two" },
            Chat = ChatDomainState.Empty with { IsAgentRunning = true }
        });

        await Assert.That(screen.FocusedId).IsEqualTo("two");
    }

    [Test]
    public async Task GenericRenderer_SyncsFocus_ThroughProjector()
    {
        var screen = GenericScreen.Build([
            new GenericNodeSpec { Id = "one", Title = "one" },
            new GenericNodeSpec { Id = "two", Title = "two" },
        ]);
        using var renderer = new GenericTuiRenderer(null, screen);

        await Assert.That(renderer.Projectors.Count).IsEqualTo(1);

        // The store only focuses registered panels (AppReducer.FocusPanel
        // ignores unknown ids), so the panels must be seeded first —
        // otherwise the projection reads a null focus and the screen keeps
        // its initial marker on every OS.
        _ = renderer.SeedPanels(
            ["one", "two"],
            ImmutableDictionary<string, TuiPanelState>.Empty
                .Add("one", TuiPanelState.Visible)
                .Add("two", TuiPanelState.Visible),
            ImmutableDictionary<string, int>.Empty);
        _ = renderer.FocusPanel("two");
        _ = renderer.Render(40, 10);

        await Assert.That(screen.FocusedId).IsEqualTo("two");
    }
}
