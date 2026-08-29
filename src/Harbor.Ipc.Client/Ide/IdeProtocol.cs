using System.Text.Json.Serialization;

namespace Harbor.Ipc.Ide;

/// <summary>
///     JSON-RPC method names served by the <c>harbor ide</c> stdio bridge.
///     External editors attach by spawning <c>harbor ide --session &lt;id&gt;</c>
///     and speaking newline-delimited JSON-RPC 2.0 over the child's stdin/stdout.
/// </summary>
public static class IdeMethods
{
    /// <summary>List sessions known to the running Harbor host.</summary>
    public const string ListSessions = "list_sessions";

    /// <summary>
    ///     Submit a prompt to a session. Returns immediately
    ///     (<c>{"accepted":true}</c>) — the run streams back via the
    ///     <see cref="IdeNotifications.Stream" /> notification. The bridge
    ///     NEVER blocks on the agent loop.
    /// </summary>
    public const string InjectPrompt = "inject_prompt";

    /// <summary>Start forwarding session events as <c>stream</c> notifications.</summary>
    public const string ReadStream = "read_stream";

    /// <summary>Stop forwarding session events.</summary>
    public const string StopStream = "stop_stream";

    /// <summary>Abort the in-flight run (and cancel pending injects).</summary>
    public const string Abort = "abort";
}

/// <summary>
///     Server→editor notification method names pushed by the bridge.
/// </summary>
public static class IdeNotifications
{
    /// <summary>Streaming payload: deltas, tool calls, turns, run lifecycle.</summary>
    public const string Stream = "stream";

    /// <summary>An injected prompt failed before/while running (non-fatal to the bridge).</summary>
    public const string PromptError = "prompt_error";

    /// <summary>The event pump stopped (connection lost or <c>stop_stream</c>).</summary>
    public const string StreamClosed = "stream_closed";
}

/// <summary>One session in a <see cref="IdeListSessionsResult" />.</summary>
public sealed record IdeSessionInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

/// <summary>Result payload for <see cref="IdeMethods.ListSessions" />.</summary>
public sealed record IdeListSessionsResult(
    [property: JsonPropertyName("sessions")] IReadOnlyList<IdeSessionInfo> Sessions);

/// <summary>Params for <see cref="IdeMethods.InjectPrompt" />.</summary>
public sealed record IdeInjectPromptParams(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("agent")] string? Agent = null);

/// <summary>Result payload for <see cref="IdeMethods.InjectPrompt" /> — acceptance is immediate.</summary>
public sealed record IdeInjectPromptResult(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("session_id")] string SessionId);

/// <summary>Params for <see cref="IdeMethods.ReadStream" />.</summary>
public sealed record IdeReadStreamParams(
    [property: JsonPropertyName("session_id")] string? SessionId = null);

/// <summary>Result payload for <see cref="IdeMethods.ReadStream" /> / <see cref="IdeMethods.StopStream" />.</summary>
public sealed record IdeReadStreamResult(
    [property: JsonPropertyName("subscribed")] bool Subscribed);

/// <summary>Params for <see cref="IdeMethods.Abort" />.</summary>
public sealed record IdeAbortParams(
    [property: JsonPropertyName("session_id")] string? SessionId = null);

/// <summary>Result payload for <see cref="IdeMethods.Abort" />.</summary>
public sealed record IdeAbortResult(
    [property: JsonPropertyName("requested")] bool Requested);

/// <summary>
///     One <see cref="IdeNotifications.Stream" /> notification payload. Flat and
///     forgiving on purpose: every editor SDK (ts/js/py/…) only needs to switch
///     on <see cref="Kind" /> and read the relevant optional fields.
/// </summary>
public sealed record IdeStreamNotification(
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("delta")] string? Delta = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("turn")] int? Turn = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null,
    [property: JsonPropertyName("tool_name")] string? ToolName = null,
    [property: JsonPropertyName("ok")] bool? Ok = null,
    [property: JsonPropertyName("error")] string? Error = null);

/// <summary>Notification payload for <see cref="IdeNotifications.PromptError" />.</summary>
public sealed record IdePromptErrorNotification(
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("error")] string Error);

/// <summary>Stream kind discriminants used by <see cref="IdeStreamNotification.Kind" />.</summary>
public static class IdeStreamKinds
{
    /// <summary>The agent run started for the session.</summary>
    public const string AgentStart = "agent_start";

    /// <summary>Partial assistant text (delta appended to the live message).</summary>
    public const string MessageDelta = "message_delta";

    /// <summary>The assistant message was finalized; <see cref="IdeStreamNotification.Text" /> carries the full text.</summary>
    public const string MessageEnd = "message_end";

    /// <summary>A tool call started; carries <c>tool_call_id</c>/<c>tool_name</c>.</summary>
    public const string ToolStart = "tool_start";

    /// <summary>A tool call finished; carries <c>tool_call_id</c> and <c>ok</c>.</summary>
    public const string ToolEnd = "tool_end";

    /// <summary>A turn started; carries <c>turn</c> (1-based).</summary>
    public const string TurnStart = "turn_start";

    /// <summary>A turn ended; carries <c>turn</c>.</summary>
    public const string TurnEnd = "turn_end";

    /// <summary>The agent run completed for the session.</summary>
    public const string AgentEnd = "agent_end";

    /// <summary>An unrecoverable agent error; carries <c>error</c>.</summary>
    public const string AgentError = "agent_error";
}

/// <summary>
///     Error raised by a bridge request handler; converted into a JSON-RPC
///     error response by the framing server.
/// </summary>
public sealed class IdeRpcException : Exception
{
    /// <summary>JSON-RPC error code.</summary>
    public int Code { get; }

    /// <summary>Create a <see cref="IdeRpcException.HandlerError" /> failure with no message.</summary>
    public IdeRpcException() : this(HandlerError, "IDE bridge request failed.")
    {
    }

    /// <summary>Create a typed bridge error.</summary>
    public IdeRpcException(int code, string message) : base(message) => Code = code;

    /// <summary>Create a <see cref="IdeRpcException.HandlerError" /> failure.</summary>
    public IdeRpcException(string message) : base(message) => Code = HandlerError;

    /// <summary>Create a <see cref="IdeRpcException.HandlerError" /> failure wrapping an inner exception.</summary>
    public IdeRpcException(string message, Exception innerException) : base(message, innerException)
        => Code = HandlerError;

    /// <summary>Create a typed bridge error wrapping an inner exception.</summary>
    public IdeRpcException(int code, string message, Exception innerException) : base(message, innerException)
        => Code = code;

    /// <summary>Standard JSON-RPC code: invalid request envelope (-32600).</summary>
    public const int InvalidRequest = -32600;

    /// <summary>Standard JSON-RPC code: method not found (-32601).</summary>
    public const int MethodNotFound = -32601;

    /// <summary>Standard JSON-RPC code: invalid params (-32602).</summary>
    public const int InvalidParams = -32602;

    /// <summary>Harbor code: request execution failed (-32000).</summary>
    public const int HandlerError = -32000;
}

/// <summary>
///     AOT-safe System.Text.Json contract for the bridge protocol. All
///     serialization goes through this <see cref="JsonSerializerContext"/> —
///     no reflection-based <c>Serialize&lt;object&gt;</c> anywhere (§PERF-002).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IdeListSessionsResult))]
[JsonSerializable(typeof(IdeInjectPromptParams))]
[JsonSerializable(typeof(IdeInjectPromptResult))]
[JsonSerializable(typeof(IdeReadStreamParams))]
[JsonSerializable(typeof(IdeReadStreamResult))]
[JsonSerializable(typeof(IdeAbortParams))]
[JsonSerializable(typeof(IdeAbortResult))]
[JsonSerializable(typeof(IdeStreamNotification))]
[JsonSerializable(typeof(IdePromptErrorNotification))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
internal sealed partial class IdeJsonContext : JsonSerializerContext;
