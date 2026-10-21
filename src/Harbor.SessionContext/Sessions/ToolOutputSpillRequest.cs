namespace Harbor.Application.Sessions;

/// <summary>
///     Where one tool output would be spilled to, and the store that would take
///     it (issue #1748).
/// </summary>
/// <param name="Store">The spill area.</param>
/// <param name="SessionId">The session the tool call belongs to.</param>
/// <param name="ToolCallId">The tool call the output belongs to.</param>
/// <remarks>
///     <para>
///         The request is what the context boundary needs and nothing more: the
///         store that takes the bytes, and the two identifiers that name where
///         they go. Neither identifier is model input — they come from the
///         transcript — and the store sanitises both before they touch the
///         filesystem.
///     </para>
///     <para>
///         A struct, not a record: the context boundary builds one of these per
///         tool result per request, and it must not allocate.
///     </para>
/// </remarks>
public readonly record struct ToolOutputSpillRequest(
    IToolOutputSpillStore Store,
    string SessionId,
    string ToolCallId);
