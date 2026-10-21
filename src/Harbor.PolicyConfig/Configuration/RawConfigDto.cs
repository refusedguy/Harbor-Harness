using System.Text.Json.Serialization;
namespace Harbor.Application.Configuration;
/// <summary>
///     Raw DTO for the persisted config.json shape. Allows the normalizer to
///     read both the canonical fields and the legacy <c>CommonConfig</c> aliases
///     (defaultProvider / defaultModel / onboardingCompleted / storageBackend / …)
///     without polluting the public <see cref="HarborConfig" /> API.
/// </summary>
public sealed class RawConfigDto
{
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("agent")] public string? Agent { get; set; }

    /// <summary>
    ///     Renderer id (see <see cref="PresentationConfig.Tui" />). Free-form
    ///     on the way in: the TUI backend registry resolves an unknown id
    ///     through its single fallback rule and warns, so a config written
    ///     for a backend this build does not ship still loads.
    /// </summary>
    [JsonPropertyName("tui")] public string? Tui { get; set; }
    [JsonPropertyName("storage")] public string? Storage { get; set; }
    [JsonPropertyName("onboarded")] public bool? Onboarded { get; set; }

    /// <summary>Nested UI section (<c>ui.consoleEx</c>) — the canonical shape.</summary>
    [JsonPropertyName("ui")] public UiRawDto? Ui { get; set; }

    /// <summary>Legacy root-level alias for <c>ui.consoleEx</c> — still read, no longer written.</summary>
    [JsonPropertyName("consoleEx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CellForgeUiConfig? CellForge { get; set; }

    [JsonPropertyName("defaultProvider")] public string? DefaultProvider { get; set; }
    [JsonPropertyName("defaultModel")] public string? DefaultModel { get; set; }
    [JsonPropertyName("onboardingCompleted")] public bool? OnboardingCompleted { get; set; }
    [JsonPropertyName("storageBackend")] public string? StorageBackend { get; set; }
    [JsonPropertyName("logLevel")] public string? LogLevelConfig { get; set; }

    [JsonPropertyName("apiKeys")] public Dictionary<string, string>? ApiKeys { get; set; }
    [JsonPropertyName("providers")] public Dictionary<string, ProviderConfigEntry>? Providers { get; set; }
    [JsonPropertyName("enabledPlugins")] public List<string>? EnabledPlugins { get; set; }
    [JsonPropertyName("disabledTools")] public List<string>? DisabledTools { get; set; }
    [JsonPropertyName("autoReloadPlugins")] public bool? AutoReloadPlugins { get; set; }
    [JsonPropertyName("maxSteps")] public int? MaxSteps { get; set; }
    [JsonPropertyName("costLimit")] public decimal? CostLimit { get; set; }
    [JsonPropertyName("compaction")] public CompactionConfig? Compaction { get; set; }
    [JsonPropertyName("secondaryModel")] public string? SecondaryModel { get; set; }
    [JsonPropertyName("permissions")] public Dictionary<string, List<PermissionRule>>? Permissions { get; set; }

    /// <summary>
    ///     The per-plugin configuration sections, keyed by plugin id (#1734).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Each value is one plugin's own section, exactly as the user
    ///         wrote it. The host does not interpret a section here: what a
    ///         section MEANS is the plugin's schema, and the schema travels
    ///         with the plugin. So the section is kept as it was written, and
    ///         the plugin pipeline checks it against the schema when the
    ///         plugin starts.
    ///     </para>
    ///     <para>
    ///         Keeping the raw section is what lets the host write the
    ///         configuration back without losing anything it did not
    ///         understand: a section for a plugin this build does not have is
    ///         still the user's setting, and it survives a round trip.
    ///     </para>
    /// </remarks>
    [JsonPropertyName("plugins")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Plugins { get; set; }
}

/// <summary>
///     Nested <c>ui</c> section of config.json. Currently carries only the
///     CellForge renderer knobs; future UI preferences land here instead of
///     growing new root-level keys.
/// </summary>
public sealed class UiRawDto
{
    [JsonPropertyName("consoleEx")] public CellForgeUiConfig? CellForge { get; set; }
}
