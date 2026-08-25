using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Providers.Internal;
using Microsoft.Extensions.Logging;
namespace Harbor.Providers.OpenAI;
/// <summary>
///     Native OpenAI provider — Chat Completions API + Responses API (for o1/o3/GPT-5+).
///     Implements Strategy pattern (GOF) via ILlmClient.
///     Special features beyond OpenAI-compat:
///     - Responses API for reasoning models (o1, o3, o4-mini)
///     - reasoning_effort parameter
///     - max_completion_tokens (vs legacy max_tokens)
///     - reasoning_content streaming
///     - tool_choice with specific function
/// </summary>
public sealed class OpenAILlmClient : ILlmClient
{
    private const string DefaultBaseUrl = "https://api.openai.com/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly IOpenAIAuthResolver _auth;

    // Pre-computed base URL with trailing slash stripped — avoids per-request
    // string manipulation on the hot path.
    private readonly string _baseUrl;
    private readonly OpenAIConfig _config;

    private readonly HttpClient _http;
    private readonly ILogger<OpenAILlmClient> _logger;

    public OpenAILlmClient(
        HttpClient http,
        OpenAIConfig config,
        IOpenAIAuthResolver auth,
        ILogger<OpenAILlmClient> logger)
    {
        _http = http;
        _config = config;
        _auth = auth;
        _logger = logger;
        _baseUrl = string.IsNullOrEmpty(_config.BaseUrl) ? DefaultBaseUrl : _config.BaseUrl.TrimEnd('/');
    }

    public ProviderId ProviderId { get; } = ProviderId.Create("openai");

    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<LlmEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var writer = channel.Writer;

        _ = Task.Run(async () =>
        {
            try
            {
                var apiKeyResult = await _auth.ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
                if (apiKeyResult.IsFailure)
                {
                    await writer.WriteAsync(new ErrorEvent($"Auth failed: {apiKeyResult.Error}", Kind: ProviderErrorKind.Auth), cancellationToken).ConfigureAwait(false);
                    return;
                }

                // Choose API: Responses API for o1/o3/o4-mini, Chat Completions for others
                bool useResponsesApi = IsReasoningModel(request.Model) || _config.ForceResponsesApi;
                var httpRequest = useResponsesApi
                    ? BuildResponsesRequest(request, apiKeyResult.Value)
                    : BuildChatCompletionsRequest(request, apiKeyResult.Value);

                // ROP-A ПР.3/ПР.4: per-stream tool-call id map + malformed counter.
                var chunkState = new ChunkStreamState();

                // Shared pump (ROP-A ПР.1): send/status/line-loop/error handling
                // live in SsePump; it emits exactly one FinishEvent on graceful
                // end-of-stream ([DONE] or EOF) and none on error/cancel.
                await SsePump.RunSseAsync(
                    writer, _http, httpRequest,
                    (data, token) => useResponsesApi
                        ? WriteResponsesEventsAsync(data, writer, token)
                        : WriteChatChunkEventsAsync(data, writer, chunkState, token),
                    "OpenAI API", _logger, cancellationToken,
                    onComplete: () =>
                    {
                        if (chunkState.MalformedChunks > 0)
                        {
                            _logger.LogInformation(
                                "OpenAI stream completed with {Count} malformed chunk(s) skipped",
                                chunkState.MalformedChunks);
                        }
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected on cancel
            }
            catch (Exception ex)
            {
                await writer.WriteAsync(new ErrorEvent(
                    $"Stream failed: {ex.Message}", ex.ToString(),
                    ProviderErrors.FromException(ex, cancellationToken)), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writer.TryComplete();
            }
        }, cancellationToken);

        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        // OpenAI has /v1/models endpoint, but it returns IDs only (no pricing/context info)
        // Better to return hardcoded catalog with full metadata
        var models = OpenAIModels.All;
        return Task.FromResult(Result.Success(models));
    }

    private static bool IsReasoningModel(string modelId)
    {
        return modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
               modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
               modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase) ||
               modelId.Contains("gpt-5", StringComparison.OrdinalIgnoreCase);
    }

    private HttpRequestMessage BuildChatCompletionsRequest(LlmRequest request, string apiKey)
    {
        string url = string.Concat(_baseUrl, "/chat/completions");

        var payload = new Dictionary<string, object?>(8)
        {
            ["model"] = request.Model,
            ["messages"] = BuildChatMessages(request),
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

        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return msg;
    }

    private HttpRequestMessage BuildResponsesRequest(LlmRequest request, string apiKey)
    {
        string url = string.Concat(_baseUrl, "/responses");

        var payload = new Dictionary<string, object?>(6)
        {
            ["model"] = request.Model,
            ["input"] = BuildResponsesInput(request),
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

        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return msg;
    }

    private static List<object> BuildChatMessages(LlmRequest request)
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
                    content = u.Content.OfType<LlmTextBlock>().Select(b => b.Text).FirstOrDefault() ?? ""
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

    private static List<object> BuildResponsesInput(LlmRequest request)
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
                    content = u.Content.OfType<LlmTextBlock>().Select(b => b.Text).FirstOrDefault() ?? ""
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

    /// <summary>
    ///     Parse one SSE data line and write any emitted events directly into the channel.
    ///     Chunk parsing is delegated to the shared <see cref="OpenAiWire" /> helpers
    ///     (ROP-A ПР.2) with the unified skip-and-count malformed-chunk policy (ПР.4).
    /// </summary>
    private async Task WriteChatChunkEventsAsync(string data, ChannelWriter<LlmEvent> writer, ChunkStreamState chunkState, CancellationToken ct)
    {
        foreach (var evt in OpenAiWire.TryParseChatChunkLine(data, chunkState, _logger))
        {
            await writer.WriteAsync(evt, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Parse one SSE data line and write any emitted events directly into the channel.
    ///     Streaming inside the JsonDocument's `using` scope eliminates the per-chunk .ToList()
    ///     materialization that the previous MapResponsesChunk helper required to keep JsonElement
    ///     values alive after dispose.
    /// </summary>
    private async Task WriteResponsesEventsAsync(string data, ChannelWriter<LlmEvent> writer, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            foreach (var evt in MapResponsesChunkFromDocument(doc.RootElement))
            {
                await writer.WriteAsync(evt, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse OpenAI Responses chunk: {Data}", data);
        }
    }

    private IEnumerable<LlmEvent> MapResponsesChunkFromDocument(JsonElement root)
    {
        string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        switch (type)
        {
            case "response.created":
                yield return new StepStartEvent(0);
                break;

            case "response.output_text.delta":
                if (root.TryGetProperty("delta", out var textDelta))
                {
                    string? text = textDelta.GetString();
                    if (!string.IsNullOrEmpty(text))
                        yield return new TextDeltaEvent("0", text);
                }
                break;

            case "response.reasoning.delta":
                if (root.TryGetProperty("delta", out var reasoningDelta))
                {
                    string? text = reasoningDelta.GetString();
                    if (!string.IsNullOrEmpty(text))
                        yield return new ThinkingDeltaEvent("0", text);
                }
                break;

            case "response.function_call_arguments.delta":
                string callId = root.TryGetProperty("output_index", out var oi) ? oi.GetInt32().ToString() : "0";
                if (root.TryGetProperty("delta", out var argsDelta))
                {
                    string? args = argsDelta.GetString();
                    if (!string.IsNullOrEmpty(args))
                        yield return new ToolCallDeltaEvent(callId, args);
                }
                break;

            case "response.output_item.added":
                if (root.TryGetProperty("item", out var item) &&
                    item.TryGetProperty("type", out var itemType) &&
                    itemType.GetString() == "function_call")
                {
                    string id = item.TryGetProperty("call_id", out var idEl) ? idEl.GetString() ?? "0" : "0";
                    string name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(name))
                        yield return new ToolCallStartEvent(id, name);
                }
                break;

            case "response.completed":
                var usageEl = root.TryGetProperty("response", out var resp) && resp.TryGetProperty("usage", out var u)
                    ? u
                    : default;
                var usage = usageEl.ValueKind == JsonValueKind.Object
                    ? new Usage(
                        usageEl.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0,
                        usageEl.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0,
                        usageEl.TryGetProperty("output_tokens_details", out var od) && od.TryGetProperty("reasoning_tokens", out var rt) ? rt.GetInt32() : null)
                    : null;
                // No FinishEvent here (ROP-A ПР.1): the shared pump emits exactly one.
                yield return new StepFinishEvent(0, "stop", usage);
                break;
        }
    }
}

public sealed class OpenAIConfig
{
    public string? BaseUrl { get; set; }
    public bool ForceResponsesApi { get; set; }
}

public interface IOpenAIAuthResolver
{
    public Task<Result<string>> ResolveApiKeyAsync(CancellationToken ct = default);
}

public sealed class EnvVarOpenAIAuthResolver : IOpenAIAuthResolver
{
    private const string EnvVarName = "OPENAI_API_KEY";
    private readonly string? _override;

    public EnvVarOpenAIAuthResolver(string? overrideKey = null)
    {
        _override = overrideKey;
    }

    public Task<Result<string>> ResolveApiKeyAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_override))
            return Task.FromResult(Result.Success(_override));

        string? envValue = Environment.GetEnvironmentVariable(EnvVarName);
        if (string.IsNullOrEmpty(envValue))
            return Task.FromResult(Result.Failure<string>($"Set ${EnvVarName} or pass --openai-api-key."));

        return Task.FromResult(Result.Success(envValue));
    }
}

public static class OpenAIModels
{
    public static readonly ModelInfo Gpt4o = new(
        "gpt-4o",
        "openai",
        "GPT-4o",
        128_000,
        16_384,
        false,
        true,
        true,
        new Pricing(2.5m, 10m, 1.25m),
        "openai");

    public static readonly ModelInfo Gpt4oMini = new(
        "gpt-4o-mini",
        "openai",
        "GPT-4o mini",
        128_000,
        16_384,
        false,
        true,
        true,
        new Pricing(0.15m, 0.6m, 0.075m),
        "openai");

    public static readonly ModelInfo Gpt41 = new(
        "gpt-4.1",
        "openai",
        "GPT-4.1",
        1_047_576,
        32_768,
        false,
        true,
        true,
        new Pricing(2m, 8m, 0.5m),
        "openai");

    public static readonly ModelInfo Gpt41Mini = new(
        "gpt-4.1-mini",
        "openai",
        "GPT-4.1 mini",
        1_047_576,
        32_768,
        false,
        true,
        true,
        new Pricing(0.4m, 1.6m, 0.1m),
        "openai");

    public static readonly ModelInfo O3 = new(
        "o3",
        "openai",
        "o3",
        200_000,
        100_000,
        true,
        true,
        true,
        new Pricing(10m, 40m, 2.5m),
        "openai");

    public static readonly ModelInfo O4Mini = new(
        "o4-mini",
        "openai",
        "o4-mini",
        200_000,
        100_000,
        true,
        true,
        true,
        new Pricing(1.1m, 4.4m, 0.275m),
        "openai");

    public static readonly IReadOnlyList<ModelInfo> All = new[]
    {
        Gpt4o,
        Gpt4oMini,
        Gpt41,
        Gpt41Mini,
        O3,
        O4Mini
    };
}
