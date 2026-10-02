using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Desktop.Shared.Locators;

/// <summary>
///     Convention-based <see cref="IViewModelLocator" /> backed by an
///     <see cref="IServiceProvider" />. Resolution is
///     <c>sp.GetService(typeof(T))</c> — a plain interface call on the
///     container, so the hot path is the container's own call-site cache
///     and no per-call reflection.
/// </summary>
/// <remarks>
///     Constructed once by the DI container (factory binds the root
///     provider). Marked <c>sealed</c>; new conventions land on
///     <see cref="IViewModelLocator" /> implementations, not subclasses.
///     Re-registering is guarded by <c>TryAdd</c> — see
///     <c>LocatorRegistration.AddViewModelLocator</c>.
///     #63 legitimate: this type IS the locator (XAML creates view-models by
///     convention outside DI) — the contained provider is the pattern, not
///     a violation of it.
///     <para>
///         #414: this used to reflect over
///         <c>ServiceProviderServiceExtensions.GetMethods()</c>, close the open
///         generic with <c>MakeGenericMethod</c>, and cache a
///         <c>Expression.Lambda(...).Compile()</c> delegate per type. That is
///         three trim-unsafe mechanisms — member-by-string-name, a closed
///         generic the trimmer cannot see, and IL emitted at run time — to
///         produce <c>sp.GetService(typeof(T))</c>, which needs none of them.
///         <c>T</c> is statically known in <c>Get&lt;T&gt;</c>, so the
///         <c>typeof(T)</c> it wanted was always available without a cache.
///     </para>
///     <para>
///         The observable contract is unchanged: same container calls, same
///         null-returns-null and null-throws messages, same
///         <c>GetRequiredService</c>-not-<c>GetService</c> distinction on the two
///         paths (both are the non-keyed overloads, which the reflection lookup
///         selected by parameter shape). <see cref="GetFromSingleton{T}" />'s
///         duplicate-instance check is likewise unchanged.
///     </para>
/// </remarks>
public sealed class ViewModelLocator : IViewModelLocator
{
    private readonly IServiceProvider _services;

    /// <summary>Construct a <see cref="ViewModelLocator" /> over the given provider.</summary>
    /// <param name="services">The host root service provider.</param>
    public ViewModelLocator(IServiceProvider services)
    {
        _services = services;
    }

    /// <inheritdoc />
    public T Get<T>() where T : class =>
        _services.GetService(typeof(T)) as T
        ?? throw new InvalidOperationException($"Service '{typeof(T).FullName}' resolved to null.");

    /// <inheritdoc />
    public T? TryGet<T>() where T : class =>
        _services.GetService(typeof(T)) as T;

    /// <inheritdoc />
    public T GetFromSingleton<T>() where T : class
    {
        T first = Get<T>();
        T second = Get<T>();
        if (!ReferenceEquals(first, second))
        {
            throw new InvalidOperationException(
                $"Type '{typeof(T).FullName}' is expected to be registered as a singleton " +
                "(shell-VM cluster / fixed window node), but the container returned two " +
                "distinct instances. Fix the composition root — do not resolve it per call.");
        }

        return first;
    }
}
