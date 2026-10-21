using System.Text.Json.Serialization;
using MemoryPack;

namespace Harbor.Abstractions.Models;

/// <summary>
///     The structured error of a failed tool call (#1760).
/// </summary>
/// <remarks>
///     <para>
///         Before #1760 a tool call failed or succeeded by ONE boolean
///         (<c>IsError</c>) plus a line of text. Everything that wanted to
///         tell a timeout from a permission refusal from a "no such tool" had
///         to parse that text — and the three places that did
///         (<c>AbandonedOutputMarker</c> through <c>Contains</c>, "timed out
///         after", "Permission denied") were each a failure point the moment
///         somebody reworded a message.
///     </para>
///     <para>
///         This record is the machine-readable half of the failure: the code
///         is stable, the name is the error's class-like label, and the reason
///         is the optional detail. The human message stays beside it in
///         <see cref="ToolResult.Output" /> — the two are siblings, not
///         alternatives.
///     </para>
///     <para>
///         <b>Why a separate typed field and not <c>Metadata</c>.</b>
///         <see cref="ToolResult.Metadata" /> is <c>object?</c>-typed and is
///         deliberately excluded from MemoryPack and dropped by both storage
///         backends (#51): a code parked there would not survive a restart.
///         This record holds only primitives, so it serializes everywhere the
///         result itself does.
///     </para>
///     <para>
///         <b>Codes are open.</b> A code Harbor does not know is still a code:
///         it is carried verbatim, it reaches the model, and it survives
///         storage. <see cref="ToolErrorCodes.IsKnown" /> answers "does this
///         build recognise the code", not "is this code valid".
///     </para>
/// </remarks>
/// <param name="Name">
///     The error's class-like name (e.g. <c>ToolTimeoutError</c>). Stable per
///     code, but informational: a policy branches on <paramref name="Code" />.
/// </param>
/// <param name="Code">
///     The stable machine-readable code (e.g. <c>TOOL_TIMEOUT</c>). See
///     <see cref="ToolErrorCodes" /> for the vocabulary Harbor itself emits.
/// </param>
/// <param name="Reason">
///     Optional detail — the deadline that fired, the exception message, the
///     policy reason. Free-form text: nothing branches on it.
/// </param>
[MemoryPackable]
public sealed partial record ToolErrorInfo(
    string Name,
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason = null);
