using System.Text.Json;
namespace Harbor.Abstractions.Models;

/// <summary>
///     Prototype-style owned-clone helper for <see cref="JsonElement" /> args (#1520).
/// </summary>
/// <remarks>
///     <para>
///         The guarded-clone form <c>ValueKind == Undefined ? element :
///         element.Clone()</c> existed as one private body in
///         <see cref="ToolCallPart" /> plus three inline copies
///         (<c>ToolExecutionStartEvent.Create</c>, <c>ToolCallEndEvent.Create</c>,
///         <c>PermissionRequest.Create</c>); this is the single implementation.
///     </para>
///     <para>
///         Fail-safe semantics: an already-<see cref="JsonValueKind.Undefined" />
///         element has no <see cref="JsonDocument" /> behind it and passes through
///         untouched; any other element is detached from its source document's
///         lifetime, so the receiving object owns its args (the OpenAiWire
///         eager-materialization rule, #86/#87). A raw
///         <see cref="JsonElement.Clone" /> throws on the Undefined
///         <see langword="default" /> — take this helper at every site where
///         Undefined is representable.
///     </para>
/// </remarks>
internal static class OwnedJson
{
    /// <summary>
    ///     Clone <paramref name="args" /> into owned storage — or pass it through
    ///     unchanged when it is the Undefined <see langword="default" />.
    /// </summary>
    /// <param name="args">The raw JSON args (view over a document, or Undefined).</param>
    /// <returns>An element the caller owns beyond the source document's lifetime.</returns>
    internal static JsonElement CloneOwned(JsonElement args) =>
        args.ValueKind == JsonValueKind.Undefined ? args : args.Clone();
}
