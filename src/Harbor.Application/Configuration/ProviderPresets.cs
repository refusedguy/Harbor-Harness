namespace Harbor.Application.Configuration;
/// <summary>
///     The provider picker catalogue — every provider Harbor knows about, in onboarding
///     order. Each entry is a projection of one <c>providers/*.json</c> file, so adding a
///     provider means dropping that file and nothing else (#580).
/// </summary>
/// <remarks>
///     There is deliberately no table here any more. <see cref="ProviderPresetCatalog" />
///     reads the same configs — through the same directory walk the provider
///     <i>registration</i> path uses — and derives the presets from their JSON fields, so
///     the wizard, <c>/providers</c>, <c>/auth set</c> and the desktop pickers can never
///     disagree with what actually got registered. The source files declare their own
///     onboarding data: <c>displayName</c>, <c>description</c>, <c>defaultModel</c>,
///     <c>setupHint</c>, <c>priority</c>, plus <c>authType</c>/<c>authEnvVar</c> for
///     credentials.
/// </remarks>
public static class ProviderPresets
{
    /// <summary>
    ///     Every provider preset, ordered by onboarding recommendation (the <c>priority</c>
    ///     each config declares, then id). Computed once per process from
    ///     <c>providers/*.json</c>.
    /// </summary>
    public static readonly IReadOnlyList<Preset> All = ProviderPresetCatalog.Load();

    /// <summary>Find a preset by id (case-insensitive). Returns <see langword="null" /> if not found.</summary>
    /// <param name="id">The provider id to look up.</param>
    /// <returns>The matching preset, or <see langword="null" />.</returns>
    public static Preset? Find(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Default presets that work without an API key (local providers).</summary>
    /// <returns>A list of presets with <see cref="Preset.RequiresApiKey" /> = <see langword="false" />.</returns>
    public static IReadOnlyList<Preset> GetNoAuth() => All.Where(p => !p.RequiresApiKey).ToList();

    /// <summary>A provider preset record.</summary>
    /// <param name="Id">Stable lowercase provider id.</param>
    /// <param name="DisplayName">Human-readable name shown in onboarding.</param>
    /// <param name="Description">One-line description shown when the user types <c>list</c> in onboarding.</param>
    /// <param name="DefaultModel">Default model id for this provider.</param>
    /// <param name="RequiresApiKey">Whether the provider needs an API key.</param>
    /// <param name="EnvVarName">Optional preset env var name (e.g. <c>KILO_API_KEY</c>).</param>
    /// <param name="SetupHint">Optional setup hint URL/message shown in onboarding.</param>
    /// <param name="Icon">
    ///     Emoji glyph shown next to the provider row (#560). Declared by the
    ///     provider's own <c>providers/&lt;id&gt;.json</c> — it used to be a
    ///     13-arm <c>switch</c> in the desktop wizard, which meant adding a provider
    ///     required editing existing C# and forgetting it failed silently.
    /// </param>
    public sealed record Preset(
        string Id,
        string DisplayName,
        string Description,
        string DefaultModel,
        bool RequiresApiKey,
        string? EnvVarName,
        string? SetupHint,
        string Icon = ProviderPresets.DefaultIcon);

    /// <summary>
    ///     Glyph used when a config declares no <c>icon</c> — a generic wrench rather
    ///     than an empty cell. A config is not required to carry an icon; the picker
    ///     just shows this instead.
    /// </summary>
    public const string DefaultIcon = "\U0001F527";
}
