namespace Harbor.Desktop.Shared.Locators;

/// <summary>
///     Centrally resolves view-models (and cross-platform shell services)
///     from the host service provider. Replaces the scattered
///     <c>App.Services.GetService&lt;T&gt;()</c> / constructor-injected
///     <see cref="IServiceProvider" /> lookups previously spread across
///     view code-behind, dialog services and shell view-models.
/// </summary>
/// <remarks>
///     Resolution is convention-based: every registered app type whose
///     name ends in <c>ViewModel</c>, <c>Service</c> or <c>Stack</c> is
///     locatable as <c>T</c>. Registered once in the composition root —
///     see <c>LocatorRegistration</c>.
/// </remarks>
public interface IViewModelLocator
{
    /// <summary>Resolve <typeparamref name="T" /> from the underlying service provider.</summary>
    /// <typeparam name="T">The view-model (or service) contract. Class-only.</typeparam>
    T Get<T>() where T : class;

    /// <summary>Resolve <typeparamref name="T" />, returning <c>null</c> when unregistered.</summary>
    /// <typeparam name="T">The view-model (or service) contract. Class-only.</typeparam>
    T? TryGet<T>() where T : class;

    /// <summary>
    ///     Resolve <typeparamref name="T" /> and verify the container holds it
    ///     as a singleton — the shell-VM cluster must come back as the same
    ///     instance across resolves so a window that stays open keeps its
    ///     fixed nodes instead of constructing duplicates.
    /// </summary>
    /// <typeparam name="T">The view-model contract. Class-only.</typeparam>
    T GetFromSingleton<T>() where T : class;
}
