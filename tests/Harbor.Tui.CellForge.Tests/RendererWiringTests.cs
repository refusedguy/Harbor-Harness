using System.Collections.Immutable;
using System.Reflection;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Terminal.Abstractions.Views;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// CF-F-001 + CF-B-011 wiring: the placement-filter override is gone, the base
/// filter paints ChatHistory/Input, <see cref="InputViewModel"/> is projected
/// from <see cref="UiState"/> and bound two-way with the composer buffer,
/// session snapshots are captured, and a full <c>RenderAsync</c> pass lands in
/// <see cref="RecordingBackend"/>.
/// </summary>
public class RendererWiringTests
{
    private static CellForgeTuiRenderer Create(
        RecordingBackend backend,
        InputViewModel? inputVm = null,
        ChatHistoryViewModel? chatVm = null,
        StatusBarViewModel? statusVm = null) =>
        new(NullLogger<CellForgeTuiRenderer>.Instance, backend, statusVm, chatVm, inputVm);

    private static InputModel TextModel(string text) =>
        new(text, ImmutableArray<string>.Empty, -1);

    [Test]
    public async Task ShouldRenderPlacement_Override_Removed()
    {
        var method = typeof(CellForgeTuiRenderer).GetMethod(
            "ShouldRenderPlacement",
            BindingFlags.Instance | BindingFlags.NonPublic);

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.DeclaringType).IsEqualTo(typeof(BaseTuiRenderer));
    }

    [Test]
    public async Task BaseFilter_Paints_ChatHistory_And_Input()
    {
        var backend = new RecordingBackend();
        using var renderer = Create(backend);
        var method = typeof(BaseTuiRenderer).GetMethod(
            "ShouldRenderPlacement",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        bool Invoke(TuiViewPlacement placement, AgentEvent evt) =>
            (bool)method.Invoke(renderer, [placement, evt])!;

        var start = new AgentStartEvent("s1", Array.Empty<AgentMessage>());
        var end = new AgentEndEvent(Array.Empty<AgentMessage>());

        await Assert.That(Invoke(TuiViewPlacement.ChatHistory, start)).IsTrue();
        await Assert.That(Invoke(TuiViewPlacement.Input, start)).IsTrue();
        await Assert.That(Invoke(TuiViewPlacement.Input, end)).IsTrue();
    }

    [Test]
    public async Task Projection_Input_Idle_And_Busy()
    {
        var backend = new RecordingBackend();
        var inputVm = new InputViewModel();
        using var renderer = Create(backend, inputVm);

        renderer.ProjectStateIntoWidgets(new UiState { Input = TextModel("draft") });

        await Assert.That(inputVm.Text).IsEqualTo("draft");
        await Assert.That(inputVm.Placeholder).IsEqualTo(CellForgeTuiRenderer.IdlePlaceholder);
        await Assert.That(renderer.PromptBuffer.SnapshotText()).IsEqualTo("draft");

        renderer.ProjectStateIntoWidgets(new UiState
        {
            Input = TextModel("draft"),
            IsAgentRunning = true
        });

        await Assert.That(inputVm.Placeholder).IsEqualTo(CellForgeTuiRenderer.BusyPlaceholder);
    }

    [Test]
    public async Task Projection_Input_Moves_Cursor_To_End()
    {
        var backend = new RecordingBackend();
        var inputVm = new InputViewModel();
        using var renderer = Create(backend, inputVm);

        renderer.ProjectStateIntoWidgets(new UiState { Input = TextModel("hello") });

        await Assert.That(inputVm.Text).IsEqualTo("hello");
        await Assert.That(inputVm.CursorPosition).IsEqualTo(5);
        await Assert.That(renderer.PromptBuffer.SnapshotText()).IsEqualTo("hello");
        await Assert.That(renderer.PromptBuffer.Cursor).IsEqualTo(5);
    }

    [Test]
    public async Task InputVm_Edits_Flow_Into_Composer_Buffer()
    {
        var backend = new RecordingBackend();
        var inputVm = new InputViewModel();
        using var renderer = Create(backend, inputVm);

        inputVm.Text = "typed";
        inputVm.CursorPosition = 2;

        await Assert.That(renderer.PromptBuffer.SnapshotText()).IsEqualTo("typed");
        await Assert.That(renderer.PromptBuffer.Cursor).IsEqualTo(2);
        // No echo back into the view model (single direction per change).
        await Assert.That(inputVm.Text).IsEqualTo("typed");
    }

    [Test]
    public async Task Projection_Sessions_Snapshot()
    {
        var backend = new RecordingBackend();
        using var renderer = Create(backend);
        var id = SessionId.New();
        var now = DateTimeOffset.UtcNow;
        var info = new SessionInfo(id, "work", now, now, "active");

        renderer.ProjectStateIntoWidgets(new UiState
        {
            Sessions = ImmutableArray.Create(info),
            ActiveSessionId = id,
            IsLoading = true
        });

        await Assert.That(renderer.SessionsSnapshot.Length).IsEqualTo(1);
        await Assert.That(renderer.SessionsSnapshot[0].Title).IsEqualTo("work");
        await Assert.That(renderer.ActiveSessionIdSnapshot).IsEqualTo(id);
        await Assert.That(renderer.SessionsLoading).IsTrue();
    }

    [Test]
    public async Task RenderAsync_EndToEnd_Writes_History_And_Input()
    {
        var backend = new RecordingBackend();
        using var renderer = Create(backend);
        await renderer.InitializeAsync();

        var user = new UserMessage("m1", "s1", DateTimeOffset.UtcNow, "hello e2e", "code", "model");
        await renderer.RenderAsync(new AgentStartEvent("s1", new AgentMessage[] { user }));
        await renderer.RenderAsync(new AgentEndEvent(Array.Empty<AgentMessage>()));

        string text = backend.Text;
        await Assert.That(text.Contains("[user]")).IsTrue();
        await Assert.That(text.Contains("hello e2e")).IsTrue();
        await Assert.That(text.Contains("> ")).IsTrue();
        await Assert.That(text.Contains(CellForgeTuiRenderer.BusyPlaceholder)).IsTrue();
    }
}
