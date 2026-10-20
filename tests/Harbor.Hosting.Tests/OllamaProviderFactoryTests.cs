// OllamaProviderFactoryTests.cs — the #1501 guard.
//
// WHAT THE ISSUE REPORTED
// -----------------------
// `src/Harbor.Hosting/Modules/ProviderFactories.cs` carried TWO factories for
// the SAME provider id: `OllamaProviderFactory` (the CLI flavor's — its HTTP
// client came from the named "ollama" client of IHttpClientFactory) and
// `DesktopOllamaProviderFactory` (the desktop flavor's — one process-wide
// client built from OLLAMA_HOST). Both declared `ProviderId == "ollama"` and
// both built the same OllamaLlmClient; they differed in ONE thing: where the
// HTTP client came from.
//
// WHY THAT WAS A DEFECT
// ---------------------
// The provider registry is keyed by provider id, so two factories for one id
// mean the second registration SILENTLY replaces the first — and a choice that
// belongs to the composition (which flavor is running) was spread over two
// classes instead of being one parameter. The merge keeps the difference and
// loses the duplication: ONE factory, the client SOURCE as its parameter.
//
// WHAT THE TESTS PIN
// ------------------
//   1. The factory declares the ollama provider id and builds an ollama client.
//   2. The factory takes its HTTP client from the SOURCE it was given, once per
//      client — the seam that replaced the second class.
//   3. The CLI flavor still takes its ollama client from the factory's named
//      "ollama" client. (The desktop half — a client that is NOT from the
//      factory — is pinned by DesktopProviderHttpClientTests.)
//   4. Each flavor's registry holds exactly the ids that flavor registers: the
//      discovered ones plus the native ones. The merge moved a class; it must
//      not have moved a single id.
//   5. ONE factory per provider id — the property the merge exists to restore.
//
// NotInParallel("hosting"): the bucket the other hosting suites share — they
// read the same providers/ directory the discovery path walks, and one of them
// drops a file into it.

using System.Reflection;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Application.Configuration;
using Harbor.Providers.OpenAiCompatible;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Hosting.Tests;

/// <summary>
///     #1501: one Ollama factory for both flavors, and the registry that
///     survives the merge unchanged.
/// </summary>
[NotInParallel("hosting")]
public class OllamaProviderFactoryTests
{
    // ── the merged factory ───────────────────────────────────────────────

    [Test]
    public async Task TheOllamaFactory_DeclaresTheOllamaProviderId_AndBuildsAnOllamaClient()
    {
        var factory = new OllamaProviderFactory(() => new HttpClient());

        await Assert.That(factory.ProviderId.Value).IsEqualTo("ollama")
            .Because("#1501: two classes claimed the provider id 'ollama', and the registry is keyed by "
                     + "that id — so the second registration silently replaced the first. There is ONE "
                     + "ollama factory now.");

        ILlmClient client = factory.CreateClient(NullLoggerFactory.Instance);

        await Assert.That(client.ProviderId.Value).IsEqualTo("ollama")
            .Because("the client the factory builds must be an ollama client, whichever source the HTTP "
                     + "client came from.");
    }

    [Test]
    public async Task TheFactory_TakesItsHttpClient_FromTheSource_OncePerClient()
    {
        int asked = 0;
        HttpClient shared = new();
        var factory = new OllamaProviderFactory(() =>
        {
            asked++;
            return shared;
        });

        factory.CreateClient(NullLoggerFactory.Instance);
        factory.CreateClient(NullLoggerFactory.Instance);

        await Assert.That(asked).IsEqualTo(2)
            .Because("the source is asked once per client, and the client it hands over is the one the "
                     + "provider client is built on. The CLI flavor's source mints a named factory client "
                     + "per call; the desktop flavor's source returns its one process-wide client. A "
                     + "factory that ignored the source would take that choice away from the caller, and "
                     + "a client built INSIDE the source would be minted per call (#1854).");
    }

    // ── the flavors ──────────────────────────────────────────────────────

    [Test]
    public async Task TheCliFlavor_TakesItsOllamaClient_FromTheNamedFactoryClient()
    {
        var factory = new RecordingHttpClientFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(factory);
        services.AddHarbor(new HarborComposeOptions { HarborDir = TempHarborDir(), DefaultStorageBackend = "memory" });
        using var sp = services.BuildServiceProvider();

        var registry = sp.GetRequiredService<IProviderRegistry>();
        var client = registry.GetClient(ProviderId.Create("ollama"));

        await Assert.That(client.IsSuccess).IsTrue()
            .Because("the ollama provider must resolve in the CLI flavor; the registry instantiates it "
                     + "lazily, and this is the call that does it.");

        await Assert.That(factory.Names).Contains("ollama")
            .Because("the CLI flavor's ollama client comes from the factory's named \"ollama\" client — "
                     + "the half of the merge that had to survive it. Asked for: "
                     + string.Join(", ", factory.Names));
    }

    [Test]
    public async Task TheCliFlavor_RegistersTheDiscoveredProviders_AndTheNativeOnes_AndNothingElse()
    {
        string harborDir = TempHarborDir();
        using var sp = CompositionFixtures.Compose(new HarborComposeOptions
        {
            HarborDir = harborDir,
            DefaultStorageBackend = "memory",
        });

        string[] expected = ExpectedCliIds(sp, harborDir);
        string[] actual = RegisteredIds(sp);

        await Assert.That(actual).IsEquivalentTo(expected)
            .Because("the CLI flavor registers the providers the discovery contributes plus the native "
                     + "ones (ollama, and anthropic/openai when the build carries them). A provider that "
                     + "stops registering, and one that starts, both fail here. Expected: "
                     + string.Join(", ", expected) + ". Actual: " + string.Join(", ", actual));
    }

    [Test]
    public async Task TheDesktopFlavor_RegistersOllama_AndTheDiscoveredProviders_AndNothingElse()
    {
        string harborDir = TempHarborDir();
        var resolver = new FixedKeyResolver();
        var catalog = new EmptyCatalog();
        using var sp = CompositionFixtures.Compose(new HarborComposeOptions
        {
            HarborDir = harborDir,
            DefaultStorageBackend = "memory",
            Providers = HarborProviderFlavor.Desktop,
            DesktopAuthResolver = resolver,
            DesktopModelCatalog = catalog,
        });

        string[] expected = ExpectedDesktopIds(sp, resolver, catalog);
        string[] actual = RegisteredIds(sp);

        await Assert.That(actual).IsEquivalentTo(expected)
            .Because("the desktop flavor registers ollama natively and the discovered providers beside "
                     + "it. The merge moved the ollama factory; it must not have moved a single id. "
                     + "Expected: " + string.Join(", ", expected)
                     + ". Actual: " + string.Join(", ", actual));
    }

    // ── one factory per provider id ──────────────────────────────────────

    [Test]
    public async Task OneFactory_DeclaresEachProviderId()
    {
        IProviderFactory[] factories = ProductProviderFactories();

        await Assert.That(factories.Length).IsGreaterThanOrEqualTo(1)
            .Because("the census must find the product's provider factories at all — a census that finds "
                     + "none reports 'no duplicates' for the wrong reason.");

        string[] ids = factories.Select(factory => factory.ProviderId.Value).ToArray();

        await Assert.That(ids).Contains("ollama")
            .Because("the ollama factory is the subject of the merge; if it vanished from the assembly, "
                     + "the census below would be a census of the wrong set.");

        string[] duplicates = ids
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(duplicates).IsEmpty()
            .Because("two factories for one provider id is the defect #1501 reported: the registry is "
                     + "keyed by that id, so the second registration silently replaces the first, and the "
                     + "flavor choice leaks into the class list. One id, one factory. Duplicated: "
                     + string.Join(", ", duplicates));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-hosting-tests", Guid.NewGuid().ToString("N"));

    /// <summary>The provider ids the composed registry holds.</summary>
    private static string[] RegisteredIds(IServiceProvider sp) =>
        sp.GetRequiredService<IProviderRegistry>()
            .GetRegisteredProviderIds()
            .Select(id => id.Value)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    ///     The ids the CLI flavor registers: what the discovery contributes
    ///     (the same code path, on the same machine, over the same providers/
    ///     directories) plus the native ones.
    /// </summary>
    private static string[] ExpectedCliIds(IServiceProvider sp, string harborDir)
    {
        var discovered = new RecordingProviderBuilder();
        JsonProviderDiscovery.RegisterJsonProviders(
            discovered,
            sp.GetRequiredService<IHttpClientFactory>(),
            NullLoggerFactory.Instance,
            Path.Combine(harborDir, "cache", "providers"),
            sp.GetRequiredService<AuthStore>());

        return discovered.Ids
            .Concat(ProductProviderIds())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    ///     The ids the desktop flavor registers: what its discovery contributes
    ///     plus ollama, which that flavor registers natively.
    /// </summary>
    private static string[] ExpectedDesktopIds(
        IServiceProvider sp,
        IAuthResolver resolver,
        IModelCatalog catalog)
    {
        var discovered = new RecordingProviderBuilder();
        JsonProviderDiscovery.RegisterDesktopProviders(
            discovered,
            resolver,
            catalog,
            sp.GetRequiredService<IHttpClientFactory>(),
            NullLoggerFactory.Instance);

        return discovered.Ids
            .Append("ollama")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    ///     The provider ids the PRODUCT's own factories declare — the native
    ///     half of every flavor's registry.
    /// </summary>
    private static string[] ProductProviderIds() =>
        ProductProviderFactories()
            .Select(factory => factory.ProviderId.Value)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    ///     Every <see cref="IProviderFactory" /> the composition root declares,
    ///     read off the assembly rather than off a hand-written list — a list
    ///     would rot the moment a factory is added, and the guard would keep
    ///     reporting the set it remembers.
    /// </summary>
    private static IProviderFactory[] ProductProviderFactories() =>
        typeof(OllamaProviderFactory).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IProviderFactory).IsAssignableFrom(type))
            .Select(Construct)
            .OrderBy(factory => factory.ProviderId.Value, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    ///     Build one factory, so its provider id can be read. A factory whose
    ///     dependencies this census cannot supply is a loud failure, not a
    ///     silently missing entry.
    /// </summary>
    private static IProviderFactory Construct(Type type)
    {
        foreach (ConstructorInfo constructor in type.GetConstructors())
        {
            object?[]? arguments = ResolveArguments(constructor.GetParameters());
            if (arguments is not null)
            {
                return (IProviderFactory)constructor.Invoke(arguments);
            }
        }

        throw new InvalidOperationException(
            $"Cannot construct the provider factory {type.FullName}: none of its constructors takes only "
            + "arguments this census can supply. The census reads the provider id, so a factory with a "
            + "dependency the census cannot build has to say so here.");
    }

    private static object?[]? ResolveArguments(ParameterInfo[] parameters)
    {
        var arguments = new List<object?>();
        foreach (ParameterInfo parameter in parameters)
        {
            object? argument = ResolveArgument(parameter.ParameterType);
            if (argument is null)
            {
                return null;
            }

            arguments.Add(argument);
        }

        return arguments.ToArray();
    }

    private static object? ResolveArgument(Type parameterType)
    {
        if (parameterType == typeof(Func<HttpClient>))
        {
            return (Func<HttpClient>)(() => new HttpClient());
        }

        if (parameterType == typeof(IHttpClientFactory))
        {
            return new RecordingHttpClientFactory();
        }

        if (parameterType == typeof(AuthStore))
        {
            // The auth store is a constructor argument here and nothing more:
            // the census reads the provider id and never asks for a key, so
            // the store is built over a path that does not exist.
            return new AuthStore(new JsonConfigStore(
                Path.Combine(Path.GetTempPath(), "harbor-hosting-tests", "auth-store.json")));
        }

        return null;
    }

    // ── doubles ──────────────────────────────────────────────────────────

    /// <summary>Records the provider ids a registration path declares.</summary>
    private sealed class RecordingProviderBuilder : IProviderRegistryBuilder
    {
        public List<string> Ids { get; } = new();

        public void AddProvider(Func<ILlmClient> factory) => Ids.Add(factory().ProviderId.Value);

        public void AddProvider(ProviderId providerId, Func<ILlmClient> factory) => Ids.Add(providerId.Value);

        public void AddProvider(string providerId, Func<ILlmClient> factory) => Ids.Add(providerId);

        public void AddProvider(IProviderFactory factory) => Ids.Add(factory.ProviderId.Value);
    }

    /// <summary>An auth resolver that answers any provider with a fixed key.</summary>
    private sealed class FixedKeyResolver : IAuthResolver
    {
        public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success($"test-key-for-{providerId}"));
    }

    /// <summary>A model catalog that lists no models at all.</summary>
    private sealed class EmptyCatalog : IModelCatalog
    {
        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(
            ProviderConfig config,
            CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>()));
    }
}
