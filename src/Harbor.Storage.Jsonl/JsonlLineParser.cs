using System.Text.Json;
using Harbor.Abstractions.Models;

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
///     <para>
///         <b>#550:</b> one thing deliberately no longer mirrors the old
///         parser. A <c>parts</c> entry this build cannot rebuild used to yield
///         <see langword="null" /> and be skipped, so a session written by a
///         newer Harbor reloaded here with the unknown part missing and nothing
///         logged. It is refused by name now; see <see cref="BuildContentPart"/>
///         for why there is no third option.
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

    /// <summary>Envelope fields scanned from a message line's top-level object.</summary>
    /// <remarks>
    ///     Ref struct: <c>RoleSpan</c>/<c>PayloadSpan</c> borrow from the line
    ///     buffer — the split adds zero heap allocations to the read hot path.
    /// </remarks>
    private ref struct LineEnvelope
    {
        public string? Id;
        public DateTimeOffset CreatedAt;
        public string? ParentId;
        public MessageRole Role;
        public bool HasRole;
        public bool IsMessage;
        public ReadOnlySpan<byte> RoleSpan;
        public ReadOnlySpan<byte> PayloadSpan;
        public bool HasPayload;
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

            var envelope = new LineEnvelope();
            ScanEnvelope(ref reader, line, ref envelope);
            return BuildFromEnvelope(ref envelope, sessionId);
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>($"Line parse failed: {ex.Message}");
        }
    }

    /// <summary>Scans the top-level envelope object into <paramref name="envelope" />.</summary>
    private static void ScanEnvelope(ref Utf8JsonReader reader, ReadOnlySpan<byte> line, ref LineEnvelope envelope)
    {
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
                    envelope.IsMessage = reader.ValueSpan.SequenceEqual("message"u8);
                    break;
                case LineProperty.Id:
                    envelope.Id = reader.GetString();
                    break;
                case LineProperty.CreatedAt:
                    envelope.CreatedAt = reader.GetDateTimeOffset();
                    break;
                case LineProperty.ParentId:
                    if (reader.TokenType == JsonTokenType.String)
                        envelope.ParentId = reader.GetString();
                    break;
                case LineProperty.Role:
                    envelope.HasRole = reader.TokenType == JsonTokenType.String;
                    envelope.RoleSpan = envelope.HasRole ? reader.ValueSpan : default;
                    envelope.Role = envelope.HasRole ? MatchRole(reader.ValueSpan) : MessageRole.Unknown;
                    break;
                case LineProperty.Payload:
                    int payloadStart = (int)reader.TokenStartIndex;
                    reader.Skip();
                    envelope.PayloadSpan = line.Slice(payloadStart, (int)(reader.BytesConsumed - payloadStart));
                    envelope.HasPayload = true;
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
    }

    /// <summary>Validates the scanned envelope and dispatches to the role payload parser.</summary>
    private static Result<AgentMessage> BuildFromEnvelope(ref LineEnvelope envelope, string sessionId)
    {
        if (!envelope.IsMessage)
            return Result.Failure<AgentMessage>("Not a message line");

        string? id = envelope.Id;
        if (id is null)
            return Result.Failure<AgentMessage>("missing 'id'");

        if (!envelope.HasRole)
            return Result.Failure<AgentMessage>($"message {id}: missing 'role'");

        if (envelope.Role == MessageRole.Unknown)
            return Result.Failure<AgentMessage>(
                $"message {id}: unknown role '{System.Text.Encoding.UTF8.GetString(envelope.RoleSpan)}'");

        if (!envelope.HasPayload)
            return Result.Failure<AgentMessage>($"message {id}: missing 'payload'");

        ReadOnlySpan<byte> payload = envelope.PayloadSpan;
        DateTimeOffset createdAt = envelope.CreatedAt;
        string? parentId = envelope.ParentId;

        return envelope.Role switch
        {
            MessageRole.User => ParseUserPayload(payload, id, sessionId, createdAt, parentId),
            MessageRole.Assistant => ParseAssistantPayload(payload, id, sessionId, createdAt, parentId),
            MessageRole.ToolResult => ParseToolResultPayload(payload, id, sessionId, createdAt, parentId),
            _ => Result.Failure<AgentMessage>($"message {id}: unknown role")
        };
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
            List<ImageAttachment>? attachments = null;

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

                switch (MatchUserProperty(prop))
                {
                    case UserProperty.Content:
                        content = reader.GetString();
                        break;
                    case UserProperty.Agent:
                        agent = reader.GetString();
                        break;
                    case UserProperty.Model:
                        model = reader.GetString();
                        break;
                    case UserProperty.Attachments:
                        attachments = ParseAttachments(ref reader);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (content is null || agent is null || model is null)
                return Result.Failure<AgentMessage>($"user message {id}: missing content/agent/model");

            return Result.Success<AgentMessage>(
                new UserMessage(
                    id, sessionId, createdAt, content, agent, model, parentId,
                    attachments is { Count: > 0 } ? attachments : null));
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
                        parts = ParseParts(ref reader);
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

                    Result<ToolResultEntry> entry = ParseSingleResultEntry(ref reader, id);
                    if (entry.IsFailure)
                        return entry.ConvertFailure<AgentMessage>();

                    results.Add(entry.Value);
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

    /// <summary>Parses one <c>results</c> array entry; the reader must sit on its StartObject.</summary>
    private static Result<ToolResultEntry> ParseSingleResultEntry(ref Utf8JsonReader reader, string messageId)
    {
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
            return Result.Failure<ToolResultEntry>($"tool_result message {messageId}: malformed result entry");

        return Result.Success<ToolResultEntry>(new ToolResultEntry(tcId, tn, output, isError));
    }

    /// <summary>Parses the <c>attachments</c> array of a user payload (#386).</summary>
    private static List<ImageAttachment> ParseAttachments(ref Utf8JsonReader reader)
    {
        var images = new List<ImageAttachment>();

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return images;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                continue;

            ImageAttachment? image = ParseSingleAttachment(ref reader);
            // A malformed entry is skipped, never fatal: one broken attachment
            // must not make the whole user turn unreadable.
            if (image is not null)
                images.Add(image);
        }

        return images;
    }

    /// <summary>Parses one <c>attachments</c> entry; the reader sits on its StartObject.</summary>
    private static ImageAttachment? ParseSingleAttachment(ref Utf8JsonReader reader)
    {
        string? path = null;
        string? mimeType = null;
        int width = 0;
        int height = 0;
        byte[]? data = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            ReadOnlySpan<byte> aProp = reader.ValueSpan;
            reader.Read();

            switch (MatchAttachmentProperty(aProp))
            {
                case AttachmentProperty.Path:
                    path = reader.GetString();
                    break;
                case AttachmentProperty.MimeType:
                    mimeType = reader.GetString();
                    break;
                case AttachmentProperty.Width:
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int w))
                        width = w;
                    break;
                case AttachmentProperty.Height:
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int h))
                        height = h;
                    break;
                case AttachmentProperty.Data:
                    if (reader.TokenType == JsonTokenType.String)
                        data = reader.GetBytesFromBase64();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (path is null || mimeType is null || data is null)
            return null;

        return new ImageAttachment(path, mimeType, width, height, data);
    }

    /// <summary>Parses the <c>parts</c> array of an assistant payload inline.</summary>
    private static List<ContentPart> ParseParts(ref Utf8JsonReader reader)
    {
        var parts = new List<ContentPart>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                continue;

            // #550: there is no null to skip any more. A part that cannot be rebuilt
            // names its tag and takes the record with it (see BuildContentPart) —
            // the previous `if (part is not null)` is exactly the line that let a
            // part disappear from a reloaded transcript without a word.
            parts.Add(ParseSinglePart(ref reader));
        }

        return parts;
    }

    /// <summary>Parses one object of the <c>parts</c> array, or throws naming what is wrong with it.</summary>
    private static ContentPart ParseSinglePart(ref Utf8JsonReader reader)
    {
        PartType partType = PartType.Unknown;
        ReadOnlySpan<byte> tag = default;
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
                    // Borrowed, not copied: the tag is only ever turned into a string
                    // on the throw path, so the success path allocates nothing more
                    // than it did before (#460's read gate measures exactly this).
                    tag = reader.ValueSpan;
                    partType = MatchPartType(tag);
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

        return BuildContentPart(partType, tag, text, partId, toolName, args, hasArgs, path, mimeType, sizeBytes);
    }

    /// <summary>Builds a <see cref="ContentPart" /> from scanned fields, or refuses the record by name.</summary>
    /// <remarks>
    ///     #550. This is a tag → type factory, not a walk over the part union, so
    ///     <see cref="ContentPartVisitor{TResult}" /> never reached it and every arm
    ///     used to fall into <c>_ => null</c>, which the caller skipped: a session
    ///     written by a NEWER Harbor reloaded in this one with the unknown part
    ///     missing and nothing anywhere saying why. Both refusals are loud and both
    ///     name the part, because the reader has no honest third option — there is no
    ///     union case that stands for "a part I could not read", and inventing one
    ///     would be the same loss with a nicer shape.
    ///     <list type="bullet">
    ///         <item>An unknown tag is version skew: this build is older than the
    ///         file, and the operator is the only one who can resolve that.</item>
    ///         <item>A known tag missing a field is a corrupt record: the line
    ///         claims a part it does not contain.</item>
    ///     </list>
    ///     The throw rides the store's existing per-record failure channel —
    ///     <see cref="ParseAssistantPayload" /> catches it, names the message, and
    ///     the reader logs it while still returning the rest of the transcript.
    /// </remarks>
    private static ContentPart BuildContentPart(
        PartType partType,
        ReadOnlySpan<byte> tag,
        string? text,
        string? partId,
        string? toolName,
        JsonElement args,
        bool hasArgs,
        string? path,
        string? mimeType,
        long sizeBytes)
    {
        return partType switch
        {
            PartType.Text when text is not null => new TextPart(text),
            PartType.Thinking when text is not null => new ThinkingPart(text),
            PartType.ToolCall when partId is not null && toolName is not null && hasArgs
                => new ToolCallPart(partId, toolName, args),
            PartType.File when path is not null && mimeType is not null => new FilePart(path, mimeType, sizeBytes),

            PartType.Text => throw new JsonException("content part of type 'text' is missing 'text'"),
            PartType.Thinking => throw new JsonException("content part of type 'thinking' is missing 'text'"),
            PartType.ToolCall => throw new JsonException(
                "content part of type 'tool_call' is missing 'id', 'toolName' or 'args'"),
            PartType.File => throw new JsonException(
                "content part of type 'file' is missing 'path', 'mimeType' or 'sizeBytes'"),

            // The foreign-tag refusal is the catch-all rather than one arm per enum
            // member. An enum with an explicit underlying type carries unnamed values,
            // and a set of named arms does NOT make the switch expression exhaustive
            // (CS8524) — a named `PartType.Unknown` arm would not have covered them.
            // Every unnamed value is the same refusal anyway: a tag this build has no
            // row for.
            _ => throw new JsonException(
                $"content part type '{TagName(tag)}' is not known to this build — this session was written by a newer Harbor, and the record is refused rather than read with the part missing"),
        };
    }

    /// <summary>
    ///     The offending tag, spelled out for a diagnostic. Only ever called from a
    ///     <c>throw</c> expression, so the one string it allocates cannot reach the
    ///     read hot path.
    /// </summary>
    private static string TagName(ReadOnlySpan<byte> tag) =>
        tag.IsEmpty ? "<no 'type' discriminator>" : System.Text.Encoding.UTF8.GetString(tag);

    // ── Property matchers (zero-alloc, UTF-8 span compare) ─────────────────

    private enum LineProperty : byte { Other, Type, Id, CreatedAt, ParentId, Role, Payload }
    private enum UserProperty : byte { Other, Content, Agent, Model, Attachments }
    private enum AttachmentProperty : byte { Other, Path, MimeType, Width, Height, Data }
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
        var x when x.SequenceEqual("attachments"u8) => UserProperty.Attachments,
        _ => UserProperty.Other
    };

    private static AttachmentProperty MatchAttachmentProperty(ReadOnlySpan<byte> p) => p switch
    {
        var x when x.SequenceEqual("path"u8) => AttachmentProperty.Path,
        var x when x.SequenceEqual("mimeType"u8) => AttachmentProperty.MimeType,
        var x when x.SequenceEqual("width"u8) => AttachmentProperty.Width,
        var x when x.SequenceEqual("height"u8) => AttachmentProperty.Height,
        var x when x.SequenceEqual("data"u8) => AttachmentProperty.Data,
        _ => AttachmentProperty.Other
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
    ///     Span fast path over the single <see cref="StopReasonTable" /> (#197);
    ///     unknown values fall back to the converter via a one-off string
    ///     (error path only).
    /// </summary>
    private static StopReason ParseStopReason(ReadOnlySpan<byte> p)
    {
        if (StopReasonTable.TryParseSpan(p, out var reason))
            return reason;

        return StopReasonJsonConverter.Parse(System.Text.Encoding.UTF8.GetString(p));
    }
}
