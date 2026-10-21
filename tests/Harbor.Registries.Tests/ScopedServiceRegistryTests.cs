using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Registries.Scopes;
using TUnit.Assertions;

namespace Harbor.Registries.Tests;

/// <summary>
///     Tests for <see cref="ScopedServiceRegistry{TKey, TService}" /> — the
///     mechanism behind "one implementation of a service per scope" (#1736).
/// </summary>
/// <remarks>
///     The registry is the DECLARATION: a binding says which implementation a
///     scope sees for a key. The resolution order it implements is the whole
///     contract — the scope's own binding, else the default scope's, else
///     nothing — and it is what keeps the feature additive: a registry with no
///     bindings resolves nothing, and the caller falls back to the
///     process-wide implementation.
/// </remarks>
public class ScopedServiceRegistryTests
{
    [Test]
    public async Task Resolve_InTheBoundScope_ReturnsTheBoundImplementation()
    {
        var registry = new ScopedServiceRegistry<string, string>();
        registry.Bind(ServiceScope.Create("ci"), "store", "ci-store");

        Result<string> resolved = registry.Resolve(ServiceScope.Create("ci"), "store");

        await Assert.That(resolved.IsSuccess).IsTrue();
        await Assert.That(resolved.Value).IsEqualTo("ci-store");
    }

    [Test]
    public async Task Resolve_InAnotherScope_InheritsTheDefaultScopeBinding()
    {
        var registry = new ScopedServiceRegistry<string, string>();
        registry.Bind(ServiceScope.Default, "store", "default-store");

        Result<string> resolved = registry.Resolve(ServiceScope.Create("ci"), "store");

        await Assert.That(resolved.IsSuccess).IsTrue();
        await Assert.That(resolved.Value).IsEqualTo("default-store");
    }

    [Test]
    public async Task Resolve_InAnotherScope_WithoutADefaultBinding_Fails()
    {
        var registry = new ScopedServiceRegistry<string, string>();
        registry.Bind(ServiceScope.Create("ci"), "store", "ci-store");

        Result<string> resolved = registry.Resolve(ServiceScope.Create("other"), "store");

        await Assert.That(resolved.IsFailure).IsTrue();
    }

    [Test]
    public async Task Resolve_WithNoBindingAtAll_Fails()
    {
        var registry = new ScopedServiceRegistry<string, string>();

        Result<string> resolved = registry.Resolve(ServiceScope.Default, "store");

        await Assert.That(resolved.IsFailure).IsTrue();
    }

    [Test]
    public async Task Bind_ForTheSameScopeAndKey_ReplacesTheEarlierBinding()
    {
        var registry = new ScopedServiceRegistry<string, string>();
        registry.Bind(ServiceScope.Create("ci"), "store", "first-store");
        registry.Bind(ServiceScope.Create("ci"), "store", "second-store");

        Result<string> resolved = registry.Resolve(ServiceScope.Create("ci"), "store");

        await Assert.That(resolved.IsSuccess).IsTrue();
        await Assert.That(resolved.Value).IsEqualTo("second-store");
    }

    [Test]
    public async Task Resolve_ForAnotherKey_FindsNothing()
    {
        var registry = new ScopedServiceRegistry<string, string>();
        registry.Bind(ServiceScope.Create("ci"), "store", "ci-store");

        Result<string> resolved = registry.Resolve(ServiceScope.Create("ci"), "other-store");

        await Assert.That(resolved.IsFailure).IsTrue();
    }

    [Test]
    public async Task IsBound_ReportsOnlyTheScopesThatDeclareTheirOwnImplementation()
    {
        var registry = new ScopedServiceRegistry<string, string>();
        registry.Bind(ServiceScope.Create("ci"), "store", "ci-store");
        registry.Bind(ServiceScope.Default, "store", "default-store");

        await Assert.That(registry.IsBound(ServiceScope.Create("ci"), "store")).IsTrue();
        await Assert.That(registry.IsBound(ServiceScope.Default, "store")).IsTrue();
        await Assert.That(registry.IsBound(ServiceScope.Create("other"), "store")).IsFalse();
    }
}
