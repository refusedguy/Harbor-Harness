using System.Runtime.CompilerServices;
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
public sealed class AnthropicLlmClient : ILlmClient
{
    private const string DefaultBaseUrl = "https://api.anthropic.com/v1";

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

                var httpRequest = AnthropicRequestBuilder.BuildRequest(
                    request, _baseUrl, apiKeyResult.Value, _config.ApiVersion, _config.BetaFeatures);

                // Shared pump (ROP-A ПР.1): exactly one FinishEvent on graceful
                // end-of-stream; parsers never emit FinishEvent themselves.
                await SsePump.RunSseAsync(
                    writer, _http, httpRequest,
                    (data, token) => AnthropicEventMapper.WriteAnthropicEventsAsync(data, writer, _logger, token),
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
