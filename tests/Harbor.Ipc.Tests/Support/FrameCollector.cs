using Harbor.Ipc.Protocol;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Reads the next frames off an IPC client's event stream.
/// </summary>
/// <remarks>
///     <para>
///         The broadcaster and reconnect suites both collect a fixed number of
///         frames and then stop reading; each wrote the same loop.
///     </para>
/// </remarks>
internal static class FrameCollector
{
    /// <summary>Subscribe and drain until at least <paramref name="count" /> frames arrive.</summary>
    internal static async Task<List<EventFrame>> CollectFramesAsync(
        MessagePackRpcClient client, int count, CancellationToken cancellationToken)
    {
        var frames = new List<EventFrame>();
        await foreach (var frame in client.EventFrames.ReadAllAsync(cancellationToken))
        {
            frames.Add(frame);
            if (frames.Count >= count) break;
        }

        return frames;
    }
}
