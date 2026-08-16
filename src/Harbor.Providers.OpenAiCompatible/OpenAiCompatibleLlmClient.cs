using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
namespace Harbor.Providers.OpenAiCompatible;

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

    private static readonly ActivitySource Source = new("Harbor");
    private const string ProviderNameTag = "gen_ai.provider.name";
    private const string RequestModelTag = "gen_ai.request.model";
    private const string PromptTokensTag = "gen_ai.prompt.tokens";
    private const string CompletionTokensTag = "gen_ai.completion.tokens";

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
            using var activity = Source.StartActivity("LLM.Call");
            activity?.SetTag(ProviderNameTag, ProviderId.Value);
            activity?.SetTag(RequestModelTag, request.Model);
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
                    activity?.SetTag("http.status_code", (int)response.StatusCode);
                }
                catch (Exception ex)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity?.AddException(ex);
                    await writer.WriteAsync(new ErrorEvent($"HTTP request failed: {ex.Message}", ex.ToString()), cancellationToken).ConfigureAwait(false);
                    return;
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        activity?.SetTag("http.status_code", (int)response.StatusCode);
                        string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        await writer.WriteAsync(new ErrorEvent($"API error {(int)response.StatusCode}: {errorBody}"), cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var reader = new StreamReader(stream);

                    // §OOP-001 (RESOLVED): the tool-call index→id map is per-StreamAsync-call
                    // state. It used to be a shared instance field on the client (a singleton),
                    // which raced when two sessions shared one provider client. It now lives
                    // as a local captured by the MapChunk closure, so concurrent StreamAsync
                    // calls each get their own map without any synchronisation.
                    var indexToId = new Dictionary<int, string>(capacity: 4);

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

                        foreach (var evt in OpenAiSseParser.ParseChunk(data.AsSpan(), indexToId, _logger))
                        {
                            if (evt is StepFinishEvent { Usage: not null } sfe)
                            {
                                activity?.SetTag(PromptTokensTag, sfe.Usage.InputTokens);
                                activity?.SetTag(CompletionTokensTag, sfe.Usage.OutputTokens);
                            }
                            await writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                activity?.SetStatus(ActivityStatusCode.Ok);
                await writer.WriteAsync(new FinishEvent(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
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
        // TODO(principles)[PERF, байтоебля, FP]: BuildRequest конструирует
        // Dictionary<string,object?> + анонимные типы + JsonSerializer.Serialize(payload, JsonOptions)
        // на каждый вызов. Это: (1) reflection-based serialize (non-AOT-friendly), (2)
        // боксинг value types в object, (3) closure над request. Для hot path правильнее
        // использовать Utf8JsonWriter + ArrayPool<byte> и писать JSON напрямую в stream
        // без intermediate Dictionary. См. аудит §PERF-002, §FP-001 (mutability).
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

        _logger.LogDebug("BuildRequest: model={Model} tools={ToolCount} toolChoice={ToolChoice} messages={MsgCount}",
            request.Model, request.Tools.Count, request.ToolChoice?.ToString() ?? "null", request.Messages.Count);
        _logger.LogDebug("BuildRequest payload:\n{Payload}", json);
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
        // §OOP-002 (RESOLVED): provider-specific quirks are now pluggable via
        // IProviderCompatFlag (Strategy pattern). The client no longer hardcodes
        // `ProviderId.Value == "deepseek"` / `"groq"` switches — it just iterates
        // the quirks attached to ProviderConfig.Quirks (populated by the
        // registration code from ProviderCompatFlags.For(providerId)). New
        // providers with quirks get their own IProviderCompatFlag implementation
        // without this client being touched (Open/Closed).
        var quirks = _config.Quirks;
        if (quirks is null || quirks.Count == 0)
            return;

        for (int i = 0; i < quirks.Count; i++)
        {
            quirks[i].Apply(payload, request);
        }
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
}

