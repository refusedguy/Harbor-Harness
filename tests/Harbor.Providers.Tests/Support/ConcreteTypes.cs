// ConcreteTypes.cs — one reflection discovery for the registry guards.
//
// The registries themselves are hand-written (NativeAOT-safe); only the tests
// scan assemblies. Two guards needed the same scan, so it lives here once.

namespace Harbor.Providers.Tests;

internal static class ProviderTestReflection
{
    /// <summary>Every concrete, parameterless-constructible implementer of <typeparamref name="T"/>.</summary>
    internal static IReadOnlyList<Type> ConcreteTypes<T>() =>
    [
        .. typeof(T).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsClass: true }
                        && typeof(T).IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
    ];
}
