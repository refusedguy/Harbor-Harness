using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Providers.Internal;
using Microsoft.Extensions.Logging;
namespace Harbor.Providers.Ollama;
/// <summary>
///     Native Ollama provider — local LLM inference.
///     Implements Strategy pattern (GOF) via ILlmClient.
///     Differences from OpenAI-compat:
///     - NDJSON (one JSON per line) instead of SSE
///     - /api/chat endpoint (not /v1/chat/completions)
///     - role: "model" instead of "assistant" (sometimes)
///     - tools field format is slightly different
///     - No API key required
///     - keep_alive parameter for model persistence
/// </summary>
public sealed class OllamaLlmClient : ProviderLlmClientBase, ILlmClient
{
    private const string DefaultBaseUrl = "http://localhost:11434";

    // #3404: legacy calls share the stream's index→id map with native ones,
    // but live in a disjoint key range so a wire `index` can never alias them.
    private const int LegacyCallIndexBase = 1_000_000;

    // Stateless and thread-safe — every field of the parser is a local. One
    // instance keeps the recovery path allocation-free on plain text chunks.
    private static readonly LegacyToolCallParser LegacyParser = new();

    // Pre-computed base URL — avoids per-request string manipulation.
    private readonly string _baseUrl;
    private readonly OllamaConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<OllamaLlmClient> _logger;

    // Keyless by design: the local daemon needs no API key. The base therefore
    // gets no IAuthResolver and skips auth resolution (keyless constructor).
    public OllamaLlmClient(HttpClient http, OllamaConfig config, ILogger<OllamaLlmClient> logger)
        : base(http, logger, ProviderId.Create("ollama"))
    {
        _http = http;
        _config = config;
        _logger = logger;
        _baseUrl = string.IsNullOrEmpty(_config.BaseUrl) ? DefaultBaseUrl : _config.BaseUrl.TrimEnd('/');
    }

    // --- Template Method variation points (#1499, extended #3767) ------------

    internal override Channel<LlmEvent> CreateChannel() =>
        Channel.CreateUnbounded<LlmEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    // Ollama speaks NDJSON: the whole line is the payload, EOF ends the stream.
    protected override bool RawLineMode => true;

    protected override string ApiErrorLabel => "Ollama";

    // Native clients historically scheduled the pump with the caller token.
    protected override bool SchedulePumpWithToken => true;

    internal override HttpRequestMessage BuildRequest(LlmRequest request, string apiKey) => BuildRequest(request);

    internal override Task WriteEventsAsync(
        string data,
        ChannelWriter<LlmEvent> writer,
        ChunkStreamState chunkState,
        LlmRequest request,
        Activity? activity,
        CancellationToken ct) =>
        WriteNdjsonEventsAsync(data, writer, chunkState, ct);

    // #3767: the local-daemon hint keeps the old send-failure message intact.
    protected override Func<Exception, CancellationToken, ErrorEvent>? SendFailureHook(Activity? activity, CancellationToken ct) =>
        (ex, token) => ex is HttpRequestException hre
            ? new ErrorEvent(
                $"Cannot connect to Ollama at {_config.BaseUrl ?? DefaultBaseUrl}. " +
                $"Is `ollama serve` running? Error: {hre.Message}",
                Kind: ProviderErrorKind.Network)
            : ProviderErrors.HttpSendFailure(ex, token);

    internal override void OnStreamComplete(ChunkStreamState chunkState, Activity? activity)
    {
        // #203: stream-health summary (only when noteworthy).
        if (chunkState.MalformedChunks > 0 || chunkState.RemappedToolCalls > 0)
        {
            OllamaLlmClientLog.StreamCompleted(_logger, chunkState.MalformedChunks, chunkState.RemappedToolCalls);
        }
    }

    protected override void OnFailed(Exception ex, Activity? activity) =>
        // #203 E5: fire-and-forget pump task must log inside the hook —
        // the ErrorEvent alone is invisible in traces.
        OllamaLlmClientLog.StreamTaskFailed(_logger, ex, ex.Message);

    public override Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) =>
        // ROP-A ПР.9: network and parsing are separate failure modes with
        // separate hints — "is the daemon up?" vs "response was not JSON".
        Result.Try(
                () => _http.GetStringAsync(string.Concat(_baseUrl, "/api/tags"), cancellationToken),
                ex => $"Cannot connect to Ollama at {_baseUrl}. Is `ollama serve` running? {ex.Message}")
            .Bind(json => Result.Try(
                () => ParseModels(json),
                ex => $"Ollama /api/tags returned invalid JSON: {ex.Message}"));

    /// <summary>Pure projection of an /api/tags payload onto ModelInfo (throws on malformed JSON).</summary>
    private static IReadOnlyList<ModelInfo> ParseModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Maybe<JsonElement> models = doc.RootElement.TryGetProperty("models", out var arr)
            && arr.ValueKind == JsonValueKind.Array
            ? Maybe.From(arr)
            : Maybe<JsonElement>.None;
        return models.Match(
            a => a.EnumerateArray()
                .Select(m => Maybe.From(m.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Map(id => new ModelInfo(id!, "ollama", id!, Ctx(m), Ctx(m), false, false, true, Pricing.Unknown, "openai")))
                .Choose()
                .ToList(),
            () => new List<ModelInfo>());

        static int Ctx(JsonElement m) =>
            m.TryGetProperty("model_info", out var info)
            && info.TryGetProperty("context_length", out var ctxEl)
            && ctxEl.ValueKind == JsonValueKind.Number
                ? ctxEl.GetInt32()
                : 4096;
    }

    /// <summary>
    ///     Build the <c>/api/chat</c> request message.
    /// </summary>
    /// <remarks>
    ///     §PERF-002 / #475: the body is written to a <see cref="Utf8JsonWriter" /> and
    ///     pinned by <c>ProviderPayloadWireTests</c>, so the request shape is assertable
    ///     without a live server. <c>internal</c> rather than <c>private</c> because that
    ///     test suite calls it directly; the provider project already declares
    ///     <c>InternalsVisibleTo("Harbor.Providers.Tests")</c>.
    /// </remarks>
    internal HttpRequestMessage BuildRequest(LlmRequest request)
    {
        string url = string.Concat(_baseUrl, "/api/chat");

        var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(WritePayload(request, _logger))
        };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return msg;
    }

    /// <summary>
    ///     Renders the whole <c>/api/chat</c> body as UTF-8 JSON.
    /// </summary>
    /// <remarks>
    ///     §PERF-002 / #475 — written straight to a <see cref="Utf8JsonWriter" />.
    ///     This used to be a <c>Dictionary&lt;string, object?&gt;</c> of anonymous
    ///     types handed to <c>JsonSerializer.SerializeToUtf8Bytes</c>, which resolves
    ///     a <c>JsonTypeInfo</c> for the runtime type of every value: reflection, and
    ///     a hard failure under a trimmed / NativeAOT publish, where
    ///     reflection-based serialization is disabled by default.
    /// </remarks>
    private byte[] WritePayload(LlmRequest request, ILogger logger)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            writer.WriteString("model", request.Model);
            WriteMessages(writer, request, logger);
            writer.WriteBoolean("stream", true);
            WriteOptions(writer, request);

            // keep_alive for model persistence (5 minutes by default)
            writer.WriteString("keep_alive", _config.KeepAlive);

            WriteTools(writer, request.Tools);

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    ///     The <c>options</c> object. Always emitted, empty object included — that is
    ///     how a local model is told "no overrides", and dropping the key is a
    ///     different request.
    /// </summary>
    private static void WriteOptions(Utf8JsonWriter writer, LlmRequest request)
    {
        writer.WriteStartObject("options");
        if (request.Temperature.HasValue) writer.WriteNumber("temperature", request.Temperature.Value);
        if (request.TopP.HasValue) writer.WriteNumber("top_p", request.TopP.Value);
        if (request.TopK.HasValue) writer.WriteNumber("top_k", request.TopK.Value);
        if (request.MaxOutputTokens.HasValue) writer.WriteNumber("num_predict", request.MaxOutputTokens.Value);
        writer.WriteEndObject();
    }

    private static void WriteTools(Utf8JsonWriter writer, IReadOnlyList<ToolDefinition> tools)
    {
        if (tools.Count == 0)
        {
            return;
        }

        writer.WriteStartArray("tools");
        for (int i = 0; i < tools.Count; i++)
        {
            ToolDefinition tool = tools[i];
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("parameters");
            tool.InputSchema.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteMessages(Utf8JsonWriter writer, LlmRequest request, ILogger logger)
    {
        writer.WriteStartArray("messages");

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            WriteRoleAndText(writer, "system", request.SystemPrompt);
        }

        for (int i = 0; i < request.Messages.Count; i++)
        {
            switch (request.Messages[i])
            {
                case LlmUserMessage user:
                    // ROP-A ПР.12: non-text blocks dropped loudly.
                    WriteRoleAndText(writer, "user", ProviderPayload.FirstTextOrEmpty(user.Content, logger, "ollama"));
                    break;

                case LlmAssistantMessage assistant:
                    WriteAssistant(writer, assistant, logger);
                    break;

                case LlmToolResultMessage result:
                    WriteRoleAndText(writer, "tool", result.Output);
                    break;

                default:
                    WriteRoleAndText(writer, "user", "");
                    break;
            }
        }

        writer.WriteEndArray();
    }

    /// <summary>
    ///     An assistant turn. Unlike the OpenAI builder this path writes
    ///     <c>""</c> rather than omitting <c>content</c> when the turn carries no
    ///     text: Ollama's template expects the key to be present.
    /// </summary>
    private static void WriteAssistant(Utf8JsonWriter writer, LlmAssistantMessage assistant, ILogger logger)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "assistant");
        writer.WriteString("content", ProviderPayload.FirstTextOrEmpty(assistant.Content, logger, "ollama"));

        writer.WriteStartArray("tool_calls");
        for (int i = 0; i < assistant.Content.Count; i++)
        {
            if (assistant.Content[i] is not LlmToolCallBlock toolCall)
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("id", toolCall.Id);
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", toolCall.Name);
            writer.WriteString("arguments", toolCall.Arguments.GetRawText());
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRoleAndText(Utf8JsonWriter writer, string role, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }

    /// <summary>
    ///     Parse one NDJSON line and write any emitted events directly into the channel.
    ///     Malformed lines follow the unified skip-and-count policy (ROP-A ПР.4).
    ///     #171: the payload transcodes in one pass into a pooled buffer and
    ///     parses via Utf8JsonReader — no JsonDocument per line.
    /// </summary>
    private async Task WriteNdjsonEventsAsync(string line, ChannelWriter<LlmEvent> writer, ChunkStreamState chunkState, CancellationToken ct)
    {
        List<LlmEvent> events;
        try
        {
            int remapsBefore = chunkState.RemappedToolCalls;
            // #171: single-pass transcode — GetMaxByteCount rent + one
            // GetBytes (was GetByteCount + GetBytes: two passes per line).
            byte[] rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(line.Length));
            int byteCount;
            try
            {
                byteCount = Encoding.UTF8.GetBytes(line, rented);
                events = MapNdjsonChunk(rented.AsSpan(0, byteCount), chunkState.IndexToId, chunkState);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            // #203 B8: positional id fallback, counted in the parser —
            // warn once per stream instead of staying silent.
            SsePump.WarnOnceOnRemap(chunkState, remapsBefore, _logger);
        }
        catch (Exception ex)
        {
            chunkState.CountMalformed();
            OllamaLlmClientLog.SkippingMalformedLine(_logger, ex, chunkState.MalformedChunks, line);
            return;
        }

        foreach (var evt in events)
        {
            await writer.WriteAsync(evt, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Map one Ollama NDJSON line from UTF-8 JSON. Fields buffer across
    ///     the single pass and tool calls emit at each tc EndObject, so
    ///     property order never matters. Non-string ids/names fall back
    ///     instead of failing the line (strictly looser than the DOM walk,
    ///     which threw on e.g. numeric ids); object arguments re-serialize
    ///     through a pooled writer (GetRawText parity).
    ///     #203: pass the stream <paramref name="state" /> (when available)
    ///     so positional id fallbacks are counted instead of silent.
    /// </summary>
    internal static List<LlmEvent> MapNdjsonChunk(ReadOnlySpan<byte> utf8Json, Dictionary<int, string> indexToId, ChunkStreamState? state = null)
    {
        // Bare-map callers (tests) have no stream state; wrap their map so the
        // shared emitter resolves against the same id table. The live stream
        // passes chunkState, whose IndexToId is this very map.
        ChunkStreamState emitState = state ?? ChunkStreamState.FromMap(indexToId);

        var events = new List<LlmEvent>(capacity: 2);
        var reader = new Utf8JsonReader(utf8Json, isFinalBlock: true, state: default);

        int depth = 0;
        bool sawRoot = false;
        bool inMessage = false;
        bool inToolCalls = false;
        bool inTc = false;
        bool inFunction = false;

        string? content = null;
        int tcIndex = 0;
        string? tcWireId = null;
        string? tcName = null;
        string? tcArgs = null;

        bool done = false;
        int inputTokens = 0;
        int outputTokens = 0;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    if (depth == 0)
                    {
                        // Single top-level object only (DOM parity) — skip
                        // anything else whole.
                        if (!sawRoot)
                        {
                            depth = 1;
                            sawRoot = true;
                        }
                        else
                        {
                            reader.Skip();
                        }

                        break;
                    }

                    if (depth == 1)
                    {
                        depth = 2;
                        break;
                    }

                    if (inToolCalls && depth == 3)
                    {
                        depth = 4;
                        inTc = true;
                        tcIndex = 0;
                        tcWireId = null;
                        tcName = null;
                        tcArgs = null;
                        inFunction = false;
                        break;
                    }

                    reader.Skip();
                    break;

                case JsonTokenType.StartArray:
                    if (depth <= 1)
                    {
                        depth++;
                        break;
                    }

                    reader.Skip();
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (depth == 5 && inFunction)
                    {
                        inFunction = false;
                    }
                    else if (depth == 4 && inTc)
                    {
                        ProviderToolCallEmitter.Emit(events, emitState, tcIndex, tcWireId, tcName, tcArgs);
                        inTc = false;
                    }
                    else if (depth == 3 && inToolCalls && reader.TokenType == JsonTokenType.EndArray)
                    {
                        inToolCalls = false;
                    }
                    else if (depth == 2 && inMessage)
                    {
                        inMessage = false;
                    }

                    if (depth > 0)
                        depth--;
                    break;

                case JsonTokenType.PropertyName:
                    // The name is matched against this client's own literal
                    // table, so the dispatch costs NO string: the old path
                    // allocated one per property per line, and a line is what
                    // every token of every answer arrives as (see
                    // Utf8JsonReaderExtensions.PropertyName).
                    string prop = Utf8JsonReaderExtensions.PropertyName(ref reader, MatchChunkProperty(reader.ValueSpan));
                    if (!reader.Read())
                        throw new JsonException("Truncated line: property without value.");
                    HandleValue(ref reader, depth, prop, inMessage, inTc, inFunction,
                        events,
                        ref inMessage, ref inToolCalls, ref inFunction, ref depth,
                        ref content, ref tcIndex, ref tcWireId, ref tcName, ref tcArgs,
                        ref done, ref inputTokens, ref outputTokens);
                    break;
            }
        }

        // #3404: a model with no native tool-call support encodes the call in
        // the message text ($ command / known fences). Recover it BEFORE the
        // standard text mapping so the agent sees a real ToolCall, not the
        // raw encoding. Native tool_calls win when both are present: the text
        // is then just narrative and must stay visible.
        if (!string.IsNullOrEmpty(content) && !events.Exists(static e => e is ToolCallStartEvent))
        {
            string text = content!;
            if (HasLegacyMarker(text))
            {
                var recovered = LegacyParser.Parse(text);
                if (recovered.Count > 0)
                {
                    EmitLegacyCalls(events, indexToId, recovered);
                    content = null; // the text WAS the call, not an answer
                }
            }
        }

        if (!string.IsNullOrEmpty(content))
            events.Insert(0, new TextDeltaEvent("0", content!));

        if (done)
            events.Add(new StepFinishEvent(0, "stop", new Usage(inputTokens, outputTokens)));

        return events;
    }

    /// <summary>Cheap scan before the full legacy parse — keeps plain-text
    /// chunks (every token of an ordinary answer) allocation-free.</summary>
    private static bool HasLegacyMarker(string text) =>
        text.Contains('$') || text.Contains("```", StringComparison.Ordinal);

    /// <summary>
    ///     Turn recovered legacy calls into the same event pair a native
    ///     tool call emits. Ids are stable per recovered ordinal via the
    ///     stream's index→id map (disjoint key range), so a repeated call in
    ///     a later chunk coalesces instead of spawning a duplicate.
    /// </summary>
    private static void EmitLegacyCalls(
        List<LlmEvent> events, Dictionary<int, string> indexToId,
        IReadOnlyList<EmulatedToolCall> calls)
    {
        for (int i = 0; i < calls.Count; i++)
        {
            EmulatedToolCall call = calls[i];
            int key = LegacyCallIndexBase + i;
            string id = indexToId.GetValueOrDefault(key) ?? $"legacy{i}";
            indexToId[key] = id;

            events.Add(new ToolCallStartEvent(id, call.ToolName));
            events.Add(new ToolCallDeltaEvent(id, call.ArgumentsJson));
        }
    }

    /// <summary>
    ///     The property names this client dispatches on. The table is
    ///     EXHAUSTIVE over the literals <see cref="HandleValue" /> compares
    ///     against — a name missing from it would stop being dispatched on,
    ///     which is why the parity test pins the whole set.
    /// </summary>
    private static string? MatchChunkProperty(ReadOnlySpan<byte> raw) => raw switch
    {
        // Ordered by how often a line carries them, not alphabetically.
        var x when x.SequenceEqual("message"u8) => "message",
        var x when x.SequenceEqual("content"u8) => "content",
        var x when x.SequenceEqual("done"u8) => "done",
        var x when x.SequenceEqual("tool_calls"u8) => "tool_calls",
        var x when x.SequenceEqual("index"u8) => "index",
        var x when x.SequenceEqual("id"u8) => "id",
        var x when x.SequenceEqual("function"u8) => "function",
        var x when x.SequenceEqual("name"u8) => "name",
        var x when x.SequenceEqual("arguments"u8) => "arguments",
        var x when x.SequenceEqual("prompt_eval_count"u8) => "prompt_eval_count",
        var x when x.SequenceEqual("eval_count"u8) => "eval_count",
        _ => null
    };

    private static void HandleValue(
        ref Utf8JsonReader reader, int depth, string prop, bool inMessage, bool inTc, bool inFunction,
        List<LlmEvent> events,
        ref bool rInMessage, ref bool rInToolCalls, ref bool rInFunction, ref int rDepth,
        ref string? rContent, ref int rTcIndex, ref string? rTcWireId, ref string? rTcName, ref string? rTcArgs,
        ref bool rDone, ref int rInputTokens, ref int rOutputTokens)
    {
        // NOTE: text content emits at the END (after tool calls), matching
        // the DOM walker's section order (content first, then tool calls,
        // then done) regardless of wire order.
        if (depth == 1)
        {
            if (prop == "message")
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    rInMessage = true;
                    rDepth++;
                }
                else
                {
                    Utf8JsonReaderExtensions.SkipContainer(ref reader);
                }
            }
            else if (prop == "done")
            {
                rDone = reader.TokenType == JsonTokenType.True;
            }
            else if (prop == "prompt_eval_count")
            {
                rInputTokens = Utf8JsonReaderExtensions.ReadTolerantInt(ref reader);
            }
            else if (prop == "eval_count")
            {
                rOutputTokens = Utf8JsonReaderExtensions.ReadTolerantInt(ref reader);
            }
            else
            {
                Utf8JsonReaderExtensions.SkipContainer(ref reader);
            }

            return;
        }

        if (depth == 2 && inMessage)
        {
            if (prop == "content")
            {
                rContent = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (prop == "tool_calls")
            {
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    rInToolCalls = true;
                    rDepth++;
                }
                else
                {
                    Utf8JsonReaderExtensions.SkipContainer(ref reader);
                }
            }
            else
            {
                Utf8JsonReaderExtensions.SkipContainer(ref reader);
            }

            return;
        }

        if (depth == 4 && inTc)
        {
            if (prop == "index")
            {
                // DOM parity: only a JSON number counts (string index → 0).
                rTcIndex = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int idx)
                    ? idx
                    : 0;
            }
            else if (prop == "id")
            {
                rTcWireId = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (prop == "function")
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    rInFunction = true;
                    rDepth++;
                }
                else
                {
                    Utf8JsonReaderExtensions.SkipContainer(ref reader);
                }
            }
            else
            {
                Utf8JsonReaderExtensions.SkipContainer(ref reader);
            }

            return;
        }

        if (depth == 5 && inFunction)
        {
            if (prop == "name")
            {
                rTcName = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (prop == "arguments")
            {
                rTcArgs = ReadArgsValue(ref reader);
            }
            else
            {
                Utf8JsonReaderExtensions.SkipContainer(ref reader);
            }

            return;
        }

        Utf8JsonReaderExtensions.SkipContainer(ref reader);
    }

    /// <summary>
    ///     Tool arguments: a string passes through, any other JSON value
    ///     re-serializes canonically (GetRawText parity for the object case).
    /// </summary>
    private static string? ReadArgsValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString();

        if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray
                or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null))
            return null;

        // Rare path (object-shaped arguments): re-serialize canonically.
        // ArrayBufferWriter pools internally, so this stays off the LOH.
        var stream = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(stream))
        {
            CopyValue(ref reader, writer);
        }

        return Encoding.UTF8.GetString(stream.WrittenSpan);
    }

    private static void CopyValue(ref Utf8JsonReader reader, Utf8JsonWriter writer)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                writer.WriteStartObject();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    writer.WritePropertyName(reader.GetString()!);
                    reader.Read();
                    CopyValue(ref reader, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonTokenType.StartArray:
                writer.WriteStartArray();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    CopyValue(ref reader, writer);
                }

                writer.WriteEndArray();
                break;

            case JsonTokenType.String:
                writer.WriteStringValue(reader.GetString());
                break;

            case JsonTokenType.Number:
                writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                break;

            case JsonTokenType.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonTokenType.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonTokenType.Null:
                writer.WriteNullValue();
                break;
        }
    }

}

public sealed class OllamaConfig
{
    public string? BaseUrl { get; set; } = "http://localhost:11434";
    public string KeepAlive { get; set; } = "5m";
}

/// <summary>
///     SG7: BCL <c>[LoggerMessage]</c> delegates for <see cref="OllamaLlmClient" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class OllamaLlmClientLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Ollama stream completed: {Malformed} malformed line(s) skipped, {Remaps} positional tool-call id fallback(s)")]
    public static partial void StreamCompleted(ILogger logger, int malformed, int remaps);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Ollama stream task failed: {Message}")]
    public static partial void StreamTaskFailed(ILogger logger, Exception ex, string message);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Skipping malformed Ollama NDJSON line #{Count}: {Line}")]
    public static partial void SkippingMalformedLine(ILogger logger, Exception ex, int count, string line);
}
