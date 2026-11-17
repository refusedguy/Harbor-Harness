using System.Diagnostics;
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
public sealed class OpenAILlmClient : ProviderLlmClientBase, ILlmClient
{
    private const string DefaultBaseUrl = "https://api.openai.com/v1";

    // Pre-computed base URL with trailing slash stripped — avoids per-request
    // string manipulation on the hot path.
    private readonly string _baseUrl;
    private readonly OpenAIConfig _config;

    public OpenAILlmClient(
        HttpClient http,
        OpenAIConfig config,
        IAuthResolver auth,
        ILogger<OpenAILlmClient> logger)
        : base(http, auth, logger, ProviderId.Create("openai"))
    {
        _config = config;
        _baseUrl = string.IsNullOrEmpty(config.BaseUrl) ? DefaultBaseUrl : config.BaseUrl.TrimEnd('/');
    }

    // --- Template Method variation points (#1499) ---------------------------

    internal override Channel<LlmEvent> CreateChannel() =>
        Channel.CreateUnbounded<LlmEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    protected override string ApiErrorLabel => "OpenAI API";

    // Native clients historically scheduled the pump with the caller token.
    protected override bool SchedulePumpWithToken => true;

    internal override HttpRequestMessage BuildRequest(LlmRequest request, string apiKey)
    {
        // Choose API: Responses API for reasoning models, Chat Completions for others.
        // Reasoning-model detection is a registered Strategy (#1498), not a
        // string-prefix test in this client.
        bool useResponsesApi = ReasoningModelStrategies.IsReasoningModel(request.Model) || _config.ForceResponsesApi;
        var httpRequest = useResponsesApi
            ? OpenAiRequestBuilder.BuildResponsesRequest(request, _baseUrl, Logger)
            : OpenAiRequestBuilder.BuildChatCompletionsRequest(request, _baseUrl, Logger);
        OpenAiRequestBuilder.AddBearerAuth(httpRequest, apiKey);
        return httpRequest;
    }

    internal override Task WriteEventsAsync(
        string data,
        ChannelWriter<LlmEvent> writer,
        ChunkStreamState chunkState,
        LlmRequest request,
        Activity? activity,
        CancellationToken token)
    {
        bool useResponsesApi = ReasoningModelStrategies.IsReasoningModel(request.Model) || _config.ForceResponsesApi;
        return useResponsesApi
            ? OpenAiResponsesMapper.WriteResponsesEventsAsync(data, writer, chunkState, Logger, token)
            : WriteChatChunkEventsAsync(data, writer, chunkState, token);
    }

    internal override void OnStreamComplete(ChunkStreamState chunkState, Activity? activity)
    {
        // #203: stream-health summary (only when noteworthy).
        if (chunkState.MalformedChunks > 0 || chunkState.RemappedToolCalls > 0 || chunkState.DroppedUsageChunks > 0)
            OpenAILlmClientLog.StreamCompleted(
                Logger, chunkState.MalformedChunks, chunkState.RemappedToolCalls, chunkState.DroppedUsageChunks);
    }

    protected override void OnFailed(Exception ex, Activity? activity) =>
        // #203 E5: fire-and-forget pump task must log inside the lambda —
        // the ErrorEvent alone is invisible in traces.
        OpenAILlmClientLog.StreamTaskFailed(Logger, ex, ex.Message);

    public override Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
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
        foreach (var evt in OpenAiWire.TryParseChatChunkLine(data, chunkState, Logger))
        {
            await writer.WriteAsync(evt, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
///     SG7: BCL <c>[LoggerMessage]</c> delegates for <see cref="OpenAILlmClient" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class OpenAILlmClientLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "OpenAI stream completed: {Malformed} malformed chunk(s) skipped, {Remaps} positional tool-call id fallback(s), {Dropped} duplicate usage chunk(s) dropped")]
    public static partial void StreamCompleted(ILogger logger, int malformed, int remaps, int dropped);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "OpenAI stream task failed: {Message}")]
    public static partial void StreamTaskFailed(ILogger logger, Exception ex, string message);
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
