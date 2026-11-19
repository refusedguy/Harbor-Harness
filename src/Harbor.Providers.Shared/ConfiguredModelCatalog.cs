// Shared source: compiled INTO Harbor.Providers.Anthropic via a <Compile Include>
// link item, exactly like ProviderLlmClientBase.cs and SsePump.cs. Native
// providers have no /models endpoint, so the only honest catalogue is the
// model list already loaded from their providers/*.json config (#3801). Keeping
// the seam and its config-backed implementation here avoids a ProjectReference
// from one Infrastructure provider assembly to another (§ARCH-003).
//
// AOT: no reflection, no JsonSerializer, no dynamic code. The ModelInfo list is
// materialised once by ProviderConfig's source-generated deserializer before it
// reaches this type; GetModelsAsync only hands the existing list back.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;

namespace Harbor.Providers.Internal;

/// <summary>
///     The model-catalogue seam a native provider asks for its model list. This
///     is deliberately a different seam from the OpenAI-compatible
///     <c>IModelCatalog</c>: that one takes a <c>ProviderConfig</c> and may fall
///     back to HTTP and a cache, while this one is already bound to the config
///     loaded at registration time. The two cannot share a declaration without
///     a ProjectReference between provider assemblies, which §ARCH-003 forbids.
/// </summary>
public interface IModelCatalog
{
    Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken ct = default);
}

/// <summary>
///     A catalogue over a fixed, already-loaded model list. This is the CONFIG
///     branch of <c>DynamicModelCatalog</c> (<c>return config.Models</c>, never
///     call <c>/models</c>) packaged for a native client. The list is captured
///     once at construction and never mutated, so the client's answer is exactly
///     what the provider JSON declared (#3801).
/// </summary>
public sealed class ConfiguredModelCatalog : IModelCatalog
{
    private readonly IReadOnlyList<ModelInfo> _models;

    public ConfiguredModelCatalog(IReadOnlyList<ModelInfo> models) => _models = models;

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success(_models));
}
