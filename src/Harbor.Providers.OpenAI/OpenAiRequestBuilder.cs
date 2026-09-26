using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Providers.Internal;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.OpenAI;

/// <summary>
///     Builds OpenAI wire requests from <see cref="LlmRequest" />:
///     Chat Completions payloads (+ message mapping) and Responses-API
///     payloads (+ input mapping). Pure functions — no I/O, no state.
///     Extracted from <see cref="OpenAILlmClient" /> (§ROP god-object split).
/// </summary>
internal static class OpenAiRequestBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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

        var payload = new Dictionary<string, object?>(8)
        {
            ["model"] = request.Model,
            ["messages"] = BuildChatMessages(request, logger),
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true }
        };

        // Reasoning models use max_completion_tokens, others max_tokens
        bool isReasoning = IsReasoningModel(request.Model);
        if (request.MaxOutputTokens.HasValue)
        {
            payload[isReasoning ? "max_completion_tokens" : "max_tokens"] = request.MaxOutputTokens;
        }

        // Reasoning models don't support temperature (or require 1)
        if (!isReasoning && request.Temperature.HasValue)
            payload["temperature"] = request.Temperature;

        if (request.TopP.HasValue && !isReasoning) payload["top_p"] = request.TopP;

        if (request.Tools.Count > 0)
        {
            payload["tools"] = request.Tools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.InputSchema }
            });
        }

        if (request.ToolChoice is not null)
        {
            payload["tool_choice"] = request.ToolChoice switch
            {
                ToolChoice.Auto => "auto",
                ToolChoice.None => "none",
                ToolChoice.Required => "required",
                ToolChoice.Specific s => new { type = "function", function = new { name = s.ToolName } },
                _ => "auto"
            };
        }

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(jsonBytes)
        };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return msg;
    }

    public static void AddBearerAuth(HttpRequestMessage msg, string apiKey)
    {
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public static HttpRequestMessage BuildResponsesRequest(LlmRequest request, string baseUrl, ILogger logger)
    {
        string url = string.Concat(baseUrl, "/responses");

        var payload = new Dictionary<string, object?>(6)
        {
            ["model"] = request.Model,
            ["input"] = BuildResponsesInput(request, logger),
            ["stream"] = true
        };

        if (request.MaxOutputTokens.HasValue)
            payload["max_output_tokens"] = request.MaxOutputTokens;

        // Reasoning effort for o-series
        if (request.ReasoningEffort.HasValue)
        {
            payload["reasoning"] = new
            {
                effort = request.ReasoningEffort.Value.ToString().ToLowerInvariant()
            };
        }

        if (request.Tools.Count > 0)
        {
            payload["tools"] = request.Tools.Select(t => new
            {
                type = "function",
                name = t.Name,
                description = t.Description,
                parameters = t.InputSchema
            });
        }

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(jsonBytes)
        };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return msg;
    }

    public static List<object> BuildChatMessages(LlmRequest request, ILogger logger)
    {
        var result = new List<object>(request.Messages.Count + 1);

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            result.Add(new { role = "system", content = request.SystemPrompt });
        }

        foreach (var msg in request.Messages)
        {
            result.Add(msg switch
            {
                LlmUserMessage u => new
                {
                    role = "user",
                    // ROP-A ПР.12: non-text blocks dropped loudly.
                    content = ProviderPayload.FirstTextOrEmpty(u.Content, logger, "openai")
                },
                LlmAssistantMessage a => new
                {
                    role = "assistant",
                    content = a.Content.OfType<LlmTextBlock>().Select(b => b.Text).FirstOrDefault(),
                    tool_calls = a.Content.OfType<LlmToolCallBlock>().Select(tc => new
                    {
                        id = tc.Id,
                        type = "function",
                        function = new { name = tc.Name, arguments = tc.Arguments.GetRawText() }
                    }).ToList()
                },
                LlmToolResultMessage tr => new
                {
                    role = "tool",
                    tool_call_id = tr.ToolCallId,
                    content = tr.Output
                },
                _ => new { role = "user", content = "" }
            });
        }

        return result;
    }

    public static List<object> BuildResponsesInput(LlmRequest request, ILogger logger)
    {
        var result = new List<object>(request.Messages.Count + 1);

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            result.Add(new { role = "system", content = request.SystemPrompt });
        }

        foreach (var msg in request.Messages)
        {
            result.Add(msg switch
            {
                LlmUserMessage u => new
                {
                    role = "user",
                    // ROP-A ПР.12: non-text blocks dropped loudly.
                    content = ProviderPayload.FirstTextOrEmpty(u.Content, logger, "openai")
                },
                LlmAssistantMessage a => new
                {
                    role = "assistant",
                    content = a.Content.OfType<LlmTextBlock>().Select(b => b.Text).FirstOrDefault()
                },
                LlmToolResultMessage tr => new
                {
                    type = "function_call_output",
                    call_id = tr.ToolCallId,
                    output = tr.Output
                },
                _ => new { role = "user", content = "" }
            });
        }

        return result;
    }
}
