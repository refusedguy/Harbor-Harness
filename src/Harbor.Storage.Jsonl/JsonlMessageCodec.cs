using Harbor.Abstractions.Tools;
using Harbor.Storage.Shared;

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
                Parts: SerializeParts(a.Parts),
                StopReason: StopReasonLower(a.StopReason),
                Usage: a.Usage,
                Model: a.Model,
                IsSummary: a.IsSummary,
                SummaryFirstKeptId: a.SummaryFirstKeptId),
            ToolResultMessage tr => new ToolResultPayload(
                SerializeToolResults(tr.Results)),
            _ => throw Unsupported(message.Role, message.GetType().Name, "message")
        };
    }

    /// <summary>
    ///     Project a single <see cref="ContentPart" /> into its JSON shape
    ///     using a named DTO type (AOT-registered). Tag strings come from
    ///     <c>Harbor.Storage.Shared.MessagePartAdapter</c> — the one place the
    ///     persisted discriminator vocabulary is declared (#1591).
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
        TextPart t => new TextPartPayload(MessagePartAdapter.TextTag, t.Text),
        ThinkingPart th => new ThinkingPartPayload(MessagePartAdapter.ThinkingTag, th.Text),
        ToolCallPart tc => new ToolCallPartPayload(MessagePartAdapter.ToolCallTag, tc.Id, tc.ToolName, tc.Args),
        FilePart f => new FilePartPayload(MessagePartAdapter.FileTag, f.Path, f.MimeType, f.SizeBytes),
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
    ///     Project the assistant <see cref="ContentPart" /> list into the JSON
    ///     shapes, pre-sizing the result array from the source count (issue
    ///     #1531). Replaced the <c>Select(SerializePart).ToArray()</c> chain,
    ///     which cost an iterator + an oversized growable array per persisted
    ///     assistant message on the append path of every turn. The on-disk
    ///     shape is unchanged: the same payload objects, in the same order.
    /// </summary>
    /// <remarks>
    ///     <see cref="SerializeAttachments" /> predates this and already used
    ///     the manual pre-sized loop — this is the same shape generalized.
    /// </remarks>
    public static object[] SerializeParts(IReadOnlyList<ContentPart> parts)
    {
        if (parts.Count == 0)
            return [];

        var payload = new object[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            payload[i] = SerializePart(parts[i]);
        }

        return payload;
    }

    /// <summary>
    ///     Project the tool-result entries of a <see cref="ToolResultMessage" />
    ///     into their JSON shape, pre-sizing the result array from the source
    ///     count (issue #1531). Replaced the <c>Select(r => new
    ///     ToolResultEntry(...)).ToArray()</c> chain on the same append path.
    ///     <see cref="ToolResultEntry.Metadata" /> is still dropped — see #51
    ///     in the <see cref="SerializeMessagePayload" /> remarks.
    ///     <para>
    ///         #1763: the deferred contexts ride with the entry, so a session
    ///         reload keeps the context the tool handed over. The property is
    ///         omitted from the line when it is <c>null</c>, so a session
    ///         written before #1763 keeps the byte-identical shape it had.
    ///     </para>
    /// </summary>
    public static ToolResultEntry[] SerializeToolResults(IReadOnlyList<ToolResultEntry> results)
    {
        if (results.Count == 0)
            return [];

        var payload = new ToolResultEntry[results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            ToolResultEntry r = results[i];
            payload[i] = new ToolResultEntry(r.ToolCallId, r.ToolName, r.Output, r.IsError)
            {
                DeferredContexts = r.DeferredContexts
            // #1760: the structured code rides along. It holds primitives
            // only, so unlike Metadata it survives the round-trip — which is
            // the whole point of a separate typed field.
                ErrorInfo = r.ErrorInfo,
            };
        }

        return payload;
    }

    /// <summary>
    ///     The lowercase wire spelling of a <see cref="StopReason" /> (issue
    ///     #1531). Replaces <c>ToString().ToLowerInvariant()</c> — an enum
    ///     name materialization + a lowercase copy, two allocations per
    ///     assistant message per turn. The arm bodies are the enum members'
    ///     names lowered, so the on-disk contract is byte-identical with what
    ///     <c>ToString().ToLowerInvariant()</c> wrote: <see cref="StopReason.ToolUse" />
    ///     persists as <c>"tooluse"</c> (and reads back through
    ///     <see cref="StopReasonTable.TryParseSpan" />; <c>"tool_use"</c> is a
    ///     provider-side alias, NOT the JSONL spelling).
    /// </summary>
    /// <remarks>
    ///     A discard-less switch over an enum does not compile — CS8524 is an
    ///     unconditional error under the build's warnings-as-errors (the
    ///     compiler demands the unnamed enum domain too). So the gate is
    ///     layered like <c>ToolCallState</c>'s: every member is named
    ///     explicitly and the unnamed domain (<c>(StopReason)99</c>) throws
    ///     loudly instead of silently serializing a numeric spelling.
    /// </remarks>
    private static string StopReasonLower(StopReason reason) => reason switch
    {
        StopReason.Stop => "stop",
        StopReason.Length => "length",
        StopReason.ToolUse => "tooluse",
        StopReason.ContentFilter => "contentfilter",
        StopReason.Error => "error",
        StopReason.Aborted => "aborted",
        _ => throw new InvalidOperationException(
            $"StopReason '{reason}' is an unnamed enum value and has no lowercase wire spelling: "
            + "name it explicitly with the lowered member name body (issue #1531)."),
    };

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
            string? tcId = JsonArgs.GetFirstString(rEl, "toolCallId", "ToolCallId");
            string? tn = JsonArgs.GetFirstString(rEl, "toolName", "ToolName");
            string? output = JsonArgs.GetFirstString(rEl, "output", "Output");
            bool isError = rEl.TryGetProperty("isError", out var ie) && ie.GetBoolean()
                           || rEl.TryGetProperty("IsError", out var ie2) && ie2.GetBoolean();
            if (tcId is null || tn is null || output is null)
                return Result.Failure<AgentMessage>($"tool_result message {id}: malformed result entry");

            results.Add(new ToolResultEntry(tcId, tn, output, isError)
            {
                DeferredContexts = DecodeDeferredContexts(rEl)
                ErrorInfo = DecodeErrorInfo(rEl),
            });
        }

        return Result.Success<AgentMessage>(new ToolResultMessage(
            id, sessionId, createdAt, results, parentId));
    }

    /// <summary>
    ///     Read the deferred contexts of one persisted result entry (#1763).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The three states of the contract survive the round trip: an
    ///         absent array means the tool had no deferred context to give
    ///         (<c>null</c>), an empty array means it promised one and it did
    ///         not arrive, and a filled array carries the contexts.
    ///     </para>
    ///     <para>
    ///         A malformed entry is skipped rather than failing the message:
    ///         one broken context must not make the whole tool-result message
    ///         unreadable — the same treatment the image attachments get.
    ///     </para>
    /// </remarks>
    /// <param name="entryEl">The persisted result entry.</param>
    /// <returns>The decoded contexts, or <see langword="null" /> when the entry carries none.</returns>
    private static IReadOnlyList<DeferredToolContext>? DecodeDeferredContexts(JsonElement entryEl)
    {
        if (!entryEl.TryGetProperty("deferredContexts", out var arr) && !entryEl.TryGetProperty("DeferredContexts", out arr))
    ///     Read the structured error of a persisted result entry (#1760).
    ///     An entry written before the structured code existed has no
    ///     <c>errorInfo</c> member at all, and that is not a defect: the code
    ///     was added later, and an absent member is the honest reading of a
    ///     record that never carried one. A member that IS there but has no
    ///     <c>code</c> is unreadable — the code is the record — and yields no
    ///     error rather than a half-built one.
    private static ToolErrorInfo? DecodeErrorInfo(JsonElement entry)
        if (!entry.TryGetProperty("errorInfo", out var error) && !entry.TryGetProperty("ErrorInfo", out error))
        {
            return null;
        }

        if (arr.ValueKind != JsonValueKind.Array)
        if (error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var contexts = new List<DeferredToolContext>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? content = JsonArgs.GetFirstString(el, "content", "Content");
            string? source = JsonArgs.GetFirstString(el, "source", "Source");
            if (content is null || source is null)
            {
                // A context without content or without a source cannot be
                // framed for the model — the source is what tells the model
                // the context is not the user's words.
                continue;
            }

            contexts.Add(new DeferredToolContext(content, source, DecodeDeferredMetadata(el)));
        }

        return contexts;
    }

    /// <summary>
    ///     Read the metadata pairs of one persisted deferred context (#1763).
    /// </summary>
    /// <param name="contextEl">The persisted deferred context.</param>
    /// <returns>The decoded pairs, or <see langword="null" /> when there are none.</returns>
    private static IReadOnlyList<DeferredContextMetadata>? DecodeDeferredMetadata(JsonElement contextEl)
    {
        if (!contextEl.TryGetProperty("metadata", out var arr) && !contextEl.TryGetProperty("Metadata", out arr))
        {
            return null;
        }

        if (arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var metadata = new List<DeferredContextMetadata>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? key = JsonArgs.GetFirstString(el, "key", "Key");
            string? value = JsonArgs.GetFirstString(el, "value", "Value");
            if (key is null)
            {
                continue;
            }

            metadata.Add(new DeferredContextMetadata(key, value ?? string.Empty));
        }

        return metadata.Count == 0 ? null : metadata;
        string? code = JsonArgs.GetFirstString(error, "code", "Code");
        if (code is null)
        string? name = JsonArgs.GetFirstString(error, "name", "Name");
        string? reason = JsonArgs.GetFirstString(error, "reason", "Reason");
        return new ToolErrorInfo(name ?? ToolErrorCodes.NameFor(code), code, reason);
    }

    /// <summary>
    ///     Parse a single JSONL part back into its <see cref="ContentPart" />.
    /// </summary>
    /// <remarks>
    ///     #550 — the part object's projection is the shared
    ///     <c>Harbor.Storage.Shared.MessagePartAdapter</c> tag → type factory
    ///     (#1591): one vocabulary, one required-member census for the element
    ///     reader, the row reader and the span parser. This method keeps its
    ///     shape and its <c>JsonException</c> contract: every refusal is loud,
    ///     and refusing an unknown tag is the *only* honest answer — the union
    ///     has no case for "a part I could not read", and the "forward-compat
    ///     with future part types" the pre-#550 code claimed described a data
    ///     loss, not a compatibility. The store's own read path refuses the
    ///     same tags the same way (<see cref="JsonlLineParser" />); the two
    ///     have to agree, and the shared table is what makes refusing the same
    ///     thing mechanical rather than pinned by hope.
    /// </remarks>
    /// <exception cref="JsonException">
    ///     The tag is unknown to this build, or the part is missing a member its tag
    ///     requires. Both name the part.
    /// </exception>
    public static ContentPart DeserializePart(JsonElement element)
    {
        if (!MessagePartAdapter.TryScanPartFields(element, legacyCasing: false, out var scan))
        {
            throw new JsonException(MessagePartAdapter.FormatRefusal(
                new MessagePartAdapter.PartRefusal(
                    MessagePartAdapter.PartRefusalKind.NoDiscriminator,
                    MessagePartAdapter.PartTagKind.Unknown,
                    null,
                    null),
                MessagePartAdapter.PartTransport.JsonlElement));
        }

        if (!MessagePartAdapter.TryBuildContentPart(
                in scan,
                MessagePartAdapter.ClassifyTag(scan.Tag),
                MessagePartAdapter.ArgsPolicy.TolerateMissing,
                MessagePartAdapter.SizePolicy.RequirePresent,
                out ContentPart? part,
                out MessagePartAdapter.PartRefusal refusal))
        {
            throw new JsonException(
                MessagePartAdapter.FormatRefusal(refusal, MessagePartAdapter.PartTransport.JsonlElement));
        }

        return part;
    }
}
