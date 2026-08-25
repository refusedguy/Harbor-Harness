using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Harbor.Providers.Internal;
using Harbor.Providers.OpenAiCompatible.Compat;
using Microsoft.Extensions.Logging;
namespace Harbor.Providers.OpenAiCompatible;

public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
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
                    await writer.WriteAsync(new ErrorEvent($"Auth failed: {apiKeyResult.Error}", Kind: ProviderErrorKind.Auth), cancellationToken).ConfigureAwait(false);
                    return;
                }

                var httpRequest = BuildRequest(request, apiKeyResult.Value);

                // §OOP-001 (RESOLVED): per-StreamAsync-call parsing state —
                // tool-call id map + malformed-chunk counter (ROP-A ПР.3/ПР.4).
                var chunkState = new ChunkStreamState();

                // Shared pump (ROP-A ПР.1): send/status/line-loop/error handling
                // live in SsePump; activity observability is preserved via hooks.
                await SsePump.RunSseAsync(
                    writer, _http, httpRequest,
                    async (data, token) =>
                    {
                        foreach (var evt in OpenAiWire.TryParseChatChunkLine(data, chunkState, _logger))
                        {
                            if (evt is StepFinishEvent { Usage: not null } sfe)
                            {
                                activity?.SetTag(PromptTokensTag, sfe.Usage.InputTokens);
                                activity?.SetTag(CompletionTokensTag, sfe.Usage.OutputTokens);
                            }
                            await writer.WriteAsync(evt, token).ConfigureAwait(false);
                        }
                    },
                    "API", _logger, cancellationToken,
                    onResponse: response => activity?.SetTag("http.status_code", (int)response.StatusCode),
                    mapSendFailure: (ex, _) =>
                    {
                        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                        activity?.AddException(ex);
                        return new ErrorEvent(
                            $"HTTP request failed: {ex.Message}", ex.ToString(),
                            ProviderErrors.FromException(ex, cancellationToken));
                    },
                    onTransportError: ex =>
                    {
                        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                        activity?.AddException(ex);
                    },
                    onComplete: () =>
                    {
                        activity?.SetStatus(ActivityStatusCode.Ok);
                        if (chunkState.MalformedChunks > 0)
                        {
                            _logger.LogInformation(
                                "Stream for {Provider} completed with {Count} malformed chunk(s) skipped",
                                ProviderId.Value, chunkState.MalformedChunks);
                        }
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
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

    public async Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) => await _modelCatalog.GetModelsAsync(_config, cancellationToken).ConfigureAwait(false);

    private HttpRequestMessage BuildRequest(LlmRequest request, string apiKey)
    {
        string url = $"{_config.BaseUrl.TrimEnd('/')}/chat/completions";

        var bufferWriter = new ArrayBufferWriter<byte>(initialCapacity: 4096);
        using var writer = new Utf8JsonWriter(bufferWriter);

        writer.WriteStartObject();

        writer.WriteString("model", request.Model);
        WriteMessages(writer, request);
        writer.WriteBoolean("stream", true);

        writer.WriteStartObject("stream_options");
        writer.WriteBoolean("include_usage", true);
        writer.WriteEndObject();

        var quirks = _config.Quirks;
        bool hasQuirks = quirks is not null && quirks.Count > 0;

        if (request.MaxOutputTokens.HasValue && !QuirkOmits("max_tokens", request, quirks) && !QuirkOmits("max_completion_tokens", request, quirks))
        {
            string field = _config.Capabilities?.GetValueOrDefault("maxTokensField", "max_tokens") ?? "max_tokens";
            writer.WriteNumber(field, request.MaxOutputTokens.Value);
        }

        if (request.Temperature.HasValue && !QuirkOmits("temperature", request, quirks))
        {
            writer.WriteNumber("temperature", request.Temperature.Value);
        }

        if (request.TopP.HasValue)
        {
            writer.WriteNumber("top_p", request.TopP.Value);
        }

        if (request.Tools.Count > 0)
        {
            writer.WriteStartArray("tools");
            foreach (var t in request.Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", t.Name);
                writer.WriteString("description", t.Description);
                writer.WritePropertyName("parameters");
                t.InputSchema.WriteTo(writer);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        if (request.ToolChoice is not null)
        {
            switch (request.ToolChoice)
            {
                case ToolChoice.Auto:
                    writer.WriteString("tool_choice", "auto");
                    break;
                case ToolChoice.None:
                    writer.WriteString("tool_choice", "none");
                    break;
                case ToolChoice.Required:
                    writer.WriteString("tool_choice", "required");
                    break;
                case ToolChoice.Specific s:
                    writer.WriteStartObject("tool_choice");
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", s.ToolName);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;
            }
        }

        if (hasQuirks)
        {
            for (int i = 0; i < quirks!.Count; i++)
            {
                quirks[i].Write(writer, request);
            }
        }

        writer.WriteEndObject();
        writer.Flush();

        // ROP-A ПР.11: one copy of the payload bytes, no intermediate string.
        // The old code GetString'd the whole body (tens of KB on hot path) and
        // then re-encoded it to UTF-8 inside StringContent.
        byte[] body = bufferWriter.WrittenSpan.ToArray();

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("BuildRequest: model={Model} tools={ToolCount} toolChoice={ToolChoice} messages={MsgCount}",
                request.Model, request.Tools.Count, request.ToolChoice?.ToString() ?? "null", request.Messages.Count);
            _logger.LogDebug("BuildRequest payload:\n{Payload}", Encoding.UTF8.GetString(body));
        }

        var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(body)
        };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        foreach ((string k, string v) in _config.Headers ?? new Dictionary<string, string>())
            msg.Headers.TryAddWithoutValidation(k, v);

        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return msg;
    }

    private static bool QuirkOmits(string propertyName, LlmRequest request, IReadOnlyList<IProviderCompatFlag>? quirks)
    {
        if (quirks is null || quirks.Count == 0) return false;
        for (int i = 0; i < quirks.Count; i++)
            if (quirks[i].IsPropertyOmitted(propertyName, request)) return true;
        return false;
    }

    private static void WriteMessages(Utf8JsonWriter writer, LlmRequest request)
    {
        writer.WritePropertyName("messages");
        writer.WriteStartArray();

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", request.SystemPrompt);
            writer.WriteEndObject();
        }

        foreach (var msg in request.Messages)
        {
            WriteMessage(writer, msg);
        }

        writer.WriteEndArray();
    }

    private static void WriteMessage(Utf8JsonWriter writer, LlmMessage msg)
    {
        switch (msg)
        {
            case LlmUserMessage u:
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteString("content", u.Content.OfType<LlmTextBlock>().Select(b => b.Text).FirstOrDefault() ?? "");
                writer.WriteEndObject();
                break;

            case LlmAssistantMessage a:
                writer.WriteStartObject();
                writer.WriteString("role", "assistant");

                var textBlock = a.Content.OfType<LlmTextBlock>().Select(b => b.Text).FirstOrDefault();
                if (textBlock is not null)
                {
                    writer.WriteString("content", textBlock);
                }

                writer.WriteStartArray("tool_calls");
                foreach (var tc in a.Content.OfType<LlmToolCallBlock>())
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", tc.Id);
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", tc.Name);
                    writer.WritePropertyName("arguments");
                    writer.WriteStringValue(tc.Arguments.GetRawText());
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
                break;

            case LlmToolResultMessage tr:
                writer.WriteStartObject();
                writer.WriteString("role", "tool");
                writer.WriteString("tool_call_id", tr.ToolCallId);
                writer.WriteString("content", tr.Output);
                writer.WriteEndObject();
                break;

            default:
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteString("content", "");
                writer.WriteEndObject();
                break;
        }
    }
}

