using System.Collections.Immutable;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Projection;

namespace Harbor.Tui.Tests;

public class DefaultUiProjectorTests
{
    private readonly DefaultUiProjector _projector = new();

    [Test]
    public async Task Project_BootState_HeaderHasEmptyModelProvider()
    {
        var state = new UiState();
        var screen = _projector.Project(state);

        await Assert.That(screen.Header.Model).IsEqualTo(string.Empty);
        await Assert.That(screen.Header.Provider).IsEqualTo(string.Empty);
        await Assert.That(screen.Header.AgentName).IsEqualTo(string.Empty);
        await Assert.That(screen.Header.IsAgentRunning).IsFalse();
        await Assert.That(screen.Header.IsStreaming).IsFalse();
    }

    [Test]
    public async Task Project_BootState_TranscriptIsEmpty()
    {
        var state = new UiState();
        var screen = _projector.Project(state);

        await Assert.That(screen.Transcript.Blocks).IsEmpty();
        await Assert.That(screen.Transcript.StreamingBlockId).IsNull();
    }

    [Test]
    public async Task Project_FocusModeInput_FocusIsInput()
    {
        var state = new UiState() with { Focus = FocusMode.Input };
        var screen = _projector.Project(state);

        await Assert.That(screen.Focus).IsEqualTo(FocusMode.Input);
    }

    [Test]
    public async Task Project_FocusModePanel_FocusIsPanel()
    {
        var state = new UiState() with
        {
            Focus = FocusMode.Panel,
            FocusedPanelId = "help"
        };
        var screen = _projector.Project(state);

        await Assert.That(screen.Focus).IsEqualTo(FocusMode.Panel);
    }

    [Test]
    public async Task Project_StreamingState_HasStreamingBlock()
    {
        var state = new UiState() with
        {
            IsStreaming = true,
            Active = new ActiveMessage("streaming text", string.Empty)
        };
        var screen = _projector.Project(state);

        await Assert.That(screen.Transcript.StreamingBlockId).IsNotNull();
        await Assert.That(screen.Transcript.Blocks.Any(b => b is UiMessageBlock mb && mb.Phase == MessageRenderPhase.Streaming)).IsTrue();
    }

    [Test]
    public async Task Project_ToolCallAndResult_BothInBlocks()
    {
        var state = new UiState()
            .AddLine(ChatRole.Tool, "→ read  /tmp/file.txt")
            .AddLine(ChatRole.ToolResult, "✓ file contents here");
        var screen = _projector.Project(state);

        var toolBlocks = screen.Transcript.Blocks.OfType<UiMessageBlock>().Where(b => b.Role is ChatRole.Tool or ChatRole.ToolResult).ToList();
        await Assert.That(toolBlocks).HasCount(2);
    }

    [Test]
    public async Task Project_EmptyTranscript_HasEmptyBlocks()
    {
        var state = new UiState();
        var screen = _projector.Project(state);

        await Assert.That(screen.Transcript.Blocks).IsEmpty();
    }

    [Test]
    public async Task Project_StatusBar_HasProviderModelSegment()
    {
        var state = new UiState() with
        {
            Model = "anthropic/claude-opus-4",
            Provider = "anthropic"
        };
        var screen = _projector.Project(state);

        var providerSegment = screen.StatusBar.Segments.FirstOrDefault(s => s.Text.Contains("anthropic"));
        await Assert.That(providerSegment).IsNotNull();
    }

    [Test]
    public async Task Project_InputEnabled_WhenAgentNotRunning()
    {
        var state = new UiState();
        var screen = _projector.Project(state);

        await Assert.That(screen.Input.IsEnabled).IsTrue();
    }

    [Test]
    public async Task Project_InputDisabled_WhenAgentRunning()
    {
        var state = new UiState() with { IsAgentRunning = true };
        var screen = _projector.Project(state);

        await Assert.That(screen.Input.IsEnabled).IsFalse();
    }

    [Test]
    public async Task Project_StateRevision_IsNonEmpty()
    {
        var state = new UiState();
        var screen = _projector.Project(state);

        await Assert.That(screen.StateRevision).IsNotEmpty();
    }

    [Test]
    public async Task Project_UserMessage_HasRoleUserSpan()
    {
        var state = new UiState().AddLine(ChatRole.User, "hello world");
        var screen = _projector.Project(state);

        var msgBlock = screen.Transcript.Blocks.OfType<UiMessageBlock>().First();
        await Assert.That(msgBlock.Role).IsEqualTo(ChatRole.User);
        await Assert.That(msgBlock.Spans).HasCount(1);
    }
}