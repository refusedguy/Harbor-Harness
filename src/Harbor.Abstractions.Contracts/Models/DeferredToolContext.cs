using MemoryPack;

namespace Harbor.Abstractions.Models;

/// <summary>
///     One piece of context a tool hands the agent loop on top of its result
///     (#1763) — the tool's second exit.
/// </summary>
/// <param name="Content">The context text the model will read.</param>
/// <param name="Source">
///     Who produced it — the tool name, a plugin id, a nested dispatch.
///     The model sees it, so the model can tell an observation from the
///     user's own words.
/// </param>
/// <param name="Metadata">
///     Optional key/value pairs that travel with the context. They are
///     persisted with it, so a reader of the transcript can tell what kind
///     of observation it was.
/// </param>
/// <remarks>
///     <para>
///         A deferred context is NOT a user message. It is context the TOOL
///         produced, and it lands in the history after the tool result it
///         belongs to — a diagnostics dump, a fresh observation, a nested
///         dispatch's context ferried up by a composite tool. The agent
///         loop frames it when it builds the model's view, so the model
///         reads it as an observation and not as something the user typed.
///     </para>
///     <para>
///         The type is deliberately a plain typed record and not a bag of
///         <c>object?</c> metadata: <see cref="ToolResultEntry.Metadata" />
///         is excluded from MemoryPack serialization, so a marker smuggled
///         through it would be lost the moment the session was persisted
///         (#1763).
///     </para>
/// </remarks>
[MemoryPackable]
public sealed partial record DeferredToolContext(
    string Content,
    string Source,
    IReadOnlyList<DeferredContextMetadata>? Metadata = null)
{
    /// <summary>
    ///     The marker that opens a deferred context in the model's view
    ///     (#1763). It is the one string that tells the model "this is an
    ///     observation, not the user's words".
    /// </summary>
    public const string Marker = "[tool-context]";
}

/// <summary>
///     One key/value pair of a <see cref="DeferredToolContext" />'s metadata
///     (#1763).
/// </summary>
/// <param name="Key">The metadata key.</param>
/// <param name="Value">The metadata value.</param>
[MemoryPackable]
public sealed partial record DeferredContextMetadata(string Key, string Value);
