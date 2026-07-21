using Harbor.Abstractions.Models;
using Harbor.Ipc.Protocol;
namespace Harbor.Ipc;
/// <summary>
///     Helpers for converting between the rich domain <see cref="HarborEvent" />
///     hierarchy and the MessagePack [Union] <see cref="HarborEventData" />
///     wire DTOs.
/// </summary>
/// <remarks>
///     This is the single mapping point. The in-process client builds
///     <see cref="HarborEvent" /> from <c>AgentEvent</c> directly (no wire
///     DTO involved). The IPC server uses <see cref="ToData" /> to project
///     the in-process <see cref="HarborEvent" /> into a wire DTO before
///     serializing; the IPC client uses <see cref="FromData" /> to invert
///     the projection after deserializing.
/// </remarks>
public static class HarborEventMapping
{
    /// <summary>
    ///     Project a domain <see cref="HarborEvent" /> to its wire DTO form.
    /// </summary>
    public static HarborEventData ToData(HarborEvent evt) => evt switch
    {
        HarborEvent.AgentStarted e => new HarborEventAgentStarted(e.SessionId),
        HarborEvent.MessageUpdate e => new HarborEventMessageUpdate(
            WireCodec.SerializeDomain(e.Partial),
            e.Delta ?? string.Empty),
        HarborEvent.MessageEnd e => new HarborEventMessageEnd(
            WireCodec.SerializeDomain(e.Final)),
        HarborEvent.ToolStart e => new HarborEventToolStart(e.ToolCallId, e.ToolName),
        HarborEvent.ToolEnd e => new HarborEventToolEnd(
            e.ToolCallId,
            WireCodec.SerializeDomain(e.Result)),
        HarborEvent.TurnStart e => new HarborEventTurnStart(e.Turn),
        HarborEvent.TurnEnd e => new HarborEventTurnEnd(e.Turn),
        HarborEvent.AgentEnded e => new HarborEventAgentEnded(e.SessionId),
        HarborEvent.AgentError e => new HarborEventAgentError(e.Message),
        HarborEvent.CompactionStarted e => new HarborEventCompactionStarted(e.SessionId),
        HarborEvent.CompactionCompleted e => new HarborEventCompactionCompleted(e.SessionId, e.Pruned, e.Saved),
        _ => throw new ArgumentOutOfRangeException(nameof(evt), $"Unknown HarborEvent kind: {evt.Kind}")
    };

    /// <summary>
    ///     Invert the projection — convert a wire DTO back to a domain
    ///     <see cref="HarborEvent" />.
    /// </summary>
    public static HarborEvent FromData(HarborEventData data) => data switch
    {
        HarborEventAgentStarted e => new HarborEvent.AgentStarted(e.SessionId),
        HarborEventMessageUpdate e => new HarborEvent.MessageUpdate(
            WireCodec.DeserializeDomain<AssistantMessage>(e.PartialBytes)!,
            e.Delta),
        HarborEventMessageEnd e => new HarborEvent.MessageEnd(
            WireCodec.DeserializeDomain<AssistantMessage>(e.FinalBytes)!),
        HarborEventToolStart e => new HarborEvent.ToolStart(e.ToolCallId, e.ToolName),
        HarborEventToolEnd e => new HarborEvent.ToolEnd(
            e.ToolCallId,
            WireCodec.DeserializeDomain<ToolResult>(e.ResultBytes)!),
        HarborEventTurnStart e => new HarborEvent.TurnStart(e.Turn),
        HarborEventTurnEnd e => new HarborEvent.TurnEnd(e.Turn),
        HarborEventAgentEnded e => new HarborEvent.AgentEnded(e.SessionId),
        HarborEventAgentError e => new HarborEvent.AgentError(e.Message),
        HarborEventCompactionStarted e => new HarborEvent.CompactionStarted(e.SessionId),
        HarborEventCompactionCompleted e => new HarborEvent.CompactionCompleted(e.SessionId, e.Pruned, e.Saved),
        _ => throw new ArgumentOutOfRangeException(nameof(data), $"Unknown HarborEventData type: {data.GetType().Name}")
    };
}
