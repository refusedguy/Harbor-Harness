using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Everything a notebook command needs for one invocation: the loaded
///     session notes (mutated in place by write commands), the parsed
///     <c>key</c>/<c>content</c> arguments, and the store + path + logger
///     for persisting and diagnostics.
/// </summary>
/// <param name="Notes">Session notes loaded by the dispatcher; write commands mutate this instance.</param>
/// <param name="Key">Parsed <c>key</c> argument (null when absent).</param>
/// <param name="Content">Parsed <c>content</c> argument (null when absent).</param>
/// <param name="SessionId">Sanitized session id (for logging).</param>
/// <param name="NotesPath">Resolved per-session JSON file path.</param>
/// <param name="Store">Shared load/save/quota persistence.</param>
/// <param name="Logger">Logger for diagnostics.</param>
internal sealed record NoteInvocation(
    Dictionary<string, NoteEntry> Notes,
    string? Key,
    string? Content,
    string SessionId,
    string NotesPath,
    NoteStore Store,
    ILogger Logger);
