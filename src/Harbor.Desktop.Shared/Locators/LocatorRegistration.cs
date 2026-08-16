using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harbor.Desktop.Shared.Locators;

/// <summary>
///     One-Time Construction Convention for the desktop locator set.
///     The composition root calls <see cref="AddViewModelLocator" />
///     exactly once; the guard rejects a second registration so a
///     duplicated window cannot re-compose the same fixed nodes.
/// </summary>
/// <remarks>
///     Registrations issued (single set, no per-call-site
///     <c>AddSingleton&lt;T&gt;</c> spread through hosting code):
///     <list type="bullet">
///         <item><see cref="ViewModelLocator" /> → self, <see cref="IViewModelLocator" />.</item>
///         <item><see cref="ShowPlaceholderFactory" /> → self, <see cref="IShowPlaceholderFactory" />.</item>
///     </list>
/// </remarks>
public static class LocatorRegistration
{
    private static readonly object s_markerKey = new();

    /// <summary>
    ///     Register the desktop view-model locator set. Idempotent: a
    ///     repeated call is a no-op (marker stamp wins), NOT a duplicate
    ///     node in the container.
    /// </summary>
    /// <param name="services">The DI container.</param>
    public static void AddViewModelLocator(this IServiceCollection services)
    {
        if (services.Any(d => ReferenceEquals(d.ServiceKey, s_markerKey)))
        {
            return;
        }

        services.AddKeyedSingleton(s_markerKey, static (_, _) => nameof(ViewModelLocator));

        // Single provider-bound factory — not per-call-site AddSingletons.
        services.TryAddSingleton<ViewModelLocator>(static sp => new ViewModelLocator(sp));
        services.TryAddSingleton<IViewModelLocator>(static sp => sp.GetRequiredService<ViewModelLocator>());
        services.TryAddSingleton<ShowPlaceholderFactory>();
        services.TryAddSingleton<IShowPlaceholderFactory>(static sp => sp.GetRequiredService<ShowPlaceholderFactory>());
    }
}
