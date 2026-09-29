using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Configuration;

/// <summary>
///     Projects <c>providers/*.json</c> onto <see cref="ProviderPresets.Preset" /> (#580).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ProviderPresets" /> used to be a hand-written table that every
///         <c>providers/*.json</c> had to be mirrored into, and a test enforced the
///         mirroring — which turned the documented "drop a <c>.json</c>, no code" path
///         (docs/EXAMPLES.md §9) into a red build. Both halves are gone: the presets
///         <i>are</i> the JSON files, read through the very same directory walk that
///         registers the providers (<see cref="FindProvidersDirectories" /> is shared
///         with the discovery path, so the two can never drift apart again).
///     </para>
///     <para>
///         Field mapping (one declaration per concept):
///         <list type="bullet">
///             <item><description><c>id</c> → <c>Id</c> (required; a config without one is not a provider).</description></item>
///             <item><description><c>displayName</c> → <c>DisplayName</c> (falls back to the id).</description></item>
///             <item><description><c>description</c> → <c>Description</c> (falls back to the display name).</description></item>
///             <item><description><c>defaultModel</c> → <c>DefaultModel</c>.</description></item>
///             <item><description><c>authType</c> → <c>RequiresApiKey</c> (<c>"none"</c> means no key).</description></item>
///             <item><description><c>authEnvVar</c> → <c>EnvVarName</c> (the one reason <c>kilocode</c> → <c>KILO_API_KEY</c> works).</description></item>
///             <item><description><c>setupHint</c> → <c>SetupHint</c>.</description></item>
///             <item><description><c>priority</c> → onboarding order (lower first; unset sorts after every declared one).</description></item>
///         </list>
///     </para>
///     <para>
///         Reading is reflection-free (<see cref="JsonDocument" />) so the catalog stays
///         NativeAOT-safe, and an unreadable directory or a malformed file is skipped
///         rather than thrown — one broken user config must not take the whole picker
///         down. A config that omits <c>defaultModel</c> still shows up: the wizard list
///         stays complete and the missing default surfaces where it is used.
///     </para>
/// </remarks>
public static class ProviderPresetCatalog
{
    /// <summary>
    ///     Onboarding priority for a config that declares none — bundled providers (which
    ///     all declare one) therefore always precede user-added ones.
    /// </summary>
    private const int DefaultPriority = 1000;

    /// <summary>How far up from the executable to look for a sibling <c>providers/</c> directory.</summary>
    private const int MaxAncestorDepth = 8;

    /// <summary>Manifest-resource name shape of the bundled configs (see Directory.Build.targets).</summary>
    private const string ResourceMarker = "providers.";

    /// <summary>A projected preset plus its sort key.</summary>
    private readonly record struct Projected(ProviderPresets.Preset Preset, int Priority);

    /// <summary>
    ///     Enumerate every candidate providers directory in precedence order: user config
    ///     (<c>~/.harbor/providers/</c>) first, then the directory next to the running
    ///     executable, then up to <see cref="MaxAncestorDepth" /> ancestors of it (the
    ///     typical dev-clone layout). User config wins on id collisions so E2E tests and
    ///     power users can override a bundled provider with their own JSON.
    /// </summary>
    /// <remarks>
    ///     Shared with the provider <i>registration</i> path
    ///     (<c>JsonProviderDiscovery</c> in Harbor.Hosting) — one walk, one precedence order.
    /// </remarks>
    public static IEnumerable<string> FindProvidersDirectories()
    {
        // 1. User config — always checked first so user overrides win.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".harbor", "providers");

        // 2. providers/ next to the running executable (single-file publish, etc.).
        string exeDir = AppContext.BaseDirectory;
        yield return Path.Combine(exeDir, "providers");

        // 3. Walk up from exeDir looking for a sibling providers/ directory
        //    (the typical dev-clone layout: repo root has providers/ next to apps/).
        string? current = exeDir;
        for (int i = 0; i < MaxAncestorDepth && current is not null; i++)
        {
            yield return Path.Combine(current, "providers");
            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>
    ///     Project raw <c>providers/*.json</c> payloads into presets, in onboarding order.
    ///     Pure: no I/O, no ambient state — the single place where a JSON field becomes a
    ///     preset field, and what the tests pin the mapping through.
    /// </summary>
    /// <param name="configs">The JSON text of every candidate config, most-preferred first.</param>
    /// <returns>The projected presets; the first occurrence of an id wins, ordered by <c>priority</c> then id.</returns>
    public static IReadOnlyList<ProviderPresets.Preset> FromJson(IEnumerable<string> configs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projected = new List<Projected>();

        foreach (string json in configs)
        {
            Projected? entry = Parse(json);
            if (entry is null || !seen.Add(entry.Value.Preset.Id)) continue;
            projected.Add(entry.Value);
        }

        projected.Sort(static (a, b) =>
        {
            int byPriority = a.Priority.CompareTo(b.Priority);
            return byPriority != 0 ? byPriority : string.CompareOrdinal(a.Preset.Id, b.Preset.Id);
        });

        var presets = new List<ProviderPresets.Preset>(projected.Count);
        for (int i = 0; i < projected.Count; i++) presets.Add(projected[i].Preset);
        return presets;
    }

    /// <summary>
    ///     Load the preset catalog the way the app does: the bundled <c>providers/*.json</c>
    ///     (embedded resources, so a published single-file binary still has them) plus every
    ///     <see cref="FindProvidersDirectories" /> entry, in that precedence order.
    /// </summary>
    /// <param name="logger">Optional logger; malformed or unreadable configs are skipped, and logged when one is supplied.</param>
    /// <returns>The projected presets, or an empty list when nothing readable was found.</returns>
    public static IReadOnlyList<ProviderPresets.Preset> Load(ILogger? logger = null)
    {
        try
        {
            return FromJson(EnumerateBundled(logger));
        }
        catch (Exception ex)
        {
            // This runs from a static field initializer, so letting anything escape would
            // turn an unreadable config directory into a TypeInitializationException in
            // every consumer. An empty catalogue degrades the picker; it must not crash.
            logger?.LogError(ex, "Failed to read providers/*.json — the provider picker will be empty.");
            return Array.Empty<ProviderPresets.Preset>();
        }
    }

    /// <summary>
    ///     Yield the JSON text of every bundled config: the shared directory walk first
    ///     (so a user copy overrides the embedded one), then the embedded resources.
    /// </summary>
    private static IEnumerable<string> EnumerateBundled(ILogger? logger)
    {
        foreach (string dir in FindProvidersDirectories())
        {
            string[] files;
            try
            {
                if (!Directory.Exists(dir)) continue;
                files = Directory.GetFiles(dir, "*.json");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // §4.6-ok: discovery resilience — an unreadable directory is skipped, not fatal.
                logger?.LogWarning("Skipping provider presets from '{Dir}': {Error}", dir, ex.Message);
                continue;
            }

            // Directory order is filesystem-defined; sort so the catalog is reproducible.
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                string? content = TryRead(file, logger);
                if (content is not null) yield return content;
            }
        }

        Assembly assembly = typeof(ProviderPresetCatalog).Assembly;
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (!name.Contains(ResourceMarker, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            string? content = TryRead(name, logger, assembly);
            if (content is not null) yield return content;
        }
    }

    /// <summary>Read a file or an embedded resource, skipping whatever cannot be read.</summary>
    private static string? TryRead(string path, ILogger? logger, Assembly? assembly = null)
    {
        try
        {
            if (assembly is null) return File.ReadAllText(path);

            using Stream? stream = assembly.GetManifestResourceStream(path);
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning("Skipping provider preset config '{Path}': {Error}", path, ex.Message);
            return null;
        }
    }

    /// <summary>
    ///     Project one JSON payload. Returns <see langword="null" /> for anything that is
    ///     not a usable provider config: malformed JSON, a non-object root, or no id.
    /// </summary>
    private static Projected? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? id = GetString(root, "id");
            if (string.IsNullOrWhiteSpace(id)) return null;

            string displayName = GetString(root, "displayName") ?? id;
            string description = GetString(root, "description") ?? displayName;
            string authType = GetString(root, "authType") ?? "bearer";

            var preset = new ProviderPresets.Preset(
                id,
                displayName,
                description,
                GetString(root, "defaultModel") ?? "",
                !authType.Equals("none", StringComparison.OrdinalIgnoreCase),
                GetString(root, "authEnvVar"),
                GetString(root, "setupHint"));

            return new Projected(preset, GetInt(root, "priority") ?? DefaultPriority);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            ? number
            : null;
}
