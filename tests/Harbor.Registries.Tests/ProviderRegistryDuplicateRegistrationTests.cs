using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Registries.Providers;
using TUnit.Assertions;
using FakeLlmClientBase = Harbor.TestKit.Generated.FakeLlmClient;

namespace Harbor.Registries.Tests;

/// <summary>
///     Issue #2574: registering an id that is already present used to overwrite
///     the existing client silently (last writer wins). Registration now fails
///     loudly and leaves the original client in place.
/// </summary>
public class ProviderRegistryDuplicateRegistrationTests
{
    private sealed class StubClient : FakeLlmClientBase
    {
        public StubClient(string id) => ProviderId = ProviderId.Create(id);
    }

    [Test]
    public async Task TryRegister_DuplicateId_ReturnsFailureAndKeepsFirstClient()
    {
        var registry = new ProviderRegistry();
        var first = new StubClient("test");
        var second = new StubClient("test");

        await Assert.That(registry.TryRegister(first.ProviderId, () => first).IsSuccess).IsTrue();
        var duplicate = registry.TryRegister(second.ProviderId, () => second);

        await Assert.That(duplicate.IsFailure).IsTrue();
        await Assert.That(duplicate.Error).IsEqualTo("Provider 'test' is already registered.");
        await Assert.That(registry.GetClient(first.ProviderId).Value).IsSameReferenceAs(first);
    }

    [Test]
    public async Task Register_DuplicateId_ThrowsAndKeepsFirstClient()
    {
        var registry = new ProviderRegistry();
        var first = new StubClient("test");
        var second = new StubClient("test");
        registry.Register(first.ProviderId, () => first);

        Assert.Throws<InvalidOperationException>(() => registry.Register(second.ProviderId, () => second));

        await Assert.That(registry.GetClient(first.ProviderId).Value).IsSameReferenceAs(first);
    }
}
