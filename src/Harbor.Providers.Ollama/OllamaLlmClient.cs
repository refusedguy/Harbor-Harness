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

    // Pre-computed base URL — avoids per-request string manipulation.
    private readonly string _baseUrl;
    private readonly OllamaConfig _config;

    private readonly HttpClient _http;
    private readonly ILogger<OllamaLlmClient> _logger;

    public OllamaLlmClient(HttpClient http, OllamaConfig config, ILogger<OllamaLlmClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _baseUrl = string.IsNullOrEmpty(_config.BaseUrl) ? DefaultBaseUrl : _config.BaseUrl.TrimEnd('/');
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

                // ROP-A ПР.3: per-stream tool-call index→id map for stable ids.
                var indexToId = new Dictionary<int, string>(capacity: 4);

                // Shared pump (ROP-A ПР.1) in raw-line (NDJSON) mode: Ollama has
                // no "data:" prefix and no [DONE] sentinel — every line is a
                // JSON chunk, EOF is the end of stream.
                await SsePump.RunAsync(
                    writer, _http, httpRequest,
                    async (line, token) =>
                    {
                        await WriteNdjsonEventsAsync(line, writer, indexToId, token).ConfigureAwait(false);
                        return true;
                    },
                    "Ollama", _logger, cancellationToken,
                    mapSendFailure: (ex, token) => ex is HttpRequestException hre
                        ? new ErrorEvent(
                            $"Cannot connect to Ollama at {_config.BaseUrl ?? DefaultBaseUrl}. " +
                            $"Is `ollama serve` running? Error: {hre.Message}",
                            Kind: ProviderErrorKind.Network)
                        : new ErrorEvent(
                            $"HTTP request failed: {ex.Message}", ex.ToString(),
                            ProviderErrors.FromException(ex, token))).ConfigureAwait(false);
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

    public async Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string response = await _http.GetStringAsync(string.Concat(_baseUrl, "/api/tags"), cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(response);
            // Pre-size the models list only when the models array is present and has a known count.
            List<ModelInfo>? models = null;
            if (doc.RootElement.TryGetProperty("models", out var modelsArray) && modelsArray.ValueKind == JsonValueKind.Array)
            {
                models = new List<ModelInfo>(modelsArray.GetArrayLength());
                foreach (var m in modelsArray.EnumerateArray())
                {
                    string? id = m.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    if (string.IsNullOrEmpty(id)) continue;

                    int ctx = m.TryGetProperty("model_info", out var info) &&
                              info.TryGetProperty("context_length", out var ctxEl) &&
                              ctxEl.ValueKind == JsonValueKind.Number
                        ? ctxEl.GetInt32()
                        : 4096;

                    models.Add(new ModelInfo(
                        id,
                        "ollama",
                        id,
                        ctx,
                        ctx,
                        false,
                        false,
                        true,
                        Pricing.Unknown,
                        "openai"));
                }
            }
            return Result.Success<IReadOnlyList<ModelInfo>>((IReadOnlyList<ModelInfo>?)models ?? Array.Empty<ModelInfo>());
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<ModelInfo>>($"Failed to fetch Ollama models: {ex.Message}");
        }
    }

    private HttpRequestMessage BuildRequest(LlmRequest request)
    {
        string url = string.Concat(_baseUrl, "/api/chat");

        var payload = new Dictionary<string, object?>(6)
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

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(jsonBytes)
        };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return msg;
    }

    private static Dictionary<string, object?> BuildOptions(LlmRequest request)
    {
        // Pre-size for the maximum known keys: temperature, top_p, top_k, num_predict.
        var options = new Dictionary<string, object?>(4);
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

    /// <summary>
    ///     Parse one NDJSON line and write any emitted events directly into the channel.
    ///     Streaming inside the JsonDocument's `using` scope eliminates the per-chunk .ToList()
    ///     materialization that the previous MapNdjsonChunk helper required to keep JsonElement
    ///     values alive after dispose.
    /// </summary>
    private async Task WriteNdjsonEventsAsync(string line, ChannelWriter<LlmEvent> writer, Dictionary<int, string> indexToId, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            foreach (var evt in MapNdjsonChunkFromDocument(doc.RootElement, indexToId))
            {
                await writer.WriteAsync(evt, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Ollama NDJSON chunk: {Line}", line);
        }
    }

    private IEnumerable<LlmEvent> MapNdjsonChunkFromDocument(JsonElement root, Dictionary<int, string> indexToId)
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
                // Stable id (ROP-A ПР.3): wire id → remembered id → positional
                // fallback. Never a fresh Guid per chunk — that broke coalescing.
                int index = tc.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number
                    ? idxEl.GetInt32()
                    : 0;
                string id = tc.TryGetProperty("id", out var idEl) && !string.IsNullOrEmpty(idEl.GetString())
                    ? idEl.GetString()!
                    : indexToId.GetValueOrDefault(index) ?? $"tc{index}";
                indexToId[index] = id;

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
