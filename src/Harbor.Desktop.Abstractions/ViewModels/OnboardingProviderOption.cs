namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     One provider row on step 2 of the onboarding wizard. Pure view-model —
///     no UI-framework dependency — so the same wizard shape can be reused by
///     Avalonia/WPF/MAUI/Blazor onboarding views. <see cref="IsSelected" /> is
///     a two-way checkbox; the wizard reads the checked set when the user
///     clicks Next.
/// </summary>
public sealed partial class OnboardingProviderOption : ObservableObject
{
    /// <summary>Two-way bound checkbox state.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Construct a provider option.</summary>
    /// <param name="id">Provider id, as declared in its <c>providers/&lt;id&gt;.json</c>.</param>
    /// <param name="displayName">Name shown in the picker.</param>
    /// <param name="authEnvVar">Environment variable holding the key, or null when the provider needs none.</param>
    /// <param name="requiresKey">Whether the provider refuses to run without a key.</param>
    /// <param name="defaultModel">Model the provider falls back to when the user picks none.</param>
    /// <param name="icon">
    ///     Glyph read from the provider's own <c>providers/&lt;id&gt;.json</c> by
    ///     <c>ProviderPresetCatalog</c> (#560) — it used to come from an id→glyph
    ///     switch in the wizard, so adding a provider meant editing this layer.
    ///     The fallback glyph for a config that declares none is applied upstream by
    ///     the catalog, so this layer stays free of a Harbor.Application reference
    ///     (the #188 Presentation→Application exception is scoped to two files by
    ///     <c>FullLayerMatrixTests.DocumentedExceptions</c>, and widening it here
    ///     would spread that debt).
    /// </param>
    public OnboardingProviderOption(string id, string displayName, string? authEnvVar, bool requiresKey, string defaultModel, string icon = "")
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
