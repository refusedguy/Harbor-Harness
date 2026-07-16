using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Providers.Anthropic;
using Harbor.Providers.OpenAI;
using Harbor.Providers.OpenAiCompatible;
namespace Harbor.Providers.Tests;
/// <summary>
///     Stub auth resolver for AnthropicLlmClient — never hits env vars or files.
/// </summary>
internal sealed class StubAnthropicAuthResolver : IAnthropicAuthResolver
{
    public static readonly StubAnthropicAuthResolver Instance = new();
    public Task<Result<string>> ResolveApiKeyAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success("test-key"));
}

/// <summary>
///     Stub auth resolver for OpenAILlmClient.
/// </summary>
internal sealed class StubOpenAIAuthResolver : IOpenAIAuthResolver
{
    public static readonly StubOpenAIAuthResolver Instance = new();
    public Task<Result<string>> ResolveApiKeyAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success("test-key"));
}

/// <summary>
///     Stub IAuthResolver for OpenAiCompatibleLlmClient — accepts any provider ID.
/// </summary>
internal sealed class StubGenericAuthResolver : IAuthResolver
{
    public static readonly StubGenericAuthResolver Instance = new();
    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success("test-key"));
}

/// <summary>
///     Stub IModelCatalog — returns an empty list without HTTP.
/// </summary>
internal sealed class StubModelCatalog : IModelCatalog
{
    public static readonly StubModelCatalog Instance = new();
    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(ProviderConfig config, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>()));
}

/// <summary>
///     Test double for HttpMessageHandler — captures requests and returns a canned response.
///     Allows tests to exercise HTTP-dependent code without real network calls.
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public List<HttpRequestMessage> CapturedRequests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CapturedRequests.Add(request);
        return Task.FromResult(_responder(request));
    }
}
