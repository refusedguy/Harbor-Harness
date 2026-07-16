using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
namespace Harbor.Providers.OpenAiCompatible;
/// <summary>
///     Generic OpenAI-compatible LLM client. Implements Strategy pattern (GOF).
///     Covers OpenRouter, DeepSeek, Groq, Together, Mistral, xAI, Ollama, LM Studio, and dozens of others.
/// </summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly IAuthResolver _auth;
    private readonly ProviderConfig _config;

    private readonly HttpClient _http;
    private readonly ILogger<OpenAiCompatibleLlmClient> _logger;
    private readonly IModelCatalog _modelCatalog;

    public OpenAiCompatibleLlmClient(
        HttpClient http,
        ProviderConfig config,
        IAuthResolver auth,
        IModelCatalog modelCatalog,
        ILogger<OpenAiCompatibleLlmClient> logger)
    {
        _http = http;
        _config = config;
        _auth = auth;
        _modelCatalog = modelCatalog;
        _logger = logger;
        ProviderId = config.GetProviderId();
    }

    public ProviderId ProviderId { get; }

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
                    await writer.WriteAsync(new ErrorEvent($"Auth failed: {apiKeyResult.Error}"), cancellationToken).ConfigureAwait(false);
                    return;
                }

                var httpRequest = BuildRequest(request, apiKeyResult.Value);
                HttpResponseMessage response;

                try
                {
                    response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await writer.WriteAsync(new ErrorEvent($"HTTP request failed: {ex.Message}", ex.ToString()), cancellationToken).ConfigureAwait(false);
                    return;
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        await writer.WriteAsync(new ErrorEvent($"API error {(int)response.StatusCode}: {errorBody}"), cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var reader = new StreamReader(stream);

                    string? line;
                    while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        if (!line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase)) continue;

                        string data = line.Substring(6);
                        if (data == "[DONE]")
                        {
                            await writer.WriteAsync(new FinishEvent(), cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        foreach (var evt in MapChunk(data))
                        {
                            await writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                await writer.WriteAsync(new FinishEvent(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected on cancel
            }
            catch (Exception ex)
            {
                await writer.WriteAsync(new ErrorEvent($"Stream failed: {ex.Message}", ex.ToString()), cancellationToken).ConfigureAwait(false);
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

    public async Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) => await _modelCatalog.GetModelsAsync(_config, cancellationToken).ConfigureAwait(false);

    private HttpRequestMessage BuildRequest(LlmRequest request, string apiKey)
    {
        string url = $"{_config.BaseUrl.TrimEnd('/')}/chat/completions";

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = BuildMessages(request),
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true }
        };

        if (request.MaxOutputTokens.HasValue)
        {
            string field = _config.Capabilities?.GetValueOrDefault("maxTokensField", "max_tokens") ?? "max_tokens";
            payload[field] = request.MaxOutputTokens;
        }

        if (request.Temperature.HasValue) payload["temperature"] = request.Temperature;
        if (request.TopP.HasValue) payload["top_p"] = request.TopP;

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

        ApplyCompatFlags(payload, request);

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        foreach ((string k, string v) in _config.Headers ?? new Dictionary<string, string>())
            msg.Headers.TryAddWithoutValidation(k, v);

        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return msg;
    }

    private void ApplyCompatFlags(Dictionary<string, object?> payload, LlmRequest request)
    {
        if (ProviderId.Value == "deepseek" && request.Model.Contains("reasoner", StringComparison.OrdinalIgnoreCase))
        {
            payload.Remove("temperature");
        }

        if (ProviderId.Value == "groq" && !payload.ContainsKey("max_tokens") && !payload.ContainsKey("max_completion_tokens"))
        {
            payload["max_tokens"] = 4096;
        }
    }

    private static List<object> BuildMessages(LlmRequest request)
    {
        var result = new List<object>();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            result.Add(new { role = "system", content = request.SystemPrompt });
        }

        foreach (var msg in request.Messages)
        {
            result.Add(BuildMessage(msg));
        }

        return result;
    }

    private static object BuildMessage(LlmMessage msg) => msg switch
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
    };

    private IEnumerable<LlmEvent> MapChunk(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            // Materialize immediately — JsonElement is invalid after JsonDocument is disposed
            return MapChunkFromDocument(doc.RootElement).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse chunk: {Data}", data);
            return Enumerable.Empty<LlmEvent>();
        }
    }

    private IEnumerable<LlmEvent> MapChunkFromDocument(JsonElement root)
    {
        var choices = root.TryGetProperty("choices", out var c) ? c.EnumerateArray().ToList() : new List<JsonElement>();
        if (choices.Count == 0)
        {
            if (root.TryGetProperty("usage", out var usage))
            {
                int inputTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                int outputTokens = usage.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetInt32() : 0;
                yield return new StepFinishEvent(0, "stop", new Usage(inputTokens, outputTokens));
            }
            yield break;
        }

        var choice = choices[0];
        var delta = choice.TryGetProperty("delta", out var d) ? d : default;
        string? finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;

        if (delta.ValueKind == JsonValueKind.Object)
        {
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                string? text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                    yield return new TextDeltaEvent("0", text);
            }

            if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
            {
                string? text = reasoning.GetString();
                if (!string.IsNullOrEmpty(text))
                    yield return new ThinkingDeltaEvent("0", text);
            }

            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    string id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                    var fn = tc.TryGetProperty("function", out var fnEl) ? fnEl : default;

                    if (fn.ValueKind == JsonValueKind.Object)
                    {
                        string? name = fn.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (!string.IsNullOrEmpty(name))
                            yield return new ToolCallStartEvent(id, name!);

                        if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                        {
                            string? argsStr = args.GetString();
                            if (!string.IsNullOrEmpty(argsStr))
                                yield return new ToolCallDeltaEvent(id, argsStr);
                        }
                    }
                }
            }
        }

        if (finishReason is not null)
        {
            var usage = root.TryGetProperty("usage", out var u) ? u : default;
            var usageObj = usage.ValueKind == JsonValueKind.Object
                ? new Usage(
                    usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0,
                    usage.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetInt32() : 0)
                : null;

            yield return new StepFinishEvent(0, finishReason, usageObj);
        }
    }
}
