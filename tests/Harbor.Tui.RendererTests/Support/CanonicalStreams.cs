namespace Harbor.Tui.RendererTests.Support;

using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.TestKit;

/// <summary>
///     The canonical event stream every renderer golden frame is built from
///     (renderer-unification sprint Phase 5). One fixed stream means frames
///     are directly comparable across renderer backends: any difference
///     between two goldens reflects renderer behavior, not input differences.
/// </summary>
public static class CanonicalStreams
{
    /// <summary>
    ///     AgentStart → MessageStart → streamed assistant text → tool round
    ///     trip → message end → agent end. Deterministic ids throughout.
    /// </summary>
    public static IEnumerable<AgentEvent> ChatWithToolRoundTrip()
    {
        var partial = AssistantMessage.Empty("s1", "stub-1");
        var args = JsonSerializer.SerializeToElement(
            new ToolPathLimitArgs("README.md", 10),
            TestJsonContext.Default.ToolPathLimitArgs);

        yield return TestEvents.AgentStart();
        yield return TestEvents.MessageStart(partial);
        yield return TestEvents.TextDeltaUpdate("0", "Hello", partial);
        yield return TestEvents.TextDeltaUpdate("0", ", ", partial);
        yield return TestEvents.TextDeltaUpdate("0", "world!", partial);
        yield return TestEvents.MessageEnd(partial);

        var toolPartial = AssistantMessage.Empty("s1", "stub-2");
        yield return new ToolExecutionStartEvent("tc_1", "read", args);
        yield return TestEvents.ToolExecutionEnd("tc_1", ToolResult.Success("[0001] # Harbor"));
        yield return TestEvents.MessageStart(toolPartial);
        yield return TestEvents.TextDeltaUpdate("1", "Done.", toolPartial);
        yield return TestEvents.MessageEnd(toolPartial);
        yield return TestEvents.AgentEnd();
    }
}
