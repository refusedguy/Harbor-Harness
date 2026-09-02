using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Providers.OpenAiCompatible;
namespace Harbor.Providers.Tests;

/// <summary>
///     Stub IAuthResolver for every client — accepts any provider ID, returns
///     a fixed key (ROP-A ПР.6: one auth interface for all providers).
/// </summary>
internal sealed class StubAuthResolver : IAuthResolver
{
    public static readonly StubAuthResolver Instance = new();
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
