using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;

namespace Harbor.Providers.Anthropic;

/// <summary>
///     Builds Anthropic Messages-API wire requests from <see cref="LlmRequest" />:
///     system-as-field, message mapping, thinking budgets, tools and tool_choice.
///     Pure functions — no I/O, no state.
///     Extracted from <see cref="AnthropicLlmClient" /> (§ROP god-object split).
/// </summary>
/// <remarks>
///     <para>
///         §PERF-002 / #475 — the payload is written straight to a
///         <see cref="Utf8JsonWriter" />. It used to be assembled as a
///         <c>Dictionary&lt;string, object?&gt;</c> of anonymous types and handed to
///         <c>JsonSerializer.SerializeToUtf8Bytes</c>, which resolves a
///         <c>JsonTypeInfo</c> for the runtime type of every value: reflection, and
///         a hard failure under a trimmed / NativeAOT publish, where
///         reflection-based serialization is disabled by default. The pattern
///         already existed in this repo — <c>OpenAiCompatibleLlmClient</c> writes
///         to a <see cref="Utf8JsonWriter" />.
///     </para>
///     <para>
///         The named <c>Write*</c> methods are the anti-corruption layer the
///         anonymous types used to hide: every step from <see cref="LlmMessage" />
///         to Anthropic's dialect is now a line of code with a name, which is what
///         makes the wire shape assertable at all.
///     </para>
///     <para>
///         Wire compatibility: the key order below is the order the dictionary used
///         to be populated in, and each optional key keeps the
///         <c>DefaultIgnoreCondition = WhenWritingNull</c> semantics the old
///         options carried — a branch that wrote a null property wrote nothing, so
///         the branches here are guarded rather than written as nulls.
///     </para>
/// </remarks>
internal static class AnthropicRequestBuilder
{
    public const string DefaultApiVersion = "2023-06-01";
    public const string DefaultBetaFeatures = "interleaved-thinking-2025-05-14,fine-grained-tool-streaming-2025-05-14";

    /// <summary>
    ///     Initial writer capacity. A small request is a few hundred bytes; the
    ///     buffer grows in place if a long conversation does not fit.
    /// </summary>
    private const int InitialPayloadCapacity = 1024;

    /// <summary>What a request costs when the caller sets no limit (§ default).</summary>
    private const int DefaultMaxTokens = 8192;

    public static HttpRequestMessage BuildRequest(
        LlmRequest request,
        string baseUrl,
        string apiKey,
        string? apiVersion,
        string? betaFeatures)
    {
        string url = string.Concat(baseUrl, "/messages");

        var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(WritePayload(request))
        };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        // Auth: x-api-key header
        msg.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        msg.Headers.TryAddWithoutValidation("anthropic-version", apiVersion ?? DefaultApiVersion);
        msg.Headers.TryAddWithoutValidation("anthropic-beta", betaFeatures ?? DefaultBetaFeatures);

        return msg;
    }

    /// <summary>Renders the whole <c>/messages</c> body as UTF-8 JSON.</summary>
    private static byte[] WritePayload(LlmRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>(InitialPayloadCapacity);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            writer.WriteString("model", request.Model);
            WriteMessages(writer, request);
            writer.WriteBoolean("stream", true);
            writer.WriteNumber("max_tokens", request.MaxOutputTokens ?? DefaultMaxTokens);

            // System prompt as separate field with optional cache_control
            WriteSystem(writer, request);

            if (request.Temperature.HasValue) writer.WriteNumber("temperature", request.Temperature.Value);
            if (request.TopP.HasValue) writer.WriteNumber("top_p", request.TopP.Value);
            if (request.TopK.HasValue) writer.WriteNumber("top_k", request.TopK.Value);

            // Extended thinking
            WriteThinking(writer, request.ReasoningEffort);

            WriteTools(writer, request.Tools);
            WriteToolChoice(writer, request.ToolChoice);

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    ///     The <c>system</c> field. Plain string without caching; an ephemeral
    ///     cache breakpoint turns it into a one-block array carrying
    ///     <c>cache_control</c>.
    /// </summary>
    private static void WriteSystem(Utf8JsonWriter writer, LlmRequest request)
    {
        if (string.IsNullOrEmpty(request.SystemPrompt))
        {
            return;
        }

        if (request.CacheStrategy != CacheStrategy.Ephemeral)
        {
            writer.WriteString("system", request.SystemPrompt);
            return;
        }

        writer.WriteStartArray("system");
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", request.SystemPrompt);
        writer.WriteStartObject("cache_control");
        writer.WriteString("type", "ephemeral");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    /// <summary>
    ///     <c>thinking: {"type":"enabled","budget_tokens":N}</c>, omitted when no
    ///     reasoning effort was asked for. The budget table is the model's, not a
    ///     ratio of the window — the API rejects a budget that does not leave room
    ///     for the answer.
    /// </summary>
    private static void WriteThinking(Utf8JsonWriter writer, ReasoningEffort? effort)
    {
        if (effort is not { } value)
        {
            return;
        }

        int budget = value switch
        {
            ReasoningEffort.Low => 5000,
            ReasoningEffort.Medium => 10000,
            ReasoningEffort.High => 20000,
            ReasoningEffort.Max => 32000,
            _ => 10000
        };

        writer.WriteStartObject("thinking");
        writer.WriteString("type", "enabled");
        writer.WriteNumber("budget_tokens", budget);
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
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("input_schema");
            tool.InputSchema.WriteTo(writer);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    ///     Anthropic names the required variant <c>"any"</c> where OpenAI says
    ///     <c>"required"</c>, and a pinned tool is a bare
    ///     <c>{"type":"tool","name":…}</c> rather than a nested function object.
    /// </summary>
    private static void WriteToolChoice(Utf8JsonWriter writer, ToolChoice? choice)
    {
        if (choice is null)
        {
            return;
        }

        writer.WriteStartObject("tool_choice");
        switch (choice)
        {
            case ToolChoice.None:
                writer.WriteString("type", "none");
                break;
            case ToolChoice.Required:
                writer.WriteString("type", "any");
                break;
            case ToolChoice.Specific specific:
                writer.WriteString("type", "tool");
                writer.WriteString("name", specific.ToolName);
                break;
            default:
                writer.WriteString("type", "auto");
                break;
        }

        writer.WriteEndObject();
    }

    /// <summary>
    ///     <c>messages[]</c>. A message subtype this builder does not know is
    ///     skipped, which is what the former <c>switch</c> did — Anthropic rejects
    ///     a message with no <c>role</c>, so emitting a placeholder would be worse
    ///     than omitting it.
    /// </summary>
    private static void WriteMessages(Utf8JsonWriter writer, LlmRequest request)
    {
        writer.WriteStartArray("messages");

        for (int i = 0; i < request.Messages.Count; i++)
        {
            switch (request.Messages[i])
            {
                case LlmUserMessage user:
                    WriteRole(writer, "user", user.Content);
                    break;

                case LlmAssistantMessage assistant:
                    WriteRole(writer, "assistant", assistant.Content);
                    break;

                case LlmToolResultMessage result:
                    // Anthropic: tool_result is a content block in a user message
                    writer.WriteStartObject();
                    writer.WriteString("role", "user");
                    writer.WriteStartArray("content");
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_result");
                    writer.WriteString("tool_use_id", result.ToolCallId);
                    writer.WriteString("content", result.Output);
                    writer.WriteBoolean("is_error", result.IsError);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    break;
            }
        }

        writer.WriteEndArray();
    }

    private static void WriteRole(Utf8JsonWriter writer, string role, IReadOnlyList<LlmContentBlock> content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        WriteContent(writer, content);
        writer.WriteEndObject();
    }

    /// <summary>
    ///     A message body: a bare string for a lone text block (Anthropic's compact
    ///     form, and what the model sees as plain prose), otherwise the block array.
    /// </summary>
    /// <remarks>
    ///     The property name is written here rather than by the caller because
    ///     <see cref="Utf8JsonWriter" /> has <c>WriteStartArray(string)</c> and
    ///     <c>WriteString(string, string)</c> but no unnamed-array equivalent — so a
    ///     helper that emitted the value in "array element" position could not open
    ///     an array, only close one.
    /// </remarks>
    private static void WriteContent(Utf8JsonWriter writer, IReadOnlyList<LlmContentBlock> blocks)
    {
        if (blocks.Count == 1 && blocks[0] is LlmTextBlock lone)
        {
            writer.WriteString("content", lone.Text);
            return;
        }

        writer.WriteStartArray("content");
        for (int i = 0; i < blocks.Count; i++)
        {
            switch (blocks[i])
            {
                case LlmTextBlock text:
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text.Text);
                    writer.WriteEndObject();
                    break;

                case LlmImageBlock image:
                    writer.WriteStartObject();
                    writer.WriteString("type", "image");
                    writer.WriteStartObject("source");
                    writer.WriteString("type", "base64");
                    writer.WriteString("media_type", image.MimeType);
                    // §PERF-002: the base64 is produced here, so no byte[] ever
                    // reaches a serializer that would have to resolve its element
                    // type at run time.
                    writer.WriteString("data", Convert.ToBase64String(image.Data));
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;

                case LlmToolCallBlock toolCall:
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_use");
                    writer.WriteString("id", toolCall.Id);
                    writer.WriteString("name", toolCall.Name);
                    writer.WritePropertyName("input");
                    toolCall.Arguments.WriteTo(writer);
                    writer.WriteEndObject();
                    break;

                case LlmToolResultBlock toolResult:
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_result");
                    writer.WriteString("tool_use_id", toolResult.ToolUseId);
                    writer.WriteString("content", toolResult.Content);
                    writer.WriteBoolean("is_error", toolResult.IsError);
                    writer.WriteEndObject();
                    break;

                case LlmThinkingBlock thinking:
                    writer.WriteStartObject();
                    writer.WriteString("type", "thinking");
                    writer.WriteString("thinking", thinking.Text);
                    writer.WriteEndObject();
                    break;

                default:
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", "");
                    writer.WriteEndObject();
                    break;
            }
        }

        writer.WriteEndArray();
    }
}
