namespace Harbor.App.Avalonia.ViewModels;
/// <summary>
///     One model row in the provider/model picker. Clicking the row raises
///     <c>ProviderModelPickerViewModel.SelectModelCommand</c>. Pure record —
///     no UI-framework dependency — extracted here so WPF/MAUI/Blazor apps
///     can reuse the picker shape.
/// </summary>
public sealed record PickerModelViewModel(
    string ProviderId,
    string Id,
    string DisplayName,
    string PricingText,
    string FeaturesText);

/// <summary>
///     One provider row in the picker. Holds the auth-status badge and the
///     lazily-populated model list. <c>IsExpanded</c> is bound to the host's
///     Expander so the user can collapse providers they're not interested in.
/// </summary>
/// <remarks>
///     Pure view-model — no UI-framework dependency — extracted from
///     <c>ProviderModelPickerViewModel.cs</c> so other desktop apps can reuse
///     the same provider/grouping shape. The actual picker VM that wires up
///     the registry / config / toast calls stays in the platform app.
/// </remarks>
public sealed partial class ProviderGroupViewModel : ObservableObject
{

    /// <summary>Whether <see cref="Models" /> has been populated.</summary>
    [ObservableProperty]
    private bool _hasModels;

    /// <summary>Whether the Expander is open.</summary>
    [ObservableProperty]
    private bool _isExpanded;
    /// <summary>Construct a provider group.</summary>
    public ProviderGroupViewModel(
        string id,
        string displayName,
        string authStatusIcon,
        string authStatusText,
        string authStatusBrushKey,
        bool isAuthenticated,
        bool requiresApiKey)
    {
        Id = id;
        DisplayName = displayName;
        AuthStatusIcon = authStatusIcon;
        AuthStatusText = authStatusText;
        AuthStatusBrushKey = authStatusBrushKey;
        IsAuthenticated = isAuthenticated;
        RequiresApiKey = requiresApiKey;
    }

    /// <summary>Provider id (e.g. <c>ollama</c>, <c>kilocode</c>).</summary>
    public string Id { get; }

    /// <summary>Human-readable name from <c>ProviderPresets</c> (or id fallback).</summary>
    public string DisplayName { get; }

    /// <summary>Auth status icon — <c>✓</c> or <c>✗</c>.</summary>
    public string AuthStatusIcon { get; }

    /// <summary>Auth status text — e.g. "Authenticated", "No API key — set one in Settings".</summary>
    public string AuthStatusText { get; }

    /// <summary>
    ///     App-resource brush key for the auth icon — <c>MochaGreen</c> when
    ///     authenticated / no-key-required, <c>MochaRed</c> when a key is
    ///     missing. Resolved at bind time via the platform's brush-key converter.
    /// </summary>
    public string AuthStatusBrushKey { get; }

    /// <summary>Whether an API key was resolved for this provider.</summary>
    public bool IsAuthenticated { get; }

    /// <summary>Whether this provider requires an API key (false for Ollama, vLLM).</summary>
    public bool RequiresApiKey { get; }

    /// <summary>Optional setup hint URL from the provider preset.</summary>
    public string? SetupHint { get; init; }

    /// <summary>Models exposed by this provider.</summary>
    public ObservableCollection<PickerModelViewModel> Models { get; } = new();
}
