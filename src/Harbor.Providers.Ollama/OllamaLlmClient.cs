using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
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
public sealed class OllamaLlmClient : ILlmClient
{
    private const string DefaultBaseUrl = "http://localhost:11434";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly OllamaConfig _config;

    private readonly HttpClient _http;
    private readonly ILogger<OllamaLlmClient> _logger;

    public OllamaLlmClient(HttpClient http, OllamaConfig config, ILogger<OllamaLlmClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public ProviderId ProviderId { get; } = ProviderId.Create("ollama");

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
                var httpRequest = BuildRequest(request);
                HttpResponseMessage response;

                try
                {
                    response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    await writer.WriteAsync(new ErrorEvent(
                        $"Cannot connect to Ollama at {_config.BaseUrl ?? DefaultBaseUrl}. " +
                        $"Is `ollama serve` running? Error: {ex.Message}"), cancellationToken).ConfigureAwait(false);
                    return;
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        await writer.WriteAsync(new ErrorEvent($"Ollama error {(int)response.StatusCode}: {errorBody}"), cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    // NDJSON: one JSON object per line (not SSE)
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var reader = new StreamReader(stream);

                    string? line;
                    while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        foreach (var evt in MapNdjsonChunk(line))
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

    public async Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string baseUrl = string.IsNullOrEmpty(_config.BaseUrl) ? DefaultBaseUrl : _config.BaseUrl.TrimEnd('/');
            string response = await _http.GetStringAsync($"{baseUrl}/api/tags", cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(response);
            var models = new List<ModelInfo>();
            if (doc.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var m in modelsArray.EnumerateArray())
                {
                    string? id = m.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    if (string.IsNullOrEmpty(id)) continue;

                    string displayName = id;
                    int ctx = m.TryGetProperty("model_info", out var info) &&
                              info.TryGetProperty("context_length", out var ctxEl) &&
                              ctxEl.ValueKind == JsonValueKind.Number
                        ? ctxEl.GetInt32()
                        : 4096;

                    models.Add(new ModelInfo(
                        id,
                        "ollama",
                        displayName,
                        ctx,
                        ctx,
                        false,
                        false,
                        true,
                        Pricing.Unknown,
                        "openai"));
                }
            }
            return Result.Success<IReadOnlyList<ModelInfo>>(models);
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<ModelInfo>>($"Failed to fetch Ollama models: {ex.Message}");
        }
    }

    private HttpRequestMessage BuildRequest(LlmRequest request)
    {
        string baseUrl = string.IsNullOrEmpty(_config.BaseUrl) ? DefaultBaseUrl : _config.BaseUrl.TrimEnd('/');
        string url = $"{baseUrl}/api/chat";

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = BuildMessages(request),
            ["stream"] = true,
            ["options"] = BuildOptions(request)
        };

        // keep_alive for model persistence (5 minutes by default)
        payload["keep_alive"] = _config.KeepAlive;

        if (request.Tools.Count > 0)
        {
            payload["tools"] = request.Tools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.InputSchema }
            });
        }

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        return new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static Dictionary<string, object?> BuildOptions(LlmRequest request)
    {
        var options = new Dictionary<string, object?>();
        if (request.Temperature.HasValue) options["temperature"] = request.Temperature;
        if (request.TopP.HasValue) options["top_p"] = request.TopP;
        if (request.TopK.HasValue) options["top_k"] = request.TopK;
        if (request.MaxOutputTokens.HasValue) options["num_predict"] = request.MaxOutputTokens;
        return options;
    }

    private static List<object> BuildMessages(LlmRequest request)
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
                    content = a.Content.OfType<LlmTextBlock>().Select(b => b.Text).FirstOrDefault() ?? "",
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
                    content = tr.Output
                },
                _ => new { role = "user", content = "" }
            });
        }

        return result;
    }

    private IEnumerable<LlmEvent> MapNdjsonChunk(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return MapNdjsonChunkFromDocument(doc.RootElement).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Ollama NDJSON chunk: {Line}", line);
            return Enumerable.Empty<LlmEvent>();
        }
    }

    private IEnumerable<LlmEvent> MapNdjsonChunkFromDocument(JsonElement root)
    {
        var message = root.TryGetProperty("message", out var msgEl) ? msgEl : default;

        // Text content
        if (message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
        {
            string? text = content.GetString();
            if (!string.IsNullOrEmpty(text))
                yield return new TextDeltaEvent("0", text);
        }

        // Tool calls
        if (message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("tool_calls", out var tcs) &&
            tcs.ValueKind == JsonValueKind.Array)
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

                    if (fn.TryGetProperty("arguments", out var args))
                    {
                        string? argsStr = args.ValueKind == JsonValueKind.String
                            ? args.GetString()
                            : args.GetRawText();
                        if (!string.IsNullOrEmpty(argsStr))
                            yield return new ToolCallDeltaEvent(id, argsStr);
                    }
                }
            }
        }

        // Done flag
        if (root.TryGetProperty("done", out var doneEl) && doneEl.GetBoolean())
        {
            int inputTokens = root.TryGetProperty("prompt_eval_count", out var pe) ? pe.GetInt32() : 0;
            int outputTokens = root.TryGetProperty("eval_count", out var ec) ? ec.GetInt32() : 0;

            yield return new StepFinishEvent(0, "stop", new Usage(inputTokens, outputTokens));
        }
    }
}

public sealed class OllamaConfig
{
    public string? BaseUrl { get; set; } = "http://localhost:11434";
    public string KeepAlive { get; set; } = "5m";
}
