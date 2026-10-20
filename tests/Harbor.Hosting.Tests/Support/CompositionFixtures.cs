using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting.Tests;

/// <summary>
///     Builds the host's service provider the way the composition suites do.
/// </summary>
/// <remarks>
///     <para>
///         Six files registered the Harbor services and built a provider. The
///         registration call is the product's, so the helper is the one place
///         that knows it.
///     </para>
/// </remarks>
internal static class CompositionFixtures
{
    internal static ServiceProvider Compose(HarborComposeOptions options)
    {
        var services = new ServiceCollection();
        services.AddHarbor(options);
        return services.BuildServiceProvider();
    }
}
