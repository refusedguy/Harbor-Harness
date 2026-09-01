using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Desktop.Shared.Locators;

/// <summary>
///     Convention-based <see cref="IViewModelLocator" /> backed by an
///     <see cref="IServiceProvider" />. Type lookups are compiled once
///     into <c>sp.GetRequiredService(typeof(T))</c> delegates and cached,
///     so the hot path after first resolve is a dictionary hit — no
///     per-call reflection.
/// </summary>
/// <remarks>
///     Constructed once by the DI container (factory binds the root
///     provider). Marked <c>sealed</c>; new conventions land on
///     <see cref="IViewModelLocator" /> implementations, not subclasses.
///     Re-registering is guarded by <c>TryAdd</c> — see
///     <c>LocatorRegistration.AddViewModelLocator</c>.
/// </remarks>
public sealed class ViewModelLocator : IViewModelLocator
{
    // Cache key must include the resolution mode: <see cref="Get{T}" /> binds
    // sp.GetRequiredService while <see cref="TryGet{T}" /> binds sp.GetService.
    // Keying on the type alone leaked the required-delegate into TryGet (and
    // vice versa), making an unregistered TryGet throw on any process where
    // Get for the same type ran first — order-dependent test/UX breakage.
    private static readonly ConcurrentDictionary<(Type Type, bool Required), Func<IServiceProvider, object?>> Cache = new();

    private readonly IServiceProvider _services;

    /// <summary>Construct a <see cref="ViewModelLocator" /> over the given provider.</summary>
    /// <param name="services">The host root service provider.</param>
    public ViewModelLocator(IServiceProvider services)
    {
        _services = services;
    }

    /// <inheritdoc />
    public T Get<T>() where T : class =>
        Cache.GetOrAdd((typeof(T), true), static key => ResolveRequired(key.Type))(_services) as T
        ?? throw new InvalidOperationException($"Service '{typeof(T).FullName}' resolved to null.");

    /// <inheritdoc />
    public T? TryGet<T>() where T : class =>
        Cache.GetOrAdd((typeof(T), false), static key => ResolveQuery(key.Type))(_services) as T;

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

    private static Func<IServiceProvider, object?> ResolveRequired(Type type) =>
        BuildCall(FindServiceMethod(isRequired: true), type);

    private static Func<IServiceProvider, object?> ResolveQuery(Type type) =>
        BuildCall(FindServiceMethod(isRequired: false), type);

    private static MethodInfo FindServiceMethod(bool isRequired)
    {
        string name = isRequired ? "GetRequiredService" : "GetService";
        foreach (MethodInfo method in typeof(ServiceProviderServiceExtensions).GetMethods())
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (method.Name == name
                && method.IsGenericMethodDefinition
                && parameters.Length == 1
                && parameters[0].ParameterType == typeof(IServiceProvider))
            {
                return method;
            }
        }

        throw new InvalidOperationException($"Microsoft.Extensions.DependencyInjection: method '{name}' not found.");
    }

    private static Func<IServiceProvider, object?> BuildCall(MethodInfo openGeneric, Type type)
    {
        ParameterExpression sp = Expression.Parameter(typeof(IServiceProvider), "sp");
        MethodInfo closed = openGeneric.MakeGenericMethod(type);
        MethodCallExpression call = Expression.Call(null, closed, sp);
        UnaryExpression cast = Expression.Convert(call, typeof(object));
        return Expression.Lambda<Func<IServiceProvider, object?>>(cast, sp).Compile();
    }
}
