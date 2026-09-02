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
namespace Harbor.Providers.Anthropic;
/// <summary>
///     Native Anthropic Messages API client.
///     Implements Strategy pattern (GOF) via ILlmClient.
///     Special features beyond OpenAI-compat:
///     - cache_control (ephemeral, 1h TTL)
///     - extended thinking (budget_tokens)
///     - fine-grained tool streaming beta
///     - interleaved thinking beta
///     - tool_result as content block in user message
///     - system as separate field (not message)
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    private const string DefaultBaseUrl = "https://api.anthropic.com/v1";
    private const string DefaultApiVersion = "2023-06-01";
    private const string DefaultBetaFeatures = "interleaved-thinking-2025-05-14,fine-grained-tool-streaming-2025-05-14";
    private const int ImageTokenEstimate = 1200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly IAuthResolver _auth;

    // Pre-computed base URL with trailing slash stripped — avoids per-request
    // string manipulation and conditional logic on the hot path.
    private readonly string _baseUrl;
    private readonly AnthropicConfig _config;

    private readonly HttpClient _http;
    private readonly ILogger<AnthropicLlmClient> _logger;

    public AnthropicLlmClient(
        HttpClient http,
        AnthropicConfig config,
        IAuthResolver auth,
        ILogger<AnthropicLlmClient> logger)
    {
        _http = http;
        _config = config;
        _auth = auth;
        _logger = logger;
        _baseUrl = string.IsNullOrEmpty(_config.BaseUrl) ? DefaultBaseUrl : _config.BaseUrl.TrimEnd('/');
    }

    public ProviderId ProviderId { get; } = ProviderId.Create("anthropic");

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
                var apiKeyResult = await _auth.ResolveApiKeyAsync(ProviderId.Value, cancellationToken).ConfigureAwait(false);
                if (apiKeyResult.IsFailure)
                {
                    await writer.WriteAsync(new ErrorEvent($"Auth failed: {apiKeyResult.Error}", Kind: ProviderErrorKind.Auth), cancellationToken).ConfigureAwait(false);
                    return;
                }

                var httpRequest = BuildRequest(request, apiKeyResult.Value);

                // Shared pump (ROP-A ПР.1): exactly one FinishEvent on graceful
                // end-of-stream; parsers never emit FinishEvent themselves.
                await SsePump.RunSseAsync(
                    writer, _http, httpRequest,
                    (data, token) => WriteAnthropicEventsAsync(data, writer, token),
                    "Anthropic API", _logger, cancellationToken).ConfigureAwait(false);
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
        // Anthropic doesn't have a /models endpoint — return hardcoded catalog
        var models = AnthropicModels.All;
        return Task.FromResult(Result.Success(models));
    }

    private HttpRequestMessage BuildRequest(LlmRequest request, string apiKey)
    {
        string url = string.Concat(_baseUrl, "/messages");

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
        msg.Headers.TryAddWithoutValidation("anthropic-version", _config.ApiVersion ?? DefaultApiVersion);
        msg.Headers.TryAddWithoutValidation("anthropic-beta", _config.BetaFeatures ?? DefaultBetaFeatures);

        return msg;
    }

    private static List<object> BuildMessages(LlmRequest request)
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

    private static object ConvertContentBlocks(IReadOnlyList<LlmContentBlock> blocks)
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

    /// <summary>
    ///     Parse one SSE data line and write any emitted events directly into the
    ///     channel. Streaming inside the JsonDocument's `using` scope eliminates the
    ///     per-chunk .ToList() materialization that the previous MapAnthropicEvent
    ///     helper required to keep JsonElement values alive after dispose.
    /// </summary>
    private async Task WriteAnthropicEventsAsync(string data, ChannelWriter<LlmEvent> writer, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            foreach (var evt in MapAnthropicEventFromDocument(doc.RootElement))
            {
                await writer.WriteAsync(evt, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Anthropic event: {Data}", data);
        }
    }

    private IEnumerable<LlmEvent> MapAnthropicEventFromDocument(JsonElement root)
    {
        string? type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

        switch (type)
        {
            case "message_start":
                yield return new StepStartEvent(0);
                break;

            case "content_block_start":
                if (root.TryGetProperty("content_block", out var block) &&
                    block.TryGetProperty("type", out var blockType))
                {
                    string id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "0" : "0";
                    string? blockTypeStr = blockType.GetString();

                    if (blockTypeStr == "text")
                        yield return new TextStartEvent(id);
                    else if (blockTypeStr == "thinking")
                        yield return new ThinkingStartEvent(id);
                    else if (blockTypeStr == "tool_use")
                    {
                        string name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                        yield return new ToolCallStartEvent(id, name);
                    }
                }
                break;

            case "content_block_delta":
                if (root.TryGetProperty("delta", out var delta))
                {
                    string? deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                    string id = "0"; // Anthropic doesn't include id in deltas; track via index

                    switch (deltaType)
                    {
                        case "text_delta":
                            if (delta.TryGetProperty("text", out var textEl))
                            {
                                string? text = textEl.GetString();
                                if (!string.IsNullOrEmpty(text))
                                    yield return new TextDeltaEvent(id, text);
                            }
                            break;

                        case "thinking_delta":
                            if (delta.TryGetProperty("thinking", out var thEl))
                            {
                                string? text = thEl.GetString();
                                if (!string.IsNullOrEmpty(text))
                                    yield return new ThinkingDeltaEvent(id, text);
                            }
                            break;

                        case "input_json_delta":
                            if (delta.TryGetProperty("partial_json", out var pjEl))
                            {
                                string? text = pjEl.GetString();
                                if (!string.IsNullOrEmpty(text))
                                    yield return new ToolCallDeltaEvent(id, text);
                            }
                            break;
                    }
                }
                break;

            case "message_delta":
                if (root.TryGetProperty("delta", out var msgDelta) &&
                    msgDelta.TryGetProperty("stop_reason", out var stopEl))
                {
                    string? stopReason = stopEl.GetString();
                    var usage = root.TryGetProperty("usage", out var usageEl)
                        ? new Usage(
                            usageEl.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0,
                            usageEl.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0,
                            null,
                            usageEl.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : null,
                            usageEl.TryGetProperty("cache_creation_input_tokens", out var cw) ? cw.GetInt32() : null)
                        : null;

                    yield return new StepFinishEvent(0, stopReason ?? "stop", usage);
                }
                break;

            case "message_stop":
                // No FinishEvent here (ROP-A ПР.1): the shared pump emits exactly one.
                break;
        }
    }
}

/// <summary>
///     Configuration for AnthropicLlmClient.
/// </summary>
public sealed class AnthropicConfig
{
    public string? BaseUrl { get; set; }
    public string? ApiVersion { get; set; }
    public string? BetaFeatures { get; set; }
}

/// <summary>
///     Hardcoded catalog of Anthropic models (no /models endpoint).
/// </summary>
public static class AnthropicModels
{
    public static readonly ModelInfo ClaudeOpus4 = new(
        "claude-opus-4-20250514",
        "anthropic",
        "Claude Opus 4",
        200_000,
        32_000,
        true,
        true,
        true,
        new Pricing(15m, 75m, 1.5m, 18.75m),
        "anthropic");

    public static readonly ModelInfo ClaudeSonnet4 = new(
        "claude-sonnet-4-20250514",
        "anthropic",
        "Claude Sonnet 4",
        200_000,
        16_000,
        true,
        true,
        true,
        new Pricing(3m, 15m, 0.3m, 3.75m),
        "anthropic");

    public static readonly ModelInfo ClaudeSonnet35 = new(
        "claude-3-5-sonnet-20241022",
        "anthropic",
        "Claude 3.5 Sonnet",
        200_000,
        8192,
        false,
        true,
        true,
        new Pricing(3m, 15m, 0.3m, 3.75m),
        "anthropic");

    public static readonly ModelInfo ClaudeHaiku35 = new(
        "claude-3-5-haiku-20241022",
        "anthropic",
        "Claude 3.5 Haiku",
        200_000,
        8192,
        false,
        true,
        true,
        new Pricing(0.8m, 4m, 0.08m, 1m),
        "anthropic");

    public static readonly IReadOnlyList<ModelInfo> All = new[]
    {
        ClaudeOpus4,
        ClaudeSonnet4,
        ClaudeSonnet35,
        ClaudeHaiku35
    };
}
