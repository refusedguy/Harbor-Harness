using System.Text.Json;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     Zero-intermediate-allocation JSONL line parser for the session read
///     path (perf sprint, IPC-005 / PERF-005 successor).
/// </summary>
/// <remarks>
///     <para>
///         <b>Design:</b> operates on raw UTF-8 line bytes (no per-line
///         <see cref="string" />, no <c>Encoding.UTF8.GetBytes</c>). Property
///         names are matched against compile-time UTF-8 literals via
///         <see cref="Utf8JsonReader.ValueSpan" /> — never materialized as
///         strings. The role-specific payload is NOT round-tripped through
///         <see cref="JsonElement" /> / <c>GetRawText()</c> / re-encode: its
///         raw span is captured from the line buffer via token indexes and
///         parsed by a second reader over the same memory. Allocations are
///         limited to the strings that end up inside the returned message
///         object graph (plus a <see cref="JsonElement"/> for tool-call args,
///         which is part of the model).
///     </para>
///     <para>
///         <b>Fidelity:</b> semantics mirror the previous
///         <c>ParseMessageLine</c> — same required-field checks, same error
///         messages, same <see cref="StopReason"/> normalization (the span
///         fast path covers every value this store writes; anything else
///         falls back to <see cref="StopReasonJsonConverter.Parse"/>).
///     </para>
/// </remarks>
internal static class JsonlLineParser
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>Parsed role of a message line.</summary>
    private enum MessageRole : byte
    {
        Unknown,
        User,
        Assistant,
        ToolResult
    }

    /// <summary>
    ///     Parse one JSONL line (raw UTF-8) into an <see cref="AgentMessage" />.
    ///     Returns <see cref="Result{T}" /> with a diagnostic message on
    ///     malformed input so the caller can log + skip (§ROP-001).
    /// </summary>
    public static Result<AgentMessage> Parse(ReadOnlySpan<byte> line, string sessionId)
    {
        try
        {
            var reader = new Utf8JsonReader(line, ReaderOptions);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return Result.Failure<AgentMessage>("JSON does not start with an object");

            string? id = null;
            DateTimeOffset createdAt = default;
            string? parentId = null;
            MessageRole role = MessageRole.Unknown;
            bool hasRole = false;
            bool isMessage = false;
            ReadOnlySpan<byte> roleSpan = default;
            ReadOnlySpan<byte> payloadSpan = default;
            bool hasPayload = false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                ReadOnlySpan<byte> prop = reader.ValueSpan;
                reader.Read();

                switch (MatchLineProperty(prop))
                {
                    case LineProperty.Type:
                        isMessage = reader.ValueSpan.SequenceEqual("message"u8);
                        break;
                    case LineProperty.Id:
                        id = reader.GetString();
                        break;
                    case LineProperty.CreatedAt:
                        createdAt = reader.GetDateTimeOffset();
                        break;
                    case LineProperty.ParentId:
                        if (reader.TokenType == JsonTokenType.String)
                            parentId = reader.GetString();
                        break;
                    case LineProperty.Role:
                        hasRole = reader.TokenType == JsonTokenType.String;
                        roleSpan = hasRole ? reader.ValueSpan : default;
                        role = hasRole ? MatchRole(reader.ValueSpan) : MessageRole.Unknown;
                        break;
                    case LineProperty.Payload:
                        int payloadStart = (int)reader.TokenStartIndex;
                        reader.Skip();
                        payloadSpan = line.Slice(payloadStart, (int)(reader.BytesConsumed - payloadStart));
                        hasPayload = true;
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (!isMessage)
                return Result.Failure<AgentMessage>("Not a message line");

            if (id is null)
                return Result.Failure<AgentMessage>("missing 'id'");

            if (!hasRole)
                return Result.Failure<AgentMessage>($"message {id}: missing 'role'");

            if (role == MessageRole.Unknown)
                return Result.Failure<AgentMessage>(
                    $"message {id}: unknown role '{System.Text.Encoding.UTF8.GetString(roleSpan)}'");

            if (!hasPayload)
                return Result.Failure<AgentMessage>($"message {id}: missing 'payload'");

            return role switch
            {
                MessageRole.User => ParseUserPayload(payloadSpan, id, sessionId, createdAt, parentId),
                MessageRole.Assistant => ParseAssistantPayload(payloadSpan, id, sessionId, createdAt, parentId),
                MessageRole.ToolResult => ParseToolResultPayload(payloadSpan, id, sessionId, createdAt, parentId),
                _ => Result.Failure<AgentMessage>($"message {id}: unknown role")
            };
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>($"Line parse failed: {ex.Message}");
        }
    }

    // ── Role payloads ──────────────────────────────────────────────────────

    private static Result<AgentMessage> ParseUserPayload(
        ReadOnlySpan<byte> payload, string id, string sessionId, DateTimeOffset createdAt, string? parentId)
    {
        try
        {
            string? content = null;
            string? agent = null;
            string? model = null;

            var reader = new Utf8JsonReader(payload, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return Result.Failure<AgentMessage>($"user message {id}: payload is not an object");

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                ReadOnlySpan<byte> prop = reader.ValueSpan;
                reader.Read();

                if (MatchUserProperty(prop) == UserProperty.Content)
                    content = reader.GetString();
                else if (MatchUserProperty(prop) == UserProperty.Agent)
                    agent = reader.GetString();
                else if (MatchUserProperty(prop) == UserProperty.Model)
                    model = reader.GetString();
            }

            if (content is null || agent is null || model is null)
                return Result.Failure<AgentMessage>($"user message {id}: missing content/agent/model");

            return Result.Success<AgentMessage>(
                new UserMessage(id, sessionId, createdAt, content, agent, model, parentId));
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>($"user message {id}: {ex.Message}");
        }
    }

    private static Result<AgentMessage> ParseAssistantPayload(
        ReadOnlySpan<byte> payload, string id, string sessionId, DateTimeOffset createdAt, string? parentId)
    {
        try
        {
            List<ContentPart>? parts = null;
            StopReason? stopReason = null;
            Usage? usage = null;
            string? model = null;
            bool isSummary = false;
            string? summaryFirstKeptId = null;

            var reader = new Utf8JsonReader(payload, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return Result.Failure<AgentMessage>($"assistant message {id}: payload is not an object");

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                ReadOnlySpan<byte> prop = reader.ValueSpan;
                reader.Read();

                switch (MatchAssistantProperty(prop))
                {
                    case AssistantProperty.Parts:
                        parts = ParseParts(ref reader, id);
                        break;
                    case AssistantProperty.StopReason:
                        stopReason = ParseStopReason(reader.ValueSpan);
                        break;
                    case AssistantProperty.Usage:
                        usage = JsonSerializer.Deserialize(ref reader, JsonlCodecContext.Default.Usage)
                            ?? new Usage(0, 0);
                        break;
                    case AssistantProperty.Model:
                        model = reader.GetString();
                        break;
                    case AssistantProperty.IsSummary:
                        isSummary = reader.GetBoolean();
                        break;
                    case AssistantProperty.SummaryFirstKeptId:
                        summaryFirstKeptId = reader.GetString();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (parts is null)
                return Result.Failure<AgentMessage>($"assistant message {id}: missing 'parts'");
            if (stopReason is null)
                return Result.Failure<AgentMessage>($"assistant message {id}: missing 'stopReason'");
            if (usage is null)
                return Result.Failure<AgentMessage>($"assistant message {id}: missing 'usage'");
            if (model is null)
                return Result.Failure<AgentMessage>($"assistant message {id}: missing 'model'");

            return Result.Success<AgentMessage>(new AssistantMessage(
                id, sessionId, createdAt, parts, stopReason.Value, usage, model, parentId, isSummary, summaryFirstKeptId));
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>($"assistant message {id}: {ex.Message}");
        }
    }

    private static Result<AgentMessage> ParseToolResultPayload(
        ReadOnlySpan<byte> payload, string id, string sessionId, DateTimeOffset createdAt, string? parentId)
    {
        try
        {
            List<ToolResultEntry>? results = null;

            var reader = new Utf8JsonReader(payload, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return Result.Failure<AgentMessage>($"tool_result message {id}: payload is not an object");

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                ReadOnlySpan<byte> prop = reader.ValueSpan;
                reader.Read();

                if (MatchToolResultProperty(prop) != ToolResultProperty.Results
                    || reader.TokenType != JsonTokenType.StartArray)
                {
                    reader.Skip();
                    continue;
                }

                results = [];
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                        continue;

                    string? tcId = null;
                    string? tn = null;
                    string? output = null;
                    bool isError = false;

                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName)
                            continue;

                        ReadOnlySpan<byte> rProp = reader.ValueSpan;
                        reader.Read();

                        switch (MatchResultEntryProperty(rProp))
                        {
                            case ResultEntryProperty.ToolCallId:
                                tcId = reader.GetString();
                                break;
                            case ResultEntryProperty.ToolName:
                                tn = reader.GetString();
                                break;
                            case ResultEntryProperty.Output:
                                output = reader.GetString();
                                break;
                            case ResultEntryProperty.IsError:
                                isError = reader.GetBoolean();
                                break;
                            default:
                                reader.Skip();
                                break;
                        }
                    }

                    if (tcId is null || tn is null || output is null)
                        return Result.Failure<AgentMessage>($"tool_result message {id}: malformed result entry");

                    results.Add(new ToolResultEntry(tcId, tn, output, isError));
                }
            }

            if (results is null)
                return Result.Failure<AgentMessage>($"tool_result message {id}: missing 'results'");

            return Result.Success<AgentMessage>(
                new ToolResultMessage(id, sessionId, createdAt, results, parentId));
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>($"tool_result message {id}: {ex.Message}");
        }
    }

    /// <summary>Parses the <c>parts</c> array of an assistant payload inline.</summary>
    private static List<ContentPart> ParseParts(ref Utf8JsonReader reader, string messageId)
    {
        var parts = new List<ContentPart>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                continue;

            PartType partType = PartType.Unknown;
            string? text = null;
            string? partId = null;
            string? toolName = null;
            JsonElement args = default;
            bool hasArgs = false;
            string? path = null;
            string? mimeType = null;
            long sizeBytes = 0;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                ReadOnlySpan<byte> pProp = reader.ValueSpan;
                reader.Read();

                switch (MatchPartProperty(pProp))
                {
                    case PartProperty.Type:
                        partType = MatchPartType(reader.ValueSpan);
                        break;
                    case PartProperty.Text:
                        text = reader.GetString();
                        break;
                    case PartProperty.Id:
                        partId = reader.GetString();
                        break;
                    case PartProperty.ToolName:
                        toolName = reader.GetString();
                        break;
                    case PartProperty.Args:
                        args = JsonSerializer.Deserialize(ref reader, JsonlCodecContext.Default.JsonElement);
                        hasArgs = true;
                        break;
                    case PartProperty.Path:
                        path = reader.GetString();
                        break;
                    case PartProperty.MimeType:
                        mimeType = reader.GetString();
                        break;
                    case PartProperty.SizeBytes:
                        sizeBytes = reader.GetInt64();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            ContentPart? part = partType switch
            {
                PartType.Text when text is not null => new TextPart(text),
                PartType.Thinking when text is not null => new ThinkingPart(text),
                PartType.ToolCall when partId is not null && toolName is not null && hasArgs
                    => new ToolCallPart(partId, toolName, args),
                PartType.File when path is not null && mimeType is not null => new FilePart(path, mimeType, sizeBytes),
                _ => null
            };

            if (part is not null)
                parts.Add(part);
        }

        return parts;
    }

    // ── Property matchers (zero-alloc, UTF-8 span compare) ─────────────────

    private enum LineProperty : byte { Other, Type, Id, CreatedAt, ParentId, Role, Payload }
    private enum UserProperty : byte { Other, Content, Agent, Model }
    private enum AssistantProperty : byte { Other, Parts, StopReason, Usage, Model, IsSummary, SummaryFirstKeptId }
    private enum ToolResultProperty : byte { Other, Results }
    private enum ResultEntryProperty : byte { Other, ToolCallId, ToolName, Output, IsError }
    private enum PartProperty : byte { Other, Type, Text, Id, ToolName, Args, Path, MimeType, SizeBytes }
    private enum PartType : byte { Unknown, Text, Thinking, ToolCall, File }

    private static LineProperty MatchLineProperty(ReadOnlySpan<byte> p) => p switch
    {
        var x when x.SequenceEqual("type"u8) => LineProperty.Type,
        var x when x.SequenceEqual("id"u8) => LineProperty.Id,
        var x when x.SequenceEqual("createdAt"u8) => LineProperty.CreatedAt,
        var x when x.SequenceEqual("parentId"u8) => LineProperty.ParentId,
        var x when x.SequenceEqual("role"u8) => LineProperty.Role,
        var x when x.SequenceEqual("payload"u8) => LineProperty.Payload,
        _ => LineProperty.Other
    };

    private static UserProperty MatchUserProperty(ReadOnlySpan<byte> p) => p switch
    {
        var x when x.SequenceEqual("content"u8) => UserProperty.Content,
        var x when x.SequenceEqual("agent"u8) => UserProperty.Agent,
        var x when x.SequenceEqual("model"u8) => UserProperty.Model,
        _ => UserProperty.Other
    };

    private static AssistantProperty MatchAssistantProperty(ReadOnlySpan<byte> p) => p switch
    {
        var x when x.SequenceEqual("parts"u8) => AssistantProperty.Parts,
        var x when x.SequenceEqual("stopReason"u8) => AssistantProperty.StopReason,
        var x when x.SequenceEqual("usage"u8) => AssistantProperty.Usage,
        var x when x.SequenceEqual("model"u8) => AssistantProperty.Model,
        var x when x.SequenceEqual("isSummary"u8) => AssistantProperty.IsSummary,
        var x when x.SequenceEqual("summaryFirstKeptId"u8) => AssistantProperty.SummaryFirstKeptId,
        _ => AssistantProperty.Other
    };

    private static ToolResultProperty MatchToolResultProperty(ReadOnlySpan<byte> p) => p switch
    {
        var x when x.SequenceEqual("results"u8) => ToolResultProperty.Results,
        _ => ToolResultProperty.Other
    };

    private static ResultEntryProperty MatchResultEntryProperty(ReadOnlySpan<byte> p) => p switch
    {
        var x when x.SequenceEqual("toolCallId"u8) => ResultEntryProperty.ToolCallId,
        var x when x.SequenceEqual("toolName"u8) => ResultEntryProperty.ToolName,
        var x when x.SequenceEqual("output"u8) => ResultEntryProperty.Output,
        var x when x.SequenceEqual("isError"u8) => ResultEntryProperty.IsError,
        _ => ResultEntryProperty.Other
    };

    private static PartProperty MatchPartProperty(ReadOnlySpan<byte> p) => p switch
    {
        var x when x.SequenceEqual("type"u8) => PartProperty.Type,
        var x when x.SequenceEqual("text"u8) => PartProperty.Text,
        var x when x.SequenceEqual("id"u8) => PartProperty.Id,
        var x when x.SequenceEqual("toolName"u8) => PartProperty.ToolName,
        var x when x.SequenceEqual("args"u8) => PartProperty.Args,
        var x when x.SequenceEqual("path"u8) => PartProperty.Path,
        var x when x.SequenceEqual("mimeType"u8) => PartProperty.MimeType,
        var x when x.SequenceEqual("sizeBytes"u8) => PartProperty.SizeBytes,
        _ => PartProperty.Other
    };

    private static PartType MatchPartType(ReadOnlySpan<byte> p) => p switch
    {
        var x when x.SequenceEqual("text"u8) => PartType.Text,
        var x when x.SequenceEqual("thinking"u8) => PartType.Thinking,
        var x when x.SequenceEqual("tool_call"u8) => PartType.ToolCall,
        var x when x.SequenceEqual("file"u8) => PartType.File,
        _ => PartType.Unknown
    };

    private static MessageRole MatchRole(ReadOnlySpan<byte> p) => p switch
    {
        var x when x.SequenceEqual("user"u8) => MessageRole.User,
        var x when x.SequenceEqual("assistant"u8) => MessageRole.Assistant,
        var x when x.SequenceEqual("tool_result"u8) => MessageRole.ToolResult,
        _ => MessageRole.Unknown
    };

    /// <summary>
    ///     Span fast path covering every casing/variant
    ///     <see cref="StopReasonJsonConverter.Parse"/> handles; unknown values
    ///     fall back to the converter via a one-off string (error path only).
    /// </summary>
    private static StopReason ParseStopReason(ReadOnlySpan<byte> p)
    {
        if (p.SequenceEqual("stop"u8) || p.SequenceEqual("end_turn"u8) || p.SequenceEqual("finish"u8))
            return StopReason.Stop;
        if (p.SequenceEqual("length"u8) || p.SequenceEqual("max_tokens"u8) || p.SequenceEqual("max_tokens_length"u8))
            return StopReason.Length;
        if (p.SequenceEqual("tool_use"u8) || p.SequenceEqual("tool_calls"u8)
            || p.SequenceEqual("function_call"u8) || p.SequenceEqual("tooluse"u8))
            return StopReason.ToolUse;
        if (p.SequenceEqual("content_filter"u8) || p.SequenceEqual("content_filtering"u8)
            || p.SequenceEqual("contentfilter"u8))
            return StopReason.ContentFilter;
        if (p.SequenceEqual("error"u8) || p.SequenceEqual("failed"u8))
            return StopReason.Error;
        if (p.SequenceEqual("aborted"u8) || p.SequenceEqual("abort"u8) || p.SequenceEqual("cancelled"u8))
            return StopReason.Aborted;

        return StopReasonJsonConverter.Parse(System.Text.Encoding.UTF8.GetString(p));
    }
}
