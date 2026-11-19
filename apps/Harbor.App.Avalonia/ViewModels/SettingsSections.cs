// SettingsSections.cs — K29/#3929: the data behind the settings accordion.
//
// One row per settings section: a stable id, the header painted on the
// Expander, a short description (the text body a text-only Accordion would
// use) and the alias string the search filters on. The body key names the live
// control in SettingsView.Resources, so the view keeps every editable control
// while the section list itself stays data-driven and testable without a view.

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     The one value the settings view reads from its data context to filter
///     sections. Implemented by <see cref="SettingsViewModel"/> and by the
///     headless test double, so the view never needs to know the full VM.
/// </summary>
public interface ISettingsSearchContext
{
    /// <summary>Current search text; empty means every section.</summary>
    string SearchQuery { get; }
}

/// <summary>A single settings section, described without any Avalonia types.</summary>
/// <param name="Id">Stable identity used in the accordion's expanded set.</param>
/// <param name="Header">Text painted on the section's Expander header.</param>
/// <param name="Description">Short text describing the section.</param>
/// <param name="Aliases">Search aliases, in the same pipe-separated form as <see cref="SettingsSearch"/>.</param>
/// <param name="BodyKey">Resource key of the live body control in SettingsView.</param>
public readonly record struct SettingsSectionDefinition(
    string Id,
    string Header,
    string Description,
    string Aliases,
    string BodyKey);

/// <summary>
///     The settings sections in paint order. Search filters this list before
///     the view assigns it to the Accordion, which rebuilds its rows from the
///     result — one source of truth for both the row set and the no-results
///     flag in <see cref="SettingsSearch"/>.
/// </summary>
public static class SettingsSections
{
    /// <summary>All sections, in the order they appear in SettingsView.</summary>
    public static IReadOnlyList<SettingsSectionDefinition> All { get; } = Array.AsReadOnly<SettingsSectionDefinition>(
    [
        new("theme", "Theme", "Theme, palette and appearance settings.",
            SettingsSearch.ThemeAliases, "ThemeBody"),
        new("default-provider", "Default provider", "The provider used for new sessions.",
            SettingsSearch.DefaultProviderAliases, "DefaultProviderBody"),
        new("default-model", "Default model", "The model id used for new sessions.",
            SettingsSearch.DefaultModelAliases, "DefaultModelBody"),
        new("font-family", "Font family", "Font used across the desktop UI.",
            SettingsSearch.FontFamilyAliases, "FontFamilyBody"),
        new("storage-backend", "Storage backend", "Where sessions are persisted.",
            SettingsSearch.StorageBackendAliases, "StorageBackendBody"),
        new("log-level", "Log level", "How much the app writes to the log.",
            SettingsSearch.LogLevelAliases, "LogLevelBody"),
        new("ollama-host", "Ollama host", "Read from OLLAMA_HOST at startup.",
            SettingsSearch.OllamaHostAliases, "OllamaHostBody"),
        new("onboarding", "Onboarding", "Re-run the first-run wizard.",
            SettingsSearch.OnboardingAliases, "OnboardingBody"),
        new("provider-configuration", "Provider Configuration", "Per-provider API keys and connection tests.",
            SettingsSearch.ProviderConfigurationAliases, "ProviderConfigurationBody"),
        new("browse-models", "Browse Models", "Pick the active session's model.",
            SettingsSearch.BrowseModelsAliases, "BrowseModelsBody"),
    ]);
}
