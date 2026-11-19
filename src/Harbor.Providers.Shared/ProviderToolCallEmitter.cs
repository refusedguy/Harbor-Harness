// Shared source: compiled INTO the OpenAI, OpenAiCompatible and Ollama
// provider assemblies via <Compile Include> link items (ROP-A ПР.1). The
// architecture matrix forbids Infrastructure→Infrastructure project references,
// so the one tool-call emit policy travels as a linked file, not a shared
// assembly. This is the single source for the Start/Delta event pair every
// native tool-call parser emits; id resolution itself is owned by
// ChunkStreamState.ResolveToolCallId (#3762).

using System.Collections.Generic;
using Harbor.Abstractions.Events;

namespace Harbor.Providers.Internal;

/// <summary>
///     The one place a parsed tool-call becomes an event pair. Owns the wire
///     id → remembered → <c>tc{index}</c> resolution by delegating to
///     <see cref="ChunkStreamState.ResolveToolCallId" />, so OpenAI, the
///     OpenAI-compatible adapter and Ollama can no longer drift apart on
///     coalescing or on the diagnostic remap count (ROP-A ПР.3, #203 B8).
/// </summary>
internal static class ProviderToolCallEmitter
{
    /// <summary>
    ///     Resolve the stable id for one tool-call index and append the
    ///     <see cref="ToolCallStartEvent" /> / <see cref="ToolCallDeltaEvent" />
    ///     pair, in that order. Empty names or arguments contribute no event,
    ///     which preserves the DOM walk's shape for partial chunks.
    /// </summary>
    public static void Emit(
        List<LlmEvent> events, ChunkStreamState state,
        int index, string? wireId, string? name, string? args)
    {
        string id = state.ResolveToolCallId(index, wireId);

        if (!string.IsNullOrEmpty(name))
            events.Add(new ToolCallStartEvent(id, name!));
        if (!string.IsNullOrEmpty(args))
            events.Add(new ToolCallDeltaEvent(id, args!));
    }
}
