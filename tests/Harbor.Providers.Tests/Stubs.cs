using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Providers.OpenAiCompatible;
using Harbor.TestKit;
namespace Harbor.Providers.Tests;
/// <summary>
///     Stub IModelCatalog — returns an empty list without HTTP.
/// </summary>
internal sealed class StubModelCatalog : IModelCatalog
{
    public static readonly StubModelCatalog Instance = new();
    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(ProviderConfig config, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>()));
}
