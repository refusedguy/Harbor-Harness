namespace Harbor.Application.Agents;

/// <summary>
///     Message snapshotting extracted from <see cref="AgentLoop" /> (#1101, slice 1):
///     materialize a snapshot list of the current session messages for events.
///     Verbatim move — the loop's private helper body, unchanged.
/// </summary>
internal static class MessageSnapshot
{
    /// <summary>
    ///     Materialize a snapshot list of the current session messages for events.
    /// </summary>
    internal static List<AgentMessage> Snapshot(IReadOnlyList<AgentMessage> messages)
    {
        var snapshot = new List<AgentMessage>(messages.Count);
        for (int i = 0; i < messages.Count; i++)
        {
            snapshot.Add(messages[i]);
        }
        return snapshot;
    }
}
