using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Registries.Providers;
using Harbor.Registries.Scopes;
using TUnit.Assertions;

using FakeLlmClientBase = Harbor.TestKit.Generated.FakeLlmClient; // #1769: generated double

namespace Harbor.Registries.Tests;

/// <summary>
///     Tests for <see cref="ScopedProviderResolver" /> — the scope-aware
///     provider lookup (#1736).
/// </summary>
/// <remarks>
///     The resolver holds two DECLARED tables: which scope an agent runs in, and
///     which client a scope sees. The rule the tests below pin down is the
///     one that keeps the feature additive: a scope that declares nothing sees
///     the process-wide client, so a resolver with empty tables behaves exactly
///     like the global registry did before scopes existed.
/// </remarks>
public class ScopedProviderResolverTests
{
    private static readonly ModelInfo GlobalModel = new(
        "global-model",
        "test",
        "Global Model",
        200_000,
        4096,
        false,
        false,
        true,
        Pricing.Unknown,
        "openai");

    private static readonly ModelInfo ScopedModel = new(
        "scoped-model",
        "test",
        "Scoped Model",
        200_000,
        4096,
        false,
        false,
        true,
        Pricing.Unknown,
        "openai");

    private static readonly ProviderId TestProvider = ProviderId.Create("test");

    [Test]
    public async Task GetClient_WithoutAScopeBinding_ReturnsTheProcessWideClient()
    {
        var global = new StubLlmClient(GlobalModel);
        var resolver = new ScopedProviderResolver(RegistryWith(global));

        Result<ILlmClient> resolved = resolver.GetClient(ServiceScope.Create("ci"), TestProvider);

        await Assert.That(resolved.IsSuccess).IsTrue();
        await Assert.That(resolved.Value).IsSameReferenceAs(global);
    }

    [Test]
    public async Task GetClient_InABoundScope_ReturnsTheScopedClient()
    {
        var global = new StubLlmClient(GlobalModel);
        var scoped = new StubLlmClient(ScopedModel);
        var resolver = new ScopedProviderResolver(RegistryWith(global));
        resolver.BindProvider(ServiceScope.Create("ci"), TestProvider, scoped);

        Result<ILlmClient> resolved = resolver.GetClient(ServiceScope.Create("ci"), TestProvider);

        await Assert.That(resolved.IsSuccess).IsTrue();
        await Assert.That(resolved.Value).IsSameReferenceAs(scoped);
    }

    [Test]
    public async Task GetClient_InAnotherScope_StillReturnsTheProcessWideClient()
    {
        var global = new StubLlmClient(GlobalModel);
        var scoped = new StubLlmClient(ScopedModel);
        var resolver = new ScopedProviderResolver(RegistryWith(global));
        resolver.BindProvider(ServiceScope.Create("ci"), TestProvider, scoped);

        Result<ILlmClient> resolved = resolver.GetClient(ServiceScope.Create("other"), TestProvider);

        await Assert.That(resolved.IsSuccess).IsTrue();
        await Assert.That(resolved.Value).IsSameReferenceAs(global);
    }

    [Test]
    public async Task GetClient_ForAnUnregisteredProvider_Fails()
    {
        var resolver = new ScopedProviderResolver(RegistryWith());

        Result<ILlmClient> resolved = resolver.GetClient(ServiceScope.Default, ProviderId.Create("absent"));

        await Assert.That(resolved.IsFailure).IsTrue();
    }

    [Test]
    public async Task ScopeFor_WithoutABinding_ReturnsTheDefaultScope()
    {
        var resolver = new ScopedProviderResolver(RegistryWith());

        ServiceScope scope = resolver.ScopeFor(AgentName.Create("code"));

        await Assert.That(scope).IsEqualTo(ServiceScope.Default);
    }

    [Test]
    public async Task ScopeFor_WithABinding_ReturnsTheBoundScope()
    {
        var resolver = new ScopedProviderResolver(RegistryWith());
        resolver.BindScope(AgentName.Create("code"), ServiceScope.Create("ci"));

        ServiceScope scope = resolver.ScopeFor(AgentName.Create("code"));

        await Assert.That(scope).IsEqualTo(ServiceScope.Create("ci"));
    }

    [Test]
    public async Task GetModelsAsync_WithoutAScopeBinding_ServesTheProcessWideCatalog()
    {
        var resolver = new ScopedProviderResolver(RegistryWith(new StubLlmClient(GlobalModel)));

        Result<IReadOnlyList<ModelInfo>> models =
            await resolver.GetModelsAsync(ServiceScope.Create("ci"), TestProvider);

        await Assert.That(models.IsSuccess).IsTrue();
        await Assert.That(models.Value.Count).IsEqualTo(1);
        await Assert.That(models.Value[0].Id).IsEqualTo(GlobalModel.Id);
    }

    [Test]
    public async Task GetModelsAsync_InABoundScope_ServesTheScopedCatalog()
    {
        var resolver = new ScopedProviderResolver(RegistryWith(new StubLlmClient(GlobalModel)));
        resolver.BindProvider(ServiceScope.Create("ci"), TestProvider, new StubLlmClient(ScopedModel));

        Result<IReadOnlyList<ModelInfo>> models =
            await resolver.GetModelsAsync(ServiceScope.Create("ci"), TestProvider);

        await Assert.That(models.IsSuccess).IsTrue();
        await Assert.That(models.Value.Count).IsEqualTo(1);
        await Assert.That(models.Value[0].Id).IsEqualTo(ScopedModel.Id);
    }

    /// <summary>
    ///     The process-wide registry the resolver falls back to.
    /// </summary>
    private static ProviderRegistry RegistryWith(ILlmClient? client = null)
    {
        var registry = new ProviderRegistry();
        if (client is not null)
        {
            ILlmClient registered = client;
            registry.Register(TestProvider, () => registered);
        }

        return registry;
    }

    /// <summary>
    ///     A client that serves one model — the resolver never streams.
    /// </summary>
    private sealed class StubLlmClient : FakeLlmClientBase
    {
        public StubLlmClient(ModelInfo model)
        {
            ProviderId = TestProvider;
            GetModelsAsyncResult = Result.Success<IReadOnlyList<ModelInfo>>([model]);
        }
    }
}
