using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Providers.Internal;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.OpenAI;

/// <summary>
///     Builds OpenAI wire requests from <see cref="LlmRequest" /> — Chat
///     Completions and the Responses API used by o1/o3/gpt-5 and later.
///     Pure functions — no I/O, no state.
/// </summary>
/// <remarks>
///     <para>
///         §PERF-002 / #475 — the payload is written straight to a
///         <see cref="Utf8JsonWriter" />. It used to be a
///         <c>Dictionary&lt;string, object?&gt;</c> of anonymous types serialized by
///         <c>JsonSerializer.SerializeToUtf8Bytes</c>. That resolves a
///         <c>JsonTypeInfo</c> for the runtime type of every value — reflection,
///         and a hard failure under a trimmed / NativeAOT publish, where
///         reflection-based serialization is disabled by default. The
///         <c>TypeInfoResolver</c> on the old options combined the
///         source-generated <c>OpenAiWireContext</c> with
///         <c>DefaultJsonTypeInfoResolver</c> precisely because the rest of the
///         payload had no other way out; that escape hatch is gone, and with it
///         the one DTO (<c>OpenAiImageUrl</c>) that existed only to be named in a
///         context.
///     </para>
///     <para>
///         <c>OpenAiCompatibleLlmClient</c> already wrote to a
///         <see cref="Utf8JsonWriter" />; this file is the same shape, so the
///         native client and the adapter that serves most providers now emit the
///         same bytes from the same shape.
///     </para>
///     <para>
///         Wire compatibility: key order is the order the dictionary used to be
///         populated in, and the optional keys keep their
///         <c>DefaultIgnoreCondition = WhenWritingNull</c> semantics. The one that
///         actually changes bytes is an assistant message with no text block:
///         its <c>content</c> was null and the property was dropped, so it is
///         dropped here too — see <see cref="WriteAssistant" />.
///     </para>
/// </remarks>
internal static class OpenAiRequestBuilder
{
    /// <summary>
    ///     Initial writer capacity. A small request is a few hundred bytes; the
    ///     buffer grows in place if a long conversation does not fit.
    /// </summary>
    private const int InitialPayloadCapacity = 1024;

    public static bool IsReasoningModel(string modelId)
    {
        return modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
               modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
               modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase) ||
               modelId.Contains("gpt-5", StringComparison.OrdinalIgnoreCase);
    }

    public static HttpRequestMessage BuildChatCompletionsRequest(LlmRequest request, string baseUrl, ILogger logger)
    {
        string url = string.Concat(baseUrl, "/chat/completions");

        var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(WriteChatCompletions(request, logger))
        };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return msg;
    }

    public static HttpRequestMessage BuildResponsesRequest(LlmRequest request, string baseUrl, ILogger logger)
    {
        string url = string.Concat(baseUrl, "/responses");

        var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(WriteResponses(request, logger))
        };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return msg;
    }

    public static void AddBearerAuth(HttpRequestMessage msg, string apiKey)
    {
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    // ---------------------------------------------------------------------
    // /chat/completions
    // ---------------------------------------------------------------------

    private static byte[] WriteChatCompletions(LlmRequest request, ILogger logger)
    {
        bool isReasoning = IsReasoningModel(request.Model);

        var buffer = new ArrayBufferWriter<byte>(InitialPayloadCapacity);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            writer.WriteString("model", request.Model);
            WriteChatMessages(writer, request, logger);
            writer.WriteBoolean("stream", true);

            writer.WriteStartObject("stream_options");
            writer.WriteBoolean("include_usage", true);
            writer.WriteEndObject();

            // Reasoning models use max_completion_tokens, others max_tokens
            if (request.MaxOutputTokens.HasValue)
            {
                writer.WriteNumber(isReasoning ? "max_completion_tokens" : "max_tokens", request.MaxOutputTokens.Value);
            }

            // Reasoning models don't support temperature (or require 1)
            if (!isReasoning && request.Temperature.HasValue)
            {
                writer.WriteNumber("temperature", request.Temperature.Value);
            }

            if (request.TopP.HasValue && !isReasoning)
            {
                writer.WriteNumber("top_p", request.TopP.Value);
            }

            WriteChatTools(writer, request.Tools);
            WriteChatToolChoice(writer, request.ToolChoice);

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteChatMessages(Utf8JsonWriter writer, LlmRequest request, ILogger logger)
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
                // #386: image turns become a content[] array of text + image_url
                // parts; a text-only turn keeps the compact string form.
                case LlmUserMessage user:
                    WriteChatUser(writer, user, logger);
                    break;

                case LlmAssistantMessage assistant:
                    WriteAssistant(writer, assistant);
                    break;

                case LlmToolResultMessage result:
                    writer.WriteStartObject();
                    writer.WriteString("role", "tool");
                    writer.WriteString("tool_call_id", result.ToolCallId);
                    writer.WriteString("content", result.Output);
                    writer.WriteEndObject();
                    break;

                default:
                    WriteRoleAndText(writer, "user", "");
                    break;
            }
        }

        writer.WriteEndArray();
    }

    /// <summary>
    ///     Project a user message for <c>/chat/completions</c>: either
    ///     <c>content = "&lt;text&gt;"</c> (the compact legacy shape) or a
    ///     <c>content[]</c> array of text and <c>image_url</c> parts.
    /// </summary>
    private static void WriteChatUser(Utf8JsonWriter writer, LlmUserMessage user, ILogger logger)
    {
        if (!OpenAiImageContent.HasImage(user.Content))
        {
            WriteRoleAndText(writer, "user", ProviderPayload.FirstTextOrEmpty(user.Content, logger, "openai"));
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("role", "user");
        writer.WriteStartArray("content");

        for (int i = 0; i < user.Content.Count; i++)
        {
            switch (user.Content[i])
            {
                case LlmTextBlock text:
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text.Text);
                    writer.WriteEndObject();
                    break;

                case LlmImageBlock image:
                    writer.WriteStartObject();
                    writer.WriteString("type", "image_url");
                    writer.WriteStartObject("image_url");
                    // §PERF-002: the base64 is built by the shared data-URL helper,
                    // so only the finished string reaches the writer.
                    writer.WriteString("url", OpenAiImageContent.ToDataUrl(image.MimeType, image.Data));
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;

                default:
                    logger.LogWarning(
                        "Dropping unsupported content block(s) of type {BlockType} for openai",
                        user.Content[i].Type);
                    break;
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    ///     An assistant turn. <c>content</c> is the first text block and is
    ///     <b>omitted when there is none</b> — a tool-calling turn has no prose,
    ///     and the old serializer dropped the null property rather than sending
    ///     <c>"content": null</c>, which OpenAI rejects.
    /// </summary>
    private static void WriteAssistant(Utf8JsonWriter writer, LlmAssistantMessage assistant)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "assistant");

        if (FirstTextOrNull(assistant.Content) is { } text)
        {
            writer.WriteString("content", text);
        }

        // Always emitted, empty array included: the shape predates the writer and
        // the compat adapter emits it the same way.
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

    private static void WriteChatTools(Utf8JsonWriter writer, IReadOnlyList<ToolDefinition> tools)
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

    /// <summary>
    ///     Chat Completions takes a bare string for the three generic choices and
    ///     an object only for a pinned tool.
    /// </summary>
    private static void WriteChatToolChoice(Utf8JsonWriter writer, ToolChoice? choice)
    {
        if (choice is null)
        {
            return;
        }

        switch (choice)
        {
            case ToolChoice.None:
                writer.WriteString("tool_choice", "none");
                break;

            case ToolChoice.Required:
                writer.WriteString("tool_choice", "required");
                break;

            case ToolChoice.Specific specific:
                writer.WriteStartObject("tool_choice");
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", specific.ToolName);
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;

            default:
                writer.WriteString("tool_choice", "auto");
                break;
        }
    }

    // ---------------------------------------------------------------------
    // /responses
    // ---------------------------------------------------------------------

    private static byte[] WriteResponses(LlmRequest request, ILogger logger)
    {
        var buffer = new ArrayBufferWriter<byte>(InitialPayloadCapacity);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            writer.WriteString("model", request.Model);
            WriteResponsesInput(writer, request, logger);
            writer.WriteBoolean("stream", true);

            if (request.MaxOutputTokens.HasValue)
            {
                writer.WriteNumber("max_output_tokens", request.MaxOutputTokens.Value);
            }

            // Reasoning effort for o-series
            if (request.ReasoningEffort.HasValue)
            {
                writer.WriteStartObject("reasoning");
                writer.WriteString("effort", request.ReasoningEffort.Value.ToString().ToLowerInvariant());
                writer.WriteEndObject();
            }

            // The Responses API takes the function fields inline rather than
            // nested under "function".
            WriteResponsesTools(writer, request.Tools);

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteResponsesInput(Utf8JsonWriter writer, LlmRequest request, ILogger logger)
    {
        writer.WriteStartArray("input");

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            WriteRoleAndText(writer, "system", request.SystemPrompt);
        }

        for (int i = 0; i < request.Messages.Count; i++)
        {
            switch (request.Messages[i])
            {
                // #386: the Responses API names the same part `input_image` and
                // takes the data URL directly.
                case LlmUserMessage user:
                    WriteResponsesUser(writer, user, logger);
                    break;

                case LlmAssistantMessage assistant:
                    writer.WriteStartObject();
                    writer.WriteString("role", "assistant");
                    if (FirstTextOrNull(assistant.Content) is { } text)
                    {
                        writer.WriteString("content", text);
                    }

                    writer.WriteEndObject();
                    break;

                case LlmToolResultMessage result:
                    writer.WriteStartObject();
                    writer.WriteString("type", "function_call_output");
                    writer.WriteString("call_id", result.ToolCallId);
                    writer.WriteString("output", result.Output);
                    writer.WriteEndObject();
                    break;

                default:
                    WriteRoleAndText(writer, "user", "");
                    break;
            }
        }

        writer.WriteEndArray();
    }

    /// <summary>
    ///     Project a user message for <c>/responses</c>: the compact string form
    ///     for text-only turns, otherwise a <c>content[]</c> array whose image
    ///     parts are <c>{"type":"input_image","image_url":"data:…"}</c>.
    /// </summary>
    private static void WriteResponsesUser(Utf8JsonWriter writer, LlmUserMessage user, ILogger logger)
    {
        if (!OpenAiImageContent.HasImage(user.Content))
        {
            WriteRoleAndText(writer, "user", ProviderPayload.FirstTextOrEmpty(user.Content, logger, "openai"));
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("role", "user");
        writer.WriteStartArray("content");

        for (int i = 0; i < user.Content.Count; i++)
        {
            switch (user.Content[i])
            {
                case LlmTextBlock text:
                    writer.WriteStartObject();
                    writer.WriteString("type", "input_text");
                    writer.WriteString("text", text.Text);
                    writer.WriteEndObject();
                    break;

                case LlmImageBlock image:
                    writer.WriteStartObject();
                    writer.WriteString("type", "input_image");
                    writer.WriteString("image_url", OpenAiImageContent.ToDataUrl(image.MimeType, image.Data));
                    writer.WriteEndObject();
                    break;

                default:
                    logger.LogWarning(
                        "Dropping unsupported content block(s) of type {BlockType} for openai responses",
                        user.Content[i].Type);
                    break;
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteResponsesTools(Utf8JsonWriter writer, IReadOnlyList<ToolDefinition> tools)
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
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("parameters");
            tool.InputSchema.WriteTo(writer);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // ---------------------------------------------------------------------
    // shared
    // ---------------------------------------------------------------------

    /// <summary>The <c>{"role":…,"content":"…"}</c> shape used by both APIs.</summary>
    private static void WriteRoleAndText(Utf8JsonWriter writer, string role, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }

    /// <summary>
    ///     The first text block's text, or <see langword="null" /> when the turn
    ///     carries none. <see langword="null" /> is meaningful here: it is what
    ///     makes the caller omit <c>content</c> entirely.
    /// </summary>
    private static string? FirstTextOrNull(IReadOnlyList<LlmContentBlock> content)
    {
        for (int i = 0; i < content.Count; i++)
        {
            if (content[i] is LlmTextBlock text)
            {
                return text.Text;
            }
        }

        return null;
    }
}
