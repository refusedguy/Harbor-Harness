using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Plugins.Abstractions;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Storage;

/// <summary>
///     <see cref="IPluginTrustPolicy" /> that trusts everything under configured root
///     directories (e.g. the user-managed global <c>~/.harbor/plugins</c> scope) and,
///     for every other script, consults a persisted decision store keyed by
///     <c>(absolute path, content hash)</c> — with an optional interactive prompt for
///     first-time decisions.
/// </summary>
/// <remarks>
///     <para>
///         Decisions are stored in one JSON file (<see cref="_storePath" />, typically
///         <c>~/.harbor/plugins/trust.json</c>) so trust does not travel with the project.
///         An entry only matches while the file content is unchanged: editing the plugin
///         invalidates the decision and forces re-approval (or skip). This bounds the
///         blast radius of "install once, edit later" attacks.
///     </para>
///     <para>
///         When no prompt callback is supplied or stdin is not interactive at the wiring
///         site, unknown scripts fail closed (<see cref="PluginTrustDecision.Untrusted" />).
///     </para>
/// </remarks>
public sealed class FileTrustPolicy : IPluginTrustPolicy
{
    private sealed record TrustEntry
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("hash")]
        public string Hash { get; set; } = string.Empty;
    }

    private static readonly JsonSerializerOptions StoreOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<FileTrustPolicy> _logger;
    private readonly Func<PluginScript, Task<bool>>? _prompt;
    private readonly IReadOnlyList<string> _trustedDirs;
    private readonly string _storePath;
    private readonly object _sync = new();
    private List<TrustEntry>? _entries;

    /// <summary>
    ///     Construct a file-backed trust policy.
    /// </summary>
    /// <param name="trustedRoots">
    ///     Directories whose plugins are trusted implicitly (scopes maintained directly by
    ///     the user, e.g. the global plugins dir). Paths are compared OrdinalIgnoreCase after normalization.
    /// </param>
    /// <param name="storePath">JSON file for per-plugin decisions.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="trustPrompt">
    ///     Optional async hook invoked with scripts that have no persisted decision yet
    ///     (e.g. a console yes/no prompt). Return <c>true</c> to trust and persist. When
    ///     null, unknown scripts are skipped.
    /// </param>
    public FileTrustPolicy(
        IEnumerable<string> trustedRoots,
        string storePath,
        ILogger<FileTrustPolicy> logger,
        Func<PluginScript, Task<bool>>? trustPrompt = null)
    {
        _trustedDirs = (trustedRoots ?? throw new ArgumentNullException(nameof(trustedRoots)))
            .Select(NormalizeDir)
            .Where(d => d.Length > 0)
            .ToArray();
        _storePath = Path.GetFullPath(storePath);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _prompt = trustPrompt;
    }

    /// <inheritdoc />
    public async Task<PluginTrustDecision> DecideAsync(PluginScript script, CancellationToken ct = default)
    {
        if (script is null)
            throw new ArgumentNullException(nameof(script));

        string fullPath = Path.GetFullPath(script.Path);
        if (_trustedDirs.Any(d => IsUnder(fullPath, d)))
            return PluginTrustDecision.Trusted;

        var entries = LoadEntries();
        var existing = entries.FirstOrDefault(e => string.Equals(
            e.Path,
            fullPath,
            StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (string.Equals(existing.Hash, script.Hash, StringComparison.Ordinal))
                return PluginTrustDecision.Trusted;

            _logger.LogWarning(
                "Plugin {Path} changed since it was last trusted ({OldHash} → {NewHash}) — re-confirmation required",
                script.Path,
                existing.Hash[..Math.Min(12, existing.Hash.Length)],
                script.Hash[..Math.Min(12, script.Hash.Length)]);
        }

        if (_prompt is null || !await _prompt(script).ConfigureAwait(false))
            return PluginTrustDecision.Untrusted;

        PersistAccept(entries, existing, fullPath, script.Hash);
        return PluginTrustDecision.Trusted;
    }

    /// <summary>
    ///     Normalize a directory candidate to an absolute form without trailing separators.
    ///     Returns empty for invalid input instead of throwing — a bad entry must not
    ///     crash startup discovery.
    /// </summary>
    private static string NormalizeDir(string dir)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool IsUnder(string fullPath, string dir) =>
        dir.Length > 0 &&
        fullPath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private List<TrustEntry> LoadEntries()
    {
        lock (_sync)
        {
            if (_entries is not null)
                return _entries;

            try
            {
                if (File.Exists(_storePath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(_storePath));
                    _entries = doc.RootElement.ValueKind == JsonValueKind.Array
                        ? DeserializeList(doc.RootElement)
                        : [];
                }
                else
                {
                    _entries = [];
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Trust store {Store} unreadable — treating as empty", _storePath);
                _entries = [];
            }

            return _entries;
        }
    }

    private static List<TrustEntry> DeserializeList(JsonElement array) =>
        array.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(e => e.Deserialize<TrustEntry>(StoreOptions))
            .Where(e => e is not null && e!.Path.Length > 0 && e.Hash.Length > 0)
            .Cast<TrustEntry>()
            .ToList();

    private void PersistAccept(List<TrustEntry> snapshot, TrustEntry? stale, string fullPath, string hash)
    {
        var updated = new List<TrustEntry>(snapshot.Count + 1);
        foreach (var entry in snapshot)
        {
            if (!ReferenceEquals(entry, stale))
                updated.Add(entry);
        }

        updated.Add(new TrustEntry { Path = fullPath, Hash = hash });

        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
                var json = JsonSerializer.Serialize(updated, StoreOptions);
                File.WriteAllText(_storePath, json);

                // Only promote to the live cache after a successful save — an unwritable
                // store would otherwise grant trust for this run but not the next one,
                // training users into "yes" without effect.
                _entries = updated;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to persist trust decision for {Path} to {Store} — it will be asked again next time",
                    fullPath,
                    _storePath);
            }
        }
    }
}
