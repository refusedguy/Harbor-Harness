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
    ///     Web-default JSON serializer options shared with
    ///     <c>JsonlSessionStore</c> for <c>Usage</c> deserialization.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Project an <see cref="AgentMessage" /> into the role-specific
    ///     payload shape that gets serialized as the <c>payload</c> field
    ///     of the JSONL line.
    /// </summary>
    public static object SerializeMessagePayload(AgentMessage message)
    {
        return message switch
        {
            UserMessage u => new { content = u.Content, agent = u.Agent, model = u.Model },
            AssistantMessage a => new
            {
                parts = a.Parts.Select(SerializePart).ToArray(),
                stopReason = a.StopReason.ToString().ToLowerInvariant(),
                usage = a.Usage,
                model = a.Model,
                isSummary = a.IsSummary,
                summaryFirstKeptId = a.SummaryFirstKeptId
            },
            ToolResultMessage tr => new { results = tr.Results },
            _ => new { }
        };
    }

    /// <summary>
    ///     Project a single <see cref="ContentPart" /> into its JSON shape.
    /// </summary>
    public static object SerializePart(ContentPart part) => part switch
    {
        TextPart t => new { type = "text", text = t.Text },
        ThinkingPart th => new { type = "thinking", text = th.Text },
        ToolCallPart tc => new { type = "tool_call", id = tc.Id, toolName = tc.ToolName, args = tc.Args },
        FilePart f => new { type = "file", path = f.Path, mimeType = f.MimeType, sizeBytes = f.SizeBytes },
        _ => new { type = "unknown" }
    };

    /// <summary>
    ///     Parse a single JSONL line back into an <see cref="AgentMessage" />.
    ///     Returns <see cref="Result{T}" /> so the caller can surface a
    ///     diagnostic message rather than silently dropping the line.
    /// </summary>
    /// <param name="sessionId">The session id to embed in the reconstructed message.</param>
    /// <param name="element">The parsed JSON element for the line.</param>
    public static Result<AgentMessage> DeserializeMessage(string sessionId, JsonElement element)
    {
        string? id;
        try
        {
            id = element.GetProperty("id").GetString();
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>($"missing 'id': {ex.Message}");
        }
        if (string.IsNullOrEmpty(id))
            return Result.Failure<AgentMessage>("'id' is null or empty");

        DateTimeOffset createdAt;
        try
        {
            createdAt = element.GetProperty("createdAt").GetDateTimeOffset();
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>($"message {id}: missing/invalid 'createdAt': {ex.Message}");
        }

        string? parentId = element.TryGetProperty("parentId", out var p) ? p.GetString() : null;
        string? role = element.TryGetProperty("role", out var r) ? r.GetString() : null;
        if (string.IsNullOrEmpty(role))
            return Result.Failure<AgentMessage>($"message {id}: missing 'role'");

        if (!element.TryGetProperty("payload", out var payload))
            return Result.Failure<AgentMessage>($"message {id}: missing 'payload'");

        if (role == "user")
        {
            string? content = payload.TryGetProperty("content", out var c) ? c.GetString() : null;
            string? agent = payload.TryGetProperty("agent", out var a) ? a.GetString() : null;
            string? model = payload.TryGetProperty("model", out var m) ? m.GetString() : null;
            if (content is null || agent is null || model is null)
                return Result.Failure<AgentMessage>($"user message {id}: missing content/agent/model");

            return Result.Success<AgentMessage>(new UserMessage(
                id!, sessionId, createdAt, content!, agent!, model!, parentId));
        }

        if (role == "assistant")
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
            StopReason stopReason;
            try
            {
                stopReason = Enum.Parse<StopReason>(srEl.GetString()!, true);
            }
            catch (Exception ex)
            {
                return Result.Failure<AgentMessage>($"assistant message {id}: invalid stopReason: {ex.Message}");
            }

            var usage = payload.TryGetProperty("usage", out var u)
                ? u.Deserialize<Usage>(JsonOptions) ?? new Usage(0, 0)
                : new Usage(0, 0);
            string? model = payload.TryGetProperty("model", out var m) ? m.GetString() : null;
            if (model is null)
                return Result.Failure<AgentMessage>($"assistant message {id}: missing 'model'");

            bool isSummary = payload.TryGetProperty("isSummary", out var s) && s.GetBoolean();
            string? summaryFirstKeptId = payload.TryGetProperty("summaryFirstKeptId", out var sf) ? sf.GetString() : null;

            return Result.Success<AgentMessage>(new AssistantMessage(
                id!, sessionId, createdAt, parts, stopReason, usage, model!, parentId, isSummary, summaryFirstKeptId));
        }

        if (role == "tool_result")
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

                results.Add(new ToolResultEntry(tcId!, tn!, output!, isError));
            }

            return Result.Success<AgentMessage>(new ToolResultMessage(
                id!, sessionId, createdAt, results, parentId));
        }

        return Result.Failure<AgentMessage>($"message {id}: unknown role '{role}'");
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
