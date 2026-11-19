using System.Diagnostics;
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
///     Orchestration only: request building lives in <see cref="AnthropicRequestBuilder" />,
///     event mapping in <see cref="AnthropicEventMapper" />, transport in <see cref="SsePump" />.
///     Special features beyond OpenAI-compat:
///     - cache_control (ephemeral, 1h TTL)
///     - extended thinking (budget_tokens)
///     - fine-grained tool streaming beta
///     - interleaved thinking beta
///     - tool_result as content block in user message
///     - system as separate field (not message)
/// </summary>
public sealed class AnthropicLlmClient : ProviderLlmClientBase, ILlmClient
{
    private const string DefaultBaseUrl = "https://api.anthropic.com/v1";

    // Pre-computed base URL with trailing slash stripped — avoids per-request
    // string manipulation and conditional logic on the hot path.
    private readonly string _baseUrl;
    private readonly AnthropicConfig _config;
    private readonly IModelCatalog _modelCatalog;

    public AnthropicLlmClient(
        HttpClient http,
        AnthropicConfig config,
        IAuthResolver auth,
        IModelCatalog modelCatalog,
        ILogger<AnthropicLlmClient> logger)
        : base(http, auth, logger, ProviderId.Create("anthropic"))
    {
        _config = config;
        _modelCatalog = modelCatalog;
        _baseUrl = string.IsNullOrEmpty(config.BaseUrl) ? DefaultBaseUrl : config.BaseUrl.TrimEnd('/');
    }

    // --- Template Method variation points (#1499) ---------------------------

    internal override Channel<LlmEvent> CreateChannel() =>
        Channel.CreateUnbounded<LlmEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    protected override string ApiErrorLabel => "Anthropic API";

    // Native clients historically scheduled the pump with the caller token.
    protected override bool SchedulePumpWithToken => true;

    internal override HttpRequestMessage BuildRequest(LlmRequest request, string apiKey) =>
        AnthropicRequestBuilder.BuildRequest(
            request, _baseUrl, apiKey, _config.ApiVersion, _config.BetaFeatures);

    internal override Task WriteEventsAsync(
        string data,
        ChannelWriter<LlmEvent> writer,
        ChunkStreamState chunkState,
        LlmRequest request,
        Activity? activity,
        CancellationToken token) =>
        AnthropicEventMapper.WriteAnthropicEventsAsync(data, writer, chunkState, Logger, token);

    internal override void OnStreamComplete(ChunkStreamState chunkState, Activity? activity)
    {
        // #203: stream-health summary (only when noteworthy).
        if (chunkState.MalformedChunks > 0 || chunkState.RemappedToolCalls > 0)
            AnthropicLlmClientLog.StreamCompleted(Logger, chunkState.MalformedChunks, chunkState.RemappedToolCalls);
    }

    protected override void OnFailed(Exception ex, Activity? activity) =>
        // #203 E5: fire-and-forget pump task must log inside the lambda —
        // the ErrorEvent alone is invisible in traces.
        AnthropicLlmClientLog.StreamTaskFailed(Logger, ex, ex.Message);

    // #3801: Anthropic has no /models endpoint. The model list is the one
    // already loaded from providers/anthropic.json by the registration path,
    // so there is no C# copy left here to drift out of sync with that file.
    public override Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) =>
        _modelCatalog.GetModelsAsync(cancellationToken);
}

/// <summary>
///     SG7: BCL <c>[LoggerMessage]</c> delegates for <see cref="AnthropicLlmClient" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class AnthropicLlmClientLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Anthropic stream completed: {Malformed} malformed event(s) skipped, {Remaps} positional tool-call id fallback(s)")]
    public static partial void StreamCompleted(ILogger logger, int malformed, int remaps);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Anthropic stream task failed: {Message}")]
    public static partial void StreamTaskFailed(ILogger logger, Exception ex, string message);
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
