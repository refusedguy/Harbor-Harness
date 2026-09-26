using System.Runtime.CompilerServices;
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
///     Orchestration only: request building lives in <see cref="OpenAiRequestBuilder" />,
///     Responses-API event mapping in <see cref="OpenAiResponsesMapper" />, Chat chunk
///     parsing in shared <see cref="OpenAiWire" />, transport in <see cref="SsePump" />.
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

    private readonly IAuthResolver _auth;

    // Pre-computed base URL with trailing slash stripped — avoids per-request
    // string manipulation on the hot path.
    private readonly string _baseUrl;
    private readonly OpenAIConfig _config;

    private readonly HttpClient _http;
    private readonly ILogger<OpenAILlmClient> _logger;

    public OpenAILlmClient(
        HttpClient http,
        OpenAIConfig config,
        IAuthResolver auth,
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
                var apiKeyResult = await _auth.ResolveApiKeyAsync(ProviderId.Value, cancellationToken).ConfigureAwait(false);
                if (apiKeyResult.IsFailure)
                {
                    await writer.WriteAsync(new ErrorEvent($"Auth failed: {apiKeyResult.Error}", Kind: ProviderErrorKind.Auth), cancellationToken).ConfigureAwait(false);
                    return;
                }

                // Choose API: Responses API for o1/o3/o4-mini, Chat Completions for others
                bool useResponsesApi = OpenAiRequestBuilder.IsReasoningModel(request.Model) || _config.ForceResponsesApi;
                var httpRequest = useResponsesApi
                    ? OpenAiRequestBuilder.BuildResponsesRequest(request, _baseUrl, _logger)
                    : OpenAiRequestBuilder.BuildChatCompletionsRequest(request, _baseUrl, _logger);
                OpenAiRequestBuilder.AddBearerAuth(httpRequest, apiKeyResult.Value);

                // ROP-A ПР.3/ПР.4: per-stream tool-call id map + malformed counter.
                var chunkState = new ChunkStreamState();

                // Shared pump (ROP-A ПР.1): send/status/line-loop/error handling
                // live in SsePump; it emits exactly one FinishEvent on graceful
                // end-of-stream ([DONE] or EOF) and none on error/cancel.
                await SsePump.RunSseAsync(
                    writer, _http, httpRequest,
                    (data, token) => useResponsesApi
                        ? OpenAiResponsesMapper.WriteResponsesEventsAsync(data, writer, _logger, token)
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
}

public sealed class OpenAIConfig
{
    public string? BaseUrl { get; set; }
    public bool ForceResponsesApi { get; set; }
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
