using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Providers.OpenAiCompatible;

namespace Harbor.Providers.Tests;

/// <summary>
///     A model catalogue that answers with an empty list, without HTTP.
/// </summary>
/// <remarks>
///     <para>
///         This one stays local (#1825). <c>IModelCatalog</c> is declared in
///         <c>Harbor.Providers.OpenAiCompatible</c> — a product assembly. The
///         closure-neutral part of the shared kit may not reach it, because
///         that part is the one every test project can take without gaining a
///         product assembly, and a shared double for this interface would put
///         the provider assembly into every one of their closures.
///     </para>
///     <para>
///         The name follows the kit's convention rather than the old local one:
///         <c>Fake*</c> is for a double of an interface Harbor owns, and
///         <c>Stub*</c> is for a double of something outside Harbor.
///     </para>
/// </remarks>
internal sealed class FakeModelCatalog : IModelCatalog
{
    /// <summary>The shared instance — the catalogue answers the same thing every time.</summary>
    public static readonly FakeModelCatalog Instance = new();

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(
        ProviderConfig config,
        CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>()));
}
