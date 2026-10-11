using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.CellForge.Tests.Pilot;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Proof that the <see cref="CellForgePilot" /> headless driver covers the
/// flakiest PTY scenarios (#1183): the same narrative as the
/// <c>Harbor.Tui.CellForge.PtyTests</c> originals, but in-process — no child
/// process, no PTY, no fixed delays. Each test names the PTY original it
/// ports.
/// <para />
/// Two deliberate adaptations (both documented per test):
/// <list type="bullet">
/// <item>Marker vocabulary: the PTY grid paints <c>ChatScreenBridge</c> /
/// <c>ToolCallBlock</c> widgets ("✔ read", "[ok]", "✓ approved (always)");
/// the pilot asserts the renderer path (<c>ChatHistoryView</c> entries:
/// "[tool] → read", "[result] ✓ …"). Same contract (card streamed, result
/// settled), renderer vocabulary.</item>
/// <item>Side effects stay PTY-covered: the pilot feeds the tool START/END
/// events explicitly, so on-disk changes, approval prompts and the agent
/// loop are out of scope here. The PTY originals keep covering them.</item>
/// </list>
/// Marker asserts only — streaming cadence is nondeterministic by design
/// (celldiff §8), in both layers.
/// </summary>
public sealed class PilotPortedPtyScenariosTests
{
    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// Port of <c>SubmitScenarioTests.Submit_UserBlock_MockResponseStreams_AndCommits</c>:
    /// "привет" + Enter → answer streams in chunks → block commits and stays
    /// on the frame, status back at idle, composer cleared.
    /// </summary>
    [Test]
    public async Task Submit_UserBlock_ResponseStreams_AndCommits()
    {
        await using var pilot = await CellForgePilot.LaunchAsync();

        // press API replaces SubmitLine("привет") over the PTY wire: the draft
        // lands in the composer through the real keymap → store path.
        await pilot.TypeText("привет");
        await Assert.That(pilot.ComposerText()).IsEqualTo("привет");

        // Enter submits and clears the composer (running the agent loop on the
        // submitted prompt is the host effect — out of pilot scope); the
        // answering turn is fed explicitly below.
        await pilot.PressEnter();
        await Assert.That(pilot.ComposerText()).IsEqualTo(string.Empty);

        var user = TestMessages.User("привет");
        var streaming = TestMessages.Assistant(string.Empty);
        await pilot.PublishAsync(new AgentStartEvent("session-1", new AgentMessage[] { user }));
        await pilot.PublishAsync(new MessageStartEvent(streaming));
        foreach (var chunk in new[] { "Привет ", "из ", "mock!" })
            await pilot.PublishAsync(new MessageUpdateEvent(new TextDeltaEvent("m1", chunk), streaming));
        await pilot.PublishAsync(new MessageEndEvent(TestMessages.Assistant("Привет из mock!")));
        await pilot.PublishAsync(new AgentEndEvent(Array.Empty<AgentMessage>()));

        // Block COMMITTED (survives past streaming end), composer cleared.
        string frame = pilot.FrameText();
        await Assert.That(frame).Contains("привет");
        await Assert.That(frame).Contains("Привет из mock!");
        await Assert.That(frame).Contains("idle");
        await Assert.That(pilot.SeenEvents.OfType<MessageEndEvent>().Count()).IsEqualTo(1);
    }

    /// <summary>
    /// Port of <c>ToolCallStreamingScenarioTests.ToolCall_ReadCardStreamsArgsResult_AndTurnSettles</c>:
    /// a <c>read</c> call streams its card, the file content reaches the
    /// frame, the turn settles at idle.
    /// </summary>
    [Test]
    public async Task ToolCall_ReadCardStreamsArgsResult_AndTurnSettles()
    {
        await using var pilot = await CellForgePilot.LaunchAsync();
        await pilot.SubmitLine("read the probe file");
        await Assert.That(pilot.ComposerText()).IsEqualTo(string.Empty);

        var user = TestMessages.User("read the probe file");
        var streaming = TestMessages.Assistant(string.Empty);
        await pilot.PublishAsync(new AgentStartEvent("session-1", new AgentMessage[] { user }));
        await pilot.PublishAsync(new MessageStartEvent(streaming));
        await pilot.PublishAsync(new MessageUpdateEvent(new ToolCallStartEvent("tc1", "read"), streaming));
        await pilot.PublishAsync(ToolExecutionStartEvent.Create(
            "tc1", "read", Args("{\"path\":\"pty-tool-probe-423.txt\"}")));
        await pilot.PublishAsync(new ToolExecutionEndEvent(
            "tc1",
            ToolResult.Success("pty-probe-line-alpha-423\npty-probe-line-beta-423\n"),
            IsError: false));
        await pilot.PublishAsync(new MessageEndEvent(TestMessages.Assistant("done")));
        await pilot.PublishAsync(new AgentEndEvent(Array.Empty<AgentMessage>()));

        // The tool card streamed with the call name and the executed result
        // (renderer vocabulary: "[tool] → read" + "[result] ✓ …").
        string frame = pilot.FrameText();
        await Assert.That(frame).Contains("→ read");
        await Assert.That(frame).Contains("pty-probe-line-alpha-423");
        await Assert.That(frame).Contains("idle");

        // message_hook proof: the tool pair is observable on the bus.
        await Assert.That(pilot.SeenEvents.OfType<ToolExecutionStartEvent>().Count()).IsEqualTo(1);
        await Assert.That(pilot.SeenEvents.OfType<ToolExecutionEndEvent>().Single().Result.Output)
            .Contains("pty-probe-line-alpha-423");
    }

    /// <summary>
    /// Port of <c>EditDiffScenarioTests.EditCall_ContextDiffStreams_AndFileChangesOnDisk</c>:
    /// an <c>edit</c> call settles with the success header + context-diff
    /// body on the frame and the follow-up text lands. The on-disk mutation
    /// and the approval prompt stay covered by the PTY original.
    /// </summary>
    [Test]
    public async Task EditCall_ContextDiffStreams_AndCardSettles()
    {
        await using var pilot = await CellForgePilot.LaunchAsync();
        await pilot.SubmitLine("apply the edit to the probe file");

        var user = TestMessages.User("apply the edit to the probe file");
        var streaming = TestMessages.Assistant(string.Empty);
        await pilot.PublishAsync(new AgentStartEvent("session-1", new AgentMessage[] { user }));
        await pilot.PublishAsync(new MessageStartEvent(streaming));
        await pilot.PublishAsync(new MessageUpdateEvent(new ToolCallStartEvent("tc1", "edit"), streaming));
        await pilot.PublishAsync(ToolExecutionStartEvent.Create(
            "tc1",
            "edit",
            Args("{\"path\":\"pty-edit-probe-1057.txt\",\"oldString\":\"oldmarker-line-1057\",\"newString\":\"newmarker-line-1057\"}")));
        await pilot.PublishAsync(new ToolExecutionEndEvent(
            "tc1",
            ToolResult.Success("Edited pty-edit-probe-1057.txt\nDiff (context):\n- oldmarker-line-1057\n+ newmarker-line-1057"),
            IsError: false));
        await pilot.PublishAsync(new MessageEndEvent(TestMessages.Assistant("probe-edit-done-1057")));
        await pilot.PublishAsync(new AgentEndEvent(Array.Empty<AgentMessage>()));

        string frame = pilot.FrameText();
        await Assert.That(frame).Contains("→ edit");
        await Assert.That(frame).Contains("Edited ");
        await Assert.That(frame).Contains("Diff (context):");
        await Assert.That(frame).Contains("probe-edit-done-1057");
        await Assert.That(frame).Contains("idle");
    }
}
