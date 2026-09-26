using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.Anthropic;

/// <summary>
///     Builds Anthropic Messages-API wire requests from <see cref="LlmRequest" />:
///     system-as-field, message mapping, thinking budgets, tools and tool_choice.
///     Pure functions — no I/O, no state.
///     Extracted from <see cref="AnthropicLlmClient" /> (§ROP god-object split).
/// </summary>
internal static class AnthropicRequestBuilder
{
    public const string DefaultApiVersion = "2023-06-01";
    public const string DefaultBetaFeatures = "interleaved-thinking-2025-05-14,fine-grained-tool-streaming-2025-05-14";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static HttpRequestMessage BuildRequest(
        LlmRequest request,
        string baseUrl,
        string apiKey,
        string? apiVersion,
        string? betaFeatures)
    {
        string url = string.Concat(baseUrl, "/messages");

        // Pre-size the payload dictionary for the maximum known key count to avoid
        // rehash/resize during population. Worst-case keys: model, messages, stream,
        // max_tokens, system, temperature, top_p, top_k, thinking, tools, tool_choice.
        var payload = new Dictionary<string, object?>(12)
        {
            ["model"] = request.Model,
            ["messages"] = BuildMessages(request),
            ["stream"] = true,
            ["max_tokens"] = request.MaxOutputTokens ?? 8192
        };

        // System prompt as separate field with optional cache_control
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            if (request.CacheStrategy == CacheStrategy.Ephemeral)
            {
                payload["system"] = new[]
                {
                    new
                    {
                        type = "text",
                        text = request.SystemPrompt,
                        cache_control = new { type = "ephemeral" }
                    }
                };
            }
            else
            {
                payload["system"] = request.SystemPrompt;
            }
        }

        if (request.Temperature.HasValue) payload["temperature"] = request.Temperature;
        if (request.TopP.HasValue) payload["top_p"] = request.TopP;
        if (request.TopK.HasValue) payload["top_k"] = request.TopK;

        // Extended thinking
        if (request.ReasoningEffort.HasValue)
        {
            int budget = request.ReasoningEffort.Value switch
            {
                ReasoningEffort.Low => 5000,
                ReasoningEffort.Medium => 10000,
                ReasoningEffort.High => 20000,
                ReasoningEffort.Max => 32000,
                _ => 10000
            };
            payload["thinking"] = new { type = "enabled", budget_tokens = budget };
        }

        if (request.Tools.Count > 0)
        {
            payload["tools"] = request.Tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                input_schema = t.InputSchema
            });
        }

        if (request.ToolChoice is not null)
        {
            payload["tool_choice"] = request.ToolChoice switch
            {
                ToolChoice.Auto => new { type = "auto" },
                ToolChoice.None => new { type = "none" },
                ToolChoice.Required => new { type = "any" },
                ToolChoice.Specific s => new { type = "tool", name = s.ToolName },
                _ => new { type = "auto" }
            };
        }

        // Serialize directly to UTF-8 bytes and wrap in ByteArrayContent — this skips
        // the intermediate string allocation that JsonSerializer.Serialize(string) +
        // StringContent(UTF-8 encode) would otherwise produce.
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(jsonBytes)
        };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        // Auth: x-api-key header
        msg.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        msg.Headers.TryAddWithoutValidation("anthropic-version", apiVersion ?? DefaultApiVersion);
        msg.Headers.TryAddWithoutValidation("anthropic-beta", betaFeatures ?? DefaultBetaFeatures);

        return msg;
    }

    public static List<object> BuildMessages(LlmRequest request)
    {
        var result = new List<object>(request.Messages.Count);

        foreach (var msg in request.Messages)
        {
            switch (msg)
            {
                case LlmUserMessage u:
                    result.Add(new
                    {
                        role = "user",
                        content = ConvertContentBlocks(u.Content)
                    });
                    break;

                case LlmAssistantMessage a:
                    result.Add(new
                    {
                        role = "assistant",
                        content = ConvertContentBlocks(a.Content)
                    });
                    break;

                case LlmToolResultMessage tr:
                    // Anthropic: tool_result is a content block in a user message
                    result.Add(new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                type = "tool_result",
                                tool_use_id = tr.ToolCallId,
                                content = tr.Output,
                                is_error = tr.IsError
                            }
                        }
                    });
                    break;
            }
        }

        return result;
    }

    public static object ConvertContentBlocks(IReadOnlyList<LlmContentBlock> blocks)
    {
        // If single text block, return as string (saves tokens)
        if (blocks.Count == 1 && blocks[0] is LlmTextBlock text)
            return text.Text;

        object[] converted = new object[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            converted[i] = blocks[i] switch
            {
                LlmTextBlock t => new { type = "text", text = t.Text },
                LlmImageBlock img => new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = img.MimeType,
                        data = Convert.ToBase64String(img.Data)
                    }
                },
                LlmToolCallBlock tc => new
                {
                    type = "tool_use",
                    id = tc.Id,
                    name = tc.Name,
                    input = tc.Arguments.Deserialize<JsonElement>()
                },
                LlmToolResultBlock tr => new
                {
                    type = "tool_result",
                    tool_use_id = tr.ToolUseId,
                    content = tr.Content,
                    is_error = tr.IsError
                },
                LlmThinkingBlock th => new
                {
                    type = "thinking",
                    thinking = th.Text
                },
                _ => new { type = "text", text = "" }
            };
        }
        return converted;
    }
}
