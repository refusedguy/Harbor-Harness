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
    /// <remarks>
    ///     <para>
    ///         <b>#51:</b> <see cref="ToolResultEntry.Metadata" /> is deliberately
    ///         dropped here. It is <c>object?</c>-typed (arbitrary runtime content),
    ///         so the source-generated <see cref="JsonlCodecContext" /> has no
    ///         <c>TypeInfo</c> for it and serialization throws per turn whenever a
    ///         tool attaches non-null metadata. The read path
    ///         (<see cref="JsonlLineParser" />) never restores metadata either, and
    ///         the <c>ToolResult.Success/Error</c> contract documents it as
    ///         "not serialized" — dropping on write is fidelity-neutral.
    ///     </para>
    ///     <para>
    ///         <b>#550:</b> a message kind this build does not know is refused
    ///         rather than written out as a placeholder payload. The callers
    ///         (<see cref="JsonlSessionStore" />, <see cref="SessionPorter" />) run
    ///         inside a <c>Result.Try</c> and surface the refusal as a logged error,
    ///         which is the same treatment the SQLite writer has had since #461.
    ///     </para>
    /// </remarks>
    public static object SerializeMessagePayload(AgentMessage message)
    {
        return message switch
        {
            UserMessage u => new UserPayload(
                u.Content, u.Agent, u.Model,
                Attachments: SerializeAttachments(u.Attachments)),
            AssistantMessage a => new AssistantPayload(
                Parts: a.Parts.Select(SerializePart).ToArray(),
                StopReason: a.StopReason.ToString().ToLowerInvariant(),
                Usage: a.Usage,
                Model: a.Model,
                IsSummary: a.IsSummary,
                SummaryFirstKeptId: a.SummaryFirstKeptId),
            ToolResultMessage tr => new ToolResultPayload(
                tr.Results.Select(r => new ToolResultEntry(r.ToolCallId, r.ToolName, r.Output, r.IsError)).ToArray()),
            _ => throw Unsupported(message.Role, message.GetType().Name, "message")
        };
    }

    /// <summary>
    ///     Project a single <see cref="ContentPart" /> into its JSON shape
    ///     using a named DTO type (AOT-registered).
    /// </summary>
    /// <remarks>
    ///     #550: the former <c>_ => new UnknownPartPayload("unknown")</c> wrote a
    ///     part this codec's own readers cannot decode — the read side dropped it, so
    ///     a message written by a build that knew the kind came back without it. The
    ///     refusal matches <see cref="ContentPartVisitor{TResult}.Accept" />, which is
    ///     what the SQLite writer has done since #461: a store must not persist
    ///     something it will not read back.
    /// </remarks>
    public static object SerializePart(ContentPart part) => part switch
    {
        TextPart t => new TextPartPayload("text", t.Text),
        ThinkingPart th => new ThinkingPartPayload("thinking", th.Text),
        ToolCallPart tc => new ToolCallPartPayload("tool_call", tc.Id, tc.ToolName, tc.Args),
        FilePart f => new FilePartPayload("file", f.Path, f.MimeType, f.SizeBytes),
        _ => throw Unsupported(part.Type, part.GetType().Name, "part")
    };

    /// <summary>
    ///     The refusal for a kind the codec has no shape for. Mirrors
    ///     <see cref="ContentPartVisitor{TResult}.VisitUnknown" /> so both stores
    ///     report the same class of drift the same way.
    /// </summary>
    private static NotSupportedException Unsupported(string tag, string typeName, string what) =>
        new($"{what} kind '{typeName}' (type discriminator '{tag}') has no JSONL shape. "
            + "A new subtype must ship with a SerializePart/SerializeMessagePayload arm; writing a placeholder "
            + "would persist a record the read path cannot read back.");

    /// <summary>
    ///     Project the images a user attached to a turn (issue #386). Returns
    ///     <see langword="null" /> for text-only turns so the field is omitted
    ///     from the line entirely (<c>WhenWritingNull</c>) — session files written
    ///     before this feature stay byte-identical.
    /// </summary>
    public static ImageAttachmentPayload[]? SerializeAttachments(IReadOnlyList<ImageAttachment>? attachments)
    {
        if (attachments is not { Count: > 0 })
            return null;

        var payload = new ImageAttachmentPayload[attachments.Count];
        for (int i = 0; i < attachments.Count; i++)
        {
            ImageAttachment image = attachments[i];
            payload[i] = new ImageAttachmentPayload(image.Path, image.MimeType, image.Width, image.Height, image.Data);
        }

        return payload;
    }

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
            id, sessionId, createdAt, content, agent, model, parentId,
            DecodeAttachments(payload)));
    }

    /// <summary>
    ///     Read the <c>attachments</c> array of a user payload (issue #386).
    ///     Absent (every pre-#386 line) → <see langword="null" />, so a text-only
    ///     turn round-trips to exactly the message it was written from.
    /// </summary>
    private static IReadOnlyList<ImageAttachment>? DecodeAttachments(JsonElement payload)
    {
        if (!payload.TryGetProperty("attachments", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        var images = new List<ImageAttachment>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                continue;

            string? path = el.TryGetProperty("path", out var p) ? p.GetString() : null;
            string? mime = el.TryGetProperty("mimeType", out var mt) ? mt.GetString() : null;
            if (path is null || mime is null)
            {
                // Malformed entry: skip it rather than drop the whole message —
                // a broken attachment must never make a user turn unreadable.
                continue;
            }

            int width = el.TryGetProperty("width", out var w) && w.TryGetInt32(out int wi) ? wi : 0;
            int height = el.TryGetProperty("height", out var h) && h.TryGetInt32(out int hi) ? hi : 0;
            byte[] data = el.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetBytesFromBase64()
                : [];

            images.Add(new ImageAttachment(path, mime, width, height, data));
        }

        return images.Count == 0 ? null : images;
    }

    private static Result<AgentMessage> DecodeAssistant(
        string sessionId, string id, DateTimeOffset createdAt, string? parentId, JsonElement payload)
    {
        if (!payload.TryGetProperty("parts", out var partsEl) || partsEl.ValueKind != JsonValueKind.Array)
            return Result.Failure<AgentMessage>($"assistant message {id}: missing 'parts'");

        if (!payload.TryGetProperty("stopReason", out var srEl) || srEl.ValueKind != JsonValueKind.String)
            return Result.Failure<AgentMessage>($"assistant message {id}: missing 'stopReason'");

        bool isSummary = payload.TryGetProperty("isSummary", out var s) && s.GetBoolean();
        string? summaryFirstKeptId = payload.TryGetProperty("summaryFirstKeptId", out var sf) ? sf.GetString() : null;

        // #550: the parts array has no null-skipping filter any more. A part that does
        // not decode names its tag, and the message fails with it — the importer then
        // logs one skipped line and imports the rest, instead of a part going missing
        // from the imported session without a word. MapError puts the message id on the
        // refusal, because "a line was skipped" is not actionable in a large export.
        return DecodeParts(partsEl)
            .MapError(error => $"assistant message {id}: {error}")
            .Bind(parts => Result.Try(() => Enum.Parse<StopReason>(srEl.GetString()!, true),
                    ex => $"assistant message {id}: invalid stopReason: {ex.Message}")
                .Map(stopReason => (stopReason, parts)))
            .Bind(x => RequiredModel(payload, id)
                .Map(model => (AgentMessage)new AssistantMessage(
                    id,
                    sessionId,
                    createdAt,
                    x.parts,
                    x.stopReason,
                    ParseUsage(payload),
                    model,
                    parentId,
                    isSummary,
                    summaryFirstKeptId)));
    }

    /// <summary>
    ///     Decode a persisted <c>parts</c> array. Every entry must decode: a part this
    ///     build cannot rebuild is not a part the reader may drop, and the only other
    ///     option — a shorter message than the one that was written — is the bug.
    /// </summary>
    private static Result<IReadOnlyList<ContentPart>> DecodeParts(JsonElement partsEl) =>
        Result.Try<IReadOnlyList<ContentPart>>(
            () =>
            {
                var parts = new List<ContentPart>(partsEl.GetArrayLength());
                foreach (var partEl in partsEl.EnumerateArray())
                    parts.Add(DeserializePart(partEl));

                return parts;
            },
            ex => ex.Message);

    /// <summary>
    ///     Legacy-tolerant usage reader: pre-V4 files stored Usage PascalCase
    ///     (no naming policy), post-fix files are camelCase. Read both, prefer
    ///     whichever field exists.
    /// </summary>
    private static Usage ParseUsage(JsonElement payload)
    {
        if (!payload.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
            return new Usage(0, 0);

        static int? Num(JsonElement el, string camel, string pascal)
            => el.TryGetProperty(camel, out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : el.TryGetProperty(pascal, out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetInt32()
                    : null;

        int input = Num(u, "inputTokens", "InputTokens") ?? 0;
        int output = Num(u, "outputTokens", "OutputTokens") ?? 0;
        return new Usage(
            input,
            output,
            ReasoningTokens: Num(u, "reasoningTokens", "ReasoningTokens"),
            CacheReadTokens: Num(u, "cacheReadTokens", "CacheReadTokens"),
            CacheWriteTokens: Num(u, "cacheWriteTokens", "CacheWriteTokens"));
    }

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
        if ((!payload.TryGetProperty("results", out var resultsEl) && !payload.TryGetProperty("Results", out resultsEl))
            || resultsEl.ValueKind != JsonValueKind.Array)
            return Result.Failure<AgentMessage>($"tool_result message {id}: missing 'results'");

        var results = new List<ToolResultEntry>();
        foreach (var rEl in resultsEl.EnumerateArray())
        {
            // Field names tolerate legacy PascalCase (pre-V4 files written without
            // a naming policy) and post-fix camelCase.
            string? tcId = FirstString(rEl, "toolCallId", "ToolCallId");
            string? tn = FirstString(rEl, "toolName", "ToolName");
            string? output = FirstString(rEl, "output", "Output");
            bool isError = rEl.TryGetProperty("isError", out var ie) && ie.GetBoolean()
                           || rEl.TryGetProperty("IsError", out var ie2) && ie2.GetBoolean();
            if (tcId is null || tn is null || output is null)
                return Result.Failure<AgentMessage>($"tool_result message {id}: malformed result entry");

            results.Add(new ToolResultEntry(tcId, tn, output, isError));
        }

        return Result.Success<AgentMessage>(new ToolResultMessage(
            id, sessionId, createdAt, results, parentId));
    }

    /// <summary>First non-null string among alias property names (legacy + current casing).</summary>
    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        return null;
    }

    /// <summary>
    ///     Parse a single JSONL part back into its <see cref="ContentPart" />.
    /// </summary>
    /// <remarks>
    ///     #550 — this is a tag → type factory, not a walk over the part union, so
    ///     <see cref="ContentPartVisitor{TResult}" /> never reached it and its
    ///     <c>_ => null</c> fed a <c>DeserializePart(partEl) is not null</c> filter in
    ///     <see cref="DecodeAssistant" />: a part tag this build does not know vanished
    ///     from the reloaded transcript with no error anywhere. Every refusal here is
    ///     loud, and refusing an unknown tag is the *only* honest answer — the union has
    ///     no case for "a part I could not read", and the "forward-compat with future
    ///     part types" this used to claim described a data loss, not a compatibility.
    ///     The store's own read path refuses the same tags the same way
    ///     (<see cref="JsonlLineParser" />); the two have to agree.
    /// </remarks>
    /// <exception cref="JsonException">
    ///     The tag is unknown to this build, or the part is missing a member its tag
    ///     requires. Both name the part.
    /// </exception>
    public static ContentPart DeserializePart(JsonElement element)
    {
        if (!element.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("content part has no 'type' discriminator, so it cannot be rebuilt.");
        }

        string type = typeElement.GetString()!;
        switch (type)
        {
            case "text":
                return new TextPart(RequiredString(element, "text", type));
            case "thinking":
                return new ThinkingPart(RequiredString(element, "text", type));
            case "tool_call":
                return new ToolCallPart(
                    RequiredString(element, "id", type),
                    RequiredString(element, "toolName", type),
                    // Tolerated exactly as before: a tool_call row with no args at all
                    // is read with an undefined element, which is a faithful reading of
                    // "this part carries no arguments".
                    element.TryGetProperty("args", out var args) ? args.Deserialize<JsonElement>() : default);
            case "file":
                return new FilePart(
                    RequiredString(element, "path", type),
                    RequiredString(element, "mimeType", type),
                    RequiredInt64(element, "sizeBytes", type));
            default:
                throw new JsonException(
                    $"content part type '{type}' is not known to this build — this payload was written by a newer Harbor, "
                    + "and the message is refused rather than read with the part missing");
        }
    }

    /// <summary>
    ///     A mandatory string member of a part. A missing one used to reach
    ///     <c>GetProperty</c> and throw <see cref="KeyNotFoundException" /> straight out
    ///     of the porter's decode, where nothing was waiting to catch it — the whole
    ///     import died instead of one line being skipped. It is a named
    ///     <see cref="JsonException" /> now, which the caller's <c>Result.Try</c> turns
    ///     into the per-line diagnostic that path already has a channel for.
    /// </summary>
    private static string RequiredString(JsonElement element, string field, string tag)
    {
        if (!element.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            throw new JsonException($"content part of type '{tag}' is missing '{field}'.");

        return value.GetString()!;
    }

    private static long RequiredInt64(JsonElement element, string field, string tag)
    {
        if (!element.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Number)
            throw new JsonException($"content part of type '{tag}' is missing '{field}'.");

        return value.GetInt64();
    }
}
