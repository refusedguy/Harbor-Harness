using System.Threading.Channels;

namespace Harbor.Application.Agents;

/// <summary>
///     Mid-run steering inbox extracted from <see cref="DefaultAgent" /> ([G4]):
///     owns the one-per-agent channel the loop drains at safe turn boundaries.
///     The channel outlives session rebinds; stale messages authored for a
///     previous session are dropped on rebind, never drained into the new history.
/// </summary>
internal sealed class AgentSteeringQueue
{
    private readonly Channel<AgentMessage> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    /// <summary>The live channel shared with the session context and the loop drain.</summary>
    internal Channel<AgentMessage> Channel => _channel;

    /// <summary>Inject a steering message into the current run (never blocks, never drops).</summary>
    internal void Enqueue(AgentMessage message) => _channel.Writer.TryWrite(message);

    /// <summary>Drop all queued messages; returns how many were dropped.</summary>
    internal int DrainStale()
    {
        int dropped = 0;
        while (_channel.Reader.TryRead(out _))
        {
            dropped++;
        }

        return dropped;
    }

    /// <summary>Complete the writer so late readers observe completion (agent dispose).</summary>
    internal void Complete() => _channel.Writer.TryComplete();
}
