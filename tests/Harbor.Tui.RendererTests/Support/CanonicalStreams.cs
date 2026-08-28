namespace Harbor.Tui.RendererTests.Support;

using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;

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
        var args = JsonSerializer.SerializeToElement(new { path = "README.md", limit = 10 });

        yield return new AgentStartEvent("s1", []);
        yield return new MessageStartEvent(partial);
        yield return new MessageUpdateEvent(new TextDeltaEvent("0", "Hello"), partial);
        yield return new MessageUpdateEvent(new TextDeltaEvent("0", ", "), partial);
        yield return new MessageUpdateEvent(new TextDeltaEvent("0", "world!"), partial);
        yield return new MessageEndEvent(partial);

        var toolPartial = AssistantMessage.Empty("s1", "stub-2");
        yield return new ToolExecutionStartEvent("tc_1", "read", args);
        yield return new ToolExecutionEndEvent("tc_1", ToolResult.Success("[0001] # Harbor"), IsError: false);
        yield return new MessageStartEvent(toolPartial);
        yield return new MessageUpdateEvent(new TextDeltaEvent("1", "Done."), toolPartial);
        yield return new MessageEndEvent(toolPartial);
        yield return new AgentEndEvent([]);
    }
}
