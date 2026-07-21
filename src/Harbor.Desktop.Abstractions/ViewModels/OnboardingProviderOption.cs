namespace Harbor.App.Avalonia.ViewModels;
/// <summary>
///     One provider row on step 2 of the onboarding wizard. Pure view-model —
///     no UI-framework dependency — extracted from <c>OnboardingViewModel.cs</c>
///     so the same wizard shape can be reused by WPF/MAUI/Blazor onboarding
///     views. <see cref="IsSelected" /> is a two-way checkbox; the wizard reads
///     the checked set when the user clicks Next.
/// </summary>
public sealed partial class OnboardingProviderOption : ObservableObject
{

    /// <summary>Two-way bound checkbox state.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Construct a provider option.</summary>
    public OnboardingProviderOption(string id, string displayName, string? authEnvVar, bool requiresKey, string defaultModel, string icon = "🔧")
    {
        Id = id;
        DisplayName = displayName;
        AuthEnvVar = authEnvVar;
        RequiresKey = requiresKey;
        DefaultModel = defaultModel;
        Icon = icon;
    }
    /// <summary>Provider id (matches <c>CommonConfig.ApiKeys</c> keys + provider JSON files).</summary>
    public string Id { get; }

    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; }

    /// <summary>Name of the env var that holds the API key (shown as a hint), or null when no key is needed.</summary>
    public string? AuthEnvVar { get; }

    /// <summary>True when this provider requires an API key (i.e. not Ollama).</summary>
    public bool RequiresKey { get; }

    /// <summary>Suggested default model id for this provider.</summary>
    public string DefaultModel { get; }

    /// <summary>Emoji icon shown next to the provider name in the wizard.</summary>
    public string Icon { get; }
}
