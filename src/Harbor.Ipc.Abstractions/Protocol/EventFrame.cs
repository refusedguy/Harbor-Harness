namespace Harbor.Ipc.Protocol;

/// <summary>
///     One received event plus its server-assigned delivery sequence.
/// </summary>
/// <param name="Sequence">
///     The server's envelope sequence. This is the whole reason the type
///     exists: a reconnecting client resumes from <c>lastSeen</c>, drops
///     duplicates, and holds a subscribe-before-snapshot baseline against it.
/// </param>
/// <param name="Event">The decoded event.</param>
/// <remarks>
///     Moved here from <c>MessagePackRpcClient.cs</c> by #494 so
///     <see cref="IRpcClient" /> — which lives in the shared contract assembly
///     alongside every other wire type — can name it. Same namespace, so no
///     <c>using</c> changed anywhere.
/// </remarks>
public readonly record struct EventFrame(ulong Sequence, HarborEvent Event);