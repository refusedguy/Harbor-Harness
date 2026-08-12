using Harbor.Ui.Framework.Services;

namespace Harbor.Desktop.Shared.Locators;

/// <summary>
///     A virtual overlay entry produced by the modal parser. It carries no
///     UI of its own; it exists so a parsed modal (settings, palette,
///     onboarding, …) can occupy a slot on the <see cref="IOverlayStack" />
///     before the concrete shell view inflates — the "show placeholder"
///     that stands in for the real overlay.
/// </summary>
public interface IShowPlaceholderOverlay
{
    /// <summary>Stable overlay identifier pushed onto <see cref="IOverlayStack" />.</summary>
    string OverlayId { get; }
}

/// <summary>
///     Parses a modal identifier (or a <c>vm:TypeName</c> view-model
///     reference) into a virtual <see cref="IShowPlaceholderOverlay" />.
///     The factory is the single place where a modal name becomes a real
///     overlay entry, instead of each dialog call-site inventing its own
///     mapping.
/// </summary>
public interface IShowPlaceholderFactory
{
    /// <summary>
    ///     Parse the modal description <paramref name="modalToken" />
    ///     (e.g. <c>"settings"</c> or <c>"vm:SettingsViewModel"</c>) and
    ///     return the placeholder overlay for it.
    /// </summary>
    /// <param name="modalToken">The modal token from XAML args or a command parameter.</param>
    IShowPlaceholderOverlay CreatePlaceholder(string modalToken);

    /// <summary>Create a placeholder for a view-model type directly.</summary>
    /// <typeparam name="TViewModel">The view-model the modal binds to.</typeparam>
    IShowPlaceholderOverlay CreateForViewModel<TViewModel>() where TViewModel : class;
}

/// <summary>
///     Default <see cref="IShowPlaceholderFactory" />: convention maps a
///     view-model <c>XxxViewModel</c> to overlay id <c>"xxx"</c>, and a
///     bare token passes through lower-cased. Constructed once with the
///     <see cref="IViewModelLocator" /> so a parsed modal can resolve its
///     VM when the real view finally inflates.
/// </summary>
public sealed class ShowPlaceholderFactory : IShowPlaceholderFactory
{
    private readonly IViewModelLocator _locator;

    /// <summary>Construct a <see cref="ShowPlaceholderFactory" />.</summary>
    /// <param name="locator">The central view-model locator.</param>
    public ShowPlaceholderFactory(IViewModelLocator locator)
    {
        _locator = locator;
    }

    /// <summary>The locator a parsed modal uses to resolve its view-model on inflation.</summary>
    public IViewModelLocator Locator => _locator;

    /// <inheritdoc />
    public IShowPlaceholderOverlay CreatePlaceholder(string modalToken)
    {
        if (string.IsNullOrWhiteSpace(modalToken))
        {
            throw new ArgumentException("Modal token must be non-empty.", nameof(modalToken));
        }

        string id = modalToken.StartsWith("vm:", StringComparison.OrdinalIgnoreCase)
            ? modalToken[3..]
            : modalToken;
        if (id.EndsWith("ViewModel", StringComparison.Ordinal))
        {
            id = id[..^"ViewModel".Length];
        }

        return new ShowPlaceholderOverlay(id.ToLowerInvariant());
    }

    /// <inheritdoc />
    public IShowPlaceholderOverlay CreateForViewModel<TViewModel>() where TViewModel : class
    {
        string name = typeof(TViewModel).Name;
        return CreatePlaceholder(name);
    }

    private sealed class ShowPlaceholderOverlay : IShowPlaceholderOverlay
    {
        public ShowPlaceholderOverlay(string overlayId)
        {
            OverlayId = overlayId;
        }

        public string OverlayId { get; }
    }
}
