using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Providers.OpenAiCompatible;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting.Tests;

/// <summary>
///     #1854: the desktop flavor's JSON-provider discovery takes its clients
///     from the shared <see cref="IHttpClientFactory" />, exactly as the CLI
///     flavor already did.
/// </summary>
/// <remarks>
///     <para>
///         Before the fix the desktop path built one <see cref="HttpClient" />
///         per <c>providers/*.json</c> file: a private connection pool each,
///         alive for the whole process, never re-resolving DNS and never
///         disposed by anybody. The CLI path had been converted to the factory
///         long before; the desktop path was the one that stayed behind.
///     </para>
///     <para>
///         The property under test is what a user of the factory can SEE: the
///         factory is asked for a client per discovered provider, and the
///         discovery asks for nothing else. The complementary half — that no
///         file outside the named owners creates a client at all — is the
///         architecture rule's job
///         (<c>HttpClientReuseRules</c> in <c>Harbor.Architecture.Tests</c>).
///     </para>
/// </remarks>
[NotInParallel("hosting")]
public class DesktopProviderHttpClientTests
{
    /// <summary>
    ///     An <see cref="IHttpClientFactory" /> that records what it was asked
    ///     for. The desktop discovery resolves the factory from the container,
    ///     so registering this instance BEFORE <c>AddHarbor</c> is what puts it
    ///     in front of the default one (<c>AddHttpClient</c> registers the
    ///     default with <c>TryAdd</c>, so the first registration wins).
    /// </summary>
    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        public List<string> Names { get; } = new();

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient();
        }
    }

    private sealed class StubAuthResolver : IAuthResolver
    {
        public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success("harbor-test-key"));
    }

    private sealed class StubModelCatalog : IModelCatalog
    {
        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(
            ProviderConfig config,
            CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>()));
    }

    [Test]
    public async Task DesktopDiscovery_TakesItsProviderClients_FromTheFactory()
    {
        var factory = new RecordingHttpClientFactory();

        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(factory);
        services.AddHarbor(new HarborComposeOptions
        {
            HarborDir = Path.Combine(
                Path.GetTempPath(), "harbor-hosting-tests", Guid.NewGuid().ToString("N")),
            DefaultStorageBackend = "memory",
            Providers = HarborProviderFlavor.Desktop,
            DesktopAuthResolver = new StubAuthResolver(),
            DesktopModelCatalog = new StubModelCatalog(),
        });

        using var sp = services.BuildServiceProvider();

        await Assert.That(factory.Names.Count).IsGreaterThan(0).Because(
            "the desktop flavor discovers the providers in providers/*.json, and every one of them "
            + "must take its client from the shared factory. Before #1854 the discovery built a "
            + "private HttpClient per file instead, so the factory was never asked for anything — "
            + "and the process carried a connection pool per provider file until it exited.");

        await Assert.That(factory.Names.All(name => name.StartsWith("provider:", StringComparison.Ordinal)))
            .IsTrue().Because(
                "the only clients the desktop flavor asks the factory for are the discovered "
                + "providers' ones, and each is named after the provider it serves — the same naming "
                + "the CLI flavor uses. Asked for: " + string.Join(", ", factory.Names));
    }
}
