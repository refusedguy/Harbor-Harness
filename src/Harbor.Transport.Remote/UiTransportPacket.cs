using Harbor.Abstractions.Events;

namespace Harbor.Transport.Remote;

public sealed record UiTransportPacket(string Type, AgentEvent? Event, DateTimeOffset Timestamp)
{
    public static UiTransportPacket FromEvent(AgentEvent @event)
        => new(@event.GetType().Name, @event, @event.Timestamp);
}
