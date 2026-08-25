namespace Harbor.Storage.Jsonl;
/// <summary>
///     Stateless JSON codec for <see cref="AgentMessage" /> / <see cref="ContentPart" />
///     serialization to/from the JSONL wire format. Extracted from
///     <c>JsonlSessionStore</c> (Task R31 god-object decomposition) so the
///     store can focus on file I/O + caching, while this class owns the
///     schema evolution concerns (versioning, polymorphic payload shapes,
///     graceful failure on malformed lines).
/// </summary>
/// <remarks>
///     <para>
///         <b>Format:</b> each line is a JSON object with
///         <c>{ id, createdAt, parentId?, role, payload }</c>. The
///         <c>payload</c> shape is role-specific:
///         <list type="bullet">
///             <item><c>user</c> → <c>{ content, agent, model }</c></item>
///             <item><c>assistant</c> → <c>{ parts, stopReason, usage, model, isSummary?, summaryFirstKeptId? }</c></item>
///             <item><c>tool_result</c> → <c>{ results: [{ toolCallId, toolName, output, isError }] }</c></item>
///         </list>
///     </para>
///     <para>
///         <b>Why Result-returning?</b> the original <c>null</c>-returning
///         deserializer silently dropped malformed lines (§ROP-001 audit).
///         Now each branch returns <see cref="Result{T}" /> with a specific
///         error message so the caller can log + skip without losing
///         diagnostic information.
///     </para>
/// </remarks>
internal static class JsonlMessageCodec
{
    /// <summary>
    ///     JSON serializer options — delegates to <see cref="JsonlCodecContext.JsonOptions" />
    ///     which includes the AOT-registered <see cref="JsonlCodecContext" /> as
    ///     <see cref="JsonSerializerOptions.TypeInfoResolver" />.
    /// </summary>
    public static JsonSerializerOptions JsonOptions => JsonlCodecContext.JsonOptions;

    /// <summary>
    ///     Project an <see cref="AgentMessage" /> into the role-specific
    ///     payload shape that gets serialized as the <c>payload</c> field
    ///     of the JSONL line. Uses named DTO types (AOT-registered in
    ///     <see cref="JsonlCodecContext" />) instead of anonymous types.
    /// </summary>
    public static object SerializeMessagePayload(AgentMessage message)
    {
        return message switch
        {
            UserMessage u => new UserPayload(u.Content, u.Agent, u.Model),
            AssistantMessage a => new AssistantPayload(
                Parts: a.Parts.Select(SerializePart).ToArray(),
                StopReason: a.StopReason.ToString().ToLowerInvariant(),
                Usage: a.Usage,
                Model: a.Model,
                IsSummary: a.IsSummary,
                SummaryFirstKeptId: a.SummaryFirstKeptId),
            ToolResultMessage tr => new ToolResultPayload(tr.Results.ToArray()),
            _ => new UnknownPartPayload("unknown")
        };
    }

    /// <summary>
    ///     Project a single <see cref="ContentPart" /> into its JSON shape
    ///     using a named DTO type (AOT-registered).
    /// </summary>
    public static object SerializePart(ContentPart part) => part switch
    {
        TextPart t => new TextPartPayload("text", t.Text),
        ThinkingPart th => new ThinkingPartPayload("thinking", th.Text),
        ToolCallPart tc => new ToolCallPartPayload("tool_call", tc.Id, tc.ToolName, tc.Args),
        FilePart f => new FilePartPayload("file", f.Path, f.MimeType, f.SizeBytes),
        _ => new UnknownPartPayload("unknown")
    };

    /// <summary>
    ///     Parse a single JSONL line back into an <see cref="AgentMessage" />.
    ///     Returns <see cref="Result{T}" /> so the caller can surface a
    ///     diagnostic message rather than silently dropping the line.
    /// </summary>
    /// <remarks>
    ///     Field extraction rides the railway (ROP-B П.10):
    ///     <see cref="Required" /> wraps the per-field try/catch in
    ///     <c>Result.Try + Ensure</c>, and the id → createdAt → body steps
    ///     chain through <c>Bind</c> so a malformed field short-circuits with
    ///     its own diagnostic instead of a ladder of catch blocks.
    /// </remarks>
    /// <param name="sessionId">The session id to embed in the reconstructed message.</param>
    /// <param name="element">The parsed JSON element for the line.</param>
    public static Result<AgentMessage> DeserializeMessage(string sessionId, JsonElement element)
    {
        return Required(element, "id")
            .Bind(id => RequiredCreatedAt(element, id).Map(createdAt => (id, createdAt)))
            .Bind(x => BuildMessage(sessionId, x.id, x.createdAt, element));
    }

    /// <summary>Read a mandatory string field: absence/shape errors and empty values both fail.</summary>
    private static Result<string> Required(JsonElement element, string field) =>
        Result.Try(
                () => element.GetProperty(field).GetString() ?? string.Empty,
                ex => $"missing '{field}': {ex.Message}")
            .Ensure(v => v.Length > 0, $"'{field}' is null or empty");

    private static Result<DateTimeOffset> RequiredCreatedAt(JsonElement element, string id) =>
        Result.Try(
            () => element.GetProperty("createdAt").GetDateTimeOffset(),
            ex => $"message {id}: missing/invalid 'createdAt': {ex.Message}");

    private static Result<AgentMessage> BuildMessage(
        string sessionId, string id, DateTimeOffset createdAt, JsonElement element)
    {
        string? parentId = element.TryGetProperty("parentId", out var p) ? p.GetString() : null;
        string? role = element.TryGetProperty("role", out var r) ? r.GetString() : null;
        if (string.IsNullOrEmpty(role))
            return Result.Failure<AgentMessage>($"message {id}: missing 'role'");

        if (!element.TryGetProperty("payload", out var payload))
            return Result.Failure<AgentMessage>($"message {id}: missing 'payload'");

        return role switch
        {
            "user" => DecodeUser(sessionId, id, createdAt, parentId, payload),
            "assistant" => DecodeAssistant(sessionId, id, createdAt, parentId, payload),
            "tool_result" => DecodeToolResult(sessionId, id, createdAt, parentId, payload),
            _ => Result.Failure<AgentMessage>($"message {id}: unknown role '{role}'")
        };
    }

    private static Result<AgentMessage> DecodeUser(
        string sessionId, string id, DateTimeOffset createdAt, string? parentId, JsonElement payload)
    {
        string? content = payload.TryGetProperty("content", out var c) ? c.GetString() : null;
        string? agent = payload.TryGetProperty("agent", out var a) ? a.GetString() : null;
        string? model = payload.TryGetProperty("model", out var m) ? m.GetString() : null;
        if (content is null || agent is null || model is null)
            return Result.Failure<AgentMessage>($"user message {id}: missing content/agent/model");

        return Result.Success<AgentMessage>(new UserMessage(
            id, sessionId, createdAt, content, agent, model, parentId));
    }

    private static Result<AgentMessage> DecodeAssistant(
        string sessionId, string id, DateTimeOffset createdAt, string? parentId, JsonElement payload)
    {
        if (!payload.TryGetProperty("parts", out var partsEl) || partsEl.ValueKind != JsonValueKind.Array)
            return Result.Failure<AgentMessage>($"assistant message {id}: missing 'parts'");

        var parts = new List<ContentPart>();
        foreach (var partEl in partsEl.EnumerateArray())
        {
            var part = DeserializePart(partEl);
            if (part is not null) parts.Add(part);
        }

        if (!payload.TryGetProperty("stopReason", out var srEl) || srEl.ValueKind != JsonValueKind.String)
            return Result.Failure<AgentMessage>($"assistant message {id}: missing 'stopReason'");

        bool isSummary = payload.TryGetProperty("isSummary", out var s) && s.GetBoolean();
        string? summaryFirstKeptId = payload.TryGetProperty("summaryFirstKeptId", out var sf) ? sf.GetString() : null;

        return Result.Try(() => Enum.Parse<StopReason>(srEl.GetString()!, true),
                ex => $"assistant message {id}: invalid stopReason: {ex.Message}")
            .Bind(stopReason => RequiredModel(payload, id).Map(model => (AgentMessage)new AssistantMessage(
                id,
                sessionId,
                createdAt,
                parts,
                stopReason,
                ParseUsage(payload),
                model,
                parentId,
                isSummary,
                summaryFirstKeptId)));
    }

    private static Usage ParseUsage(JsonElement payload) =>
        payload.TryGetProperty("usage", out var u)
            ? JsonSerializer.Deserialize<Usage>(u.GetRawText(), JsonlCodecContext.Default.Usage) ?? new Usage(0, 0)
            : new Usage(0, 0);

    private static Result<string> RequiredModel(JsonElement payload, string id)
    {
        string? model = payload.TryGetProperty("model", out var m) ? m.GetString() : null;
        return model is null
            ? Result.Failure<string>($"assistant message {id}: missing 'model'")
            : Result.Success(model);
    }

    private static Result<AgentMessage> DecodeToolResult(
        string sessionId, string id, DateTimeOffset createdAt, string? parentId, JsonElement payload)
    {
        if (!payload.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
            return Result.Failure<AgentMessage>($"tool_result message {id}: missing 'results'");

        var results = new List<ToolResultEntry>();
        foreach (var rEl in resultsEl.EnumerateArray())
        {
            string? tcId = rEl.TryGetProperty("toolCallId", out var tci) ? tci.GetString() : null;
            string? tn = rEl.TryGetProperty("toolName", out var tnEl) ? tnEl.GetString() : null;
            string? output = rEl.TryGetProperty("output", out var o) ? o.GetString() : null;
            bool isError = rEl.TryGetProperty("isError", out var ie) && ie.GetBoolean();
            if (tcId is null || tn is null || output is null)
                return Result.Failure<AgentMessage>($"tool_result message {id}: malformed result entry");

            results.Add(new ToolResultEntry(tcId, tn, output, isError));
        }

        return Result.Success<AgentMessage>(new ToolResultMessage(
            id, sessionId, createdAt, results, parentId));
    }

    /// <summary>
    ///     Parse a single <see cref="ContentPart" /> from its JSON shape.
    ///     Returns null for unknown <c>type</c> values (forward-compat
    ///     with future part types).
    /// </summary>
    public static ContentPart? DeserializePart(JsonElement element)
    {
        string? type = element.GetProperty("type").GetString();
        return type switch
        {
            "text" => new TextPart(element.GetProperty("text").GetString()!),
            "thinking" => new ThinkingPart(element.GetProperty("text").GetString()!),
            "tool_call" => new ToolCallPart(
                element.GetProperty("id").GetString()!,
                element.GetProperty("toolName").GetString()!,
                element.GetProperty("args").Deserialize<JsonElement>()),
            "file" => new FilePart(
                element.GetProperty("path").GetString()!,
                element.GetProperty("mimeType").GetString()!,
                element.GetProperty("sizeBytes").GetInt64()),
            _ => null
        };
    }
}
