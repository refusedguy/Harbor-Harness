// SessionTranscript.cs — one read of a session's transcript (#1744).
//
// A transcript is not the same thing as a message list. The messages are what
// the log holds; the transcript is what a READER gets: the messages plus the
// closing of the turns the log never closed, plus the honest statement of
// what the read could not deliver.
//
// The two are kept apart on purpose. The messages are the log's own content
// and belong to the log: a fork copies them, the stats fold over them, and
// nothing may add to them behind the store's back. The closers and the
// integrity belong to the READ: they are derived, deterministic, and never
// written back.

using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     One read of a session's transcript: the messages it delivered and what
///     the read found at its tail (#1744).
/// </summary>
/// <param name="Messages">
///     The messages of the transcript, in chronological order, with the turns
///     the log never closed closed in memory (see
///     <see cref="TranscriptInspector.CloseUnclosedToolCalls" />). Nothing was
///     written back: the log on disk still ends where it ended.
/// </param>
/// <param name="Integrity">
///     What the read could not deliver: the records it could not decode and the
///     tool calls the transcript never got results for.
/// </param>
public sealed record SessionTranscript(
    IReadOnlyList<AgentMessage> Messages,
    TranscriptIntegrity Integrity);
