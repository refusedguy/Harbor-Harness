using Harbor.Ui.Framework.Strings;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Ui.Framework.Services;

/// <summary>
///     Composition-root seam for the UI string catalogue (issue #1573):
///     <c>services.AddUiStrings()</c> registers <see cref="IUiStrings" /> as one
///     singleton per host, so migrated call sites resolve locale + lookups
///     through DI instead of the static <c>UiStrings</c> process-global.
/// </summary>
/// <remarks>
///     Deliberately hosted in Services, not in the Abstractions project the
///     catalogue lives in: hosts compose through their Dependencies-layer edge
///     to Services (the same shape as registering
///     <see cref="IFileTreeLoader" />), while
///     <c>Harbor.Ui.Framework.Abstractions</c> stays a pure Domain contract
///     assembly the CompositionRoot is not allowed to bind (the full layer
///     matrix rule blocks it).
/// </remarks>
public static class UiStringsRegistration
{
    /// <summary>
    ///     Registers the catalogue as one singleton under
    ///     <see cref="IUiStrings" />. A root that needs a pinned fake registers
    ///     <see cref="IUiStrings" /> itself before this call
    ///     (<c>AddSingleton&lt;IUiStrings&gt;(new UiStringsCatalog("ru"))</c>).
    /// </summary>
    public static IServiceCollection AddUiStrings(this IServiceCollection services) =>
        services.AddSingleton<IUiStrings, UiStringsCatalog>();
}
