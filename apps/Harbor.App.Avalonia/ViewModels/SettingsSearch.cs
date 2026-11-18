using System.Collections.ObjectModel;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Pure matching rules for the Settings search box (K29).
/// </summary>
/// <remarks>
///     <para>
///         The view-model and the Avalonia converter share this one definition
///         of which sections exist and what words find them. A section's
///         aliases are plain strings separated by '|'; matching is a
///         case-insensitive substring test with no reflection, XAML load or
///         application state, so the same code is safe under NativeAOT.
///     </para>
///     <para>
///         The alias properties are also the XAML <c>ConverterParameter</c>s,
///         which keeps the filter in the view and the "is anything left?"
///         answer in the view-model from drifting apart.
///     </para>
/// </remarks>
public static class SettingsSearch
{
    /// <summary>Aliases for the Theme / palette section.</summary>
    public static string ThemeAliases => "Theme|Palette|Appearance";

    /// <summary>Aliases for the Default provider section.</summary>
    public static string DefaultProviderAliases => "Default provider|Provider";

    /// <summary>Aliases for the Default model section.</summary>
    public static string DefaultModelAliases => "Default model|Model";

    /// <summary>Aliases for the Font family section.</summary>
    public static string FontFamilyAliases => "Font family|Font";

    /// <summary>Aliases for the Storage backend section.</summary>
    public static string StorageBackendAliases => "Storage backend|Storage";

    /// <summary>Aliases for the Log level section.</summary>
    public static string LogLevelAliases => "Log level|Logging";

    /// <summary>Aliases for the Ollama host section.</summary>
    public static string OllamaHostAliases => "Ollama host|Ollama";

    /// <summary>Aliases for the Onboarding section.</summary>
    public static string OnboardingAliases => "Onboarding|Setup wizard";

    /// <summary>Aliases for the Provider Configuration section.</summary>
    public static string ProviderConfigurationAliases => "Provider configuration|API key|Auth";

    /// <summary>Aliases for the Browse Models section.</summary>
    public static string BrowseModelsAliases => "Browse models|Model browser|Models";

    /// <summary>All sections, in the order they appear in SettingsView.</summary>
    public static ReadOnlyCollection<string> Sections { get; } = Array.AsReadOnly(
    [
        ThemeAliases,
        DefaultProviderAliases,
        DefaultModelAliases,
        FontFamilyAliases,
        StorageBackendAliases,
        LogLevelAliases,
        OllamaHostAliases,
        OnboardingAliases,
        ProviderConfigurationAliases,
        BrowseModelsAliases
    ]);

    /// <summary>
    ///     Whether a section whose aliases are <paramref name="aliases"/> should
    ///     be visible for <paramref name="query"/>. An empty query shows every
    ///     section; a query matches when it appears in any alias, ignoring case.
    /// </summary>
    public static bool IsMatch(string? aliases, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (string.IsNullOrEmpty(aliases)) return false;

        string needle = query.Trim();
        foreach (string alias in aliases.Split('|'))
        {
            if (alias.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    ///     Whether at least one section matches <paramref name="query"/>. The
    ///     view uses this to show the no-results state instead of an empty list.
    /// </summary>
    public static bool HasMatches(string? query)
    {
        foreach (string section in Sections)
        {
            if (IsMatch(section, query)) return true;
        }
        return false;
    }
}
