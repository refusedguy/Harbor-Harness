using System.Collections.Frozen;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.Logging;

namespace Harbor.Core.Configuration;

/// <summary>
/// Harbor application configuration.
/// Stored at ~/.harbor/config.json. No env vars required.
/// </summary>
public sealed class HarborConfig
{
    /// <summary>Selected provider ID (e.g. "anthropic", "openai", "kilocode").</summary>
    public string Provider { get; set; } = "kilocode";

    /// <summary>Selected model ID (e.g. "anthropic/claude-3.5-sonnet").</summary>
    public string Model { get; set; } = "anthropic/claude-3.5-sonnet";

    /// <summary>Selected agent (mode): code, plan, explore.</summary>
    public string Agent { get; set; } = "code";

    /// <summary>TUI renderer: ansi, plain, spectre.</summary>
    public string Tui { get; set; } = "ansi";

    /// <summary>Storage backend: jsonl, memory, sqlite.</summary>
    public string Storage { get; set; } = "jsonl";

    /// <summary>Whether onboarding has been completed.</summary>
    public bool Onboarded { get; set; } = false;

    /// <summary>API keys per provider (encrypted at rest via OS keychain in future).</summary>
    public Dictionary<string, string> ApiKeys { get; set; } = new();

    /// <summary>Custom provider configs (path to JSON or inline).</summary>
    public Dictionary<string, ProviderConfigEntry> Providers { get; set; } = new();

    /// <summary>Enabled plugins.</summary>
    public List<string> EnabledPlugins { get; set; } = new();

    /// <summary>Disabled builtin tools.</summary>
    public List<string> DisabledTools { get; set; } = new();

    /// <summary>Default max steps per agent run.</summary>
    public int MaxSteps { get; set; } = 50;

    /// <summary>Cost limit per session (USD). 0 = no limit.</summary>
    public decimal CostLimit { get; set; } = 10m;

    /// <summary>Compaction settings.</summary>
    public CompactionConfig Compaction { get; set; } = new();

    /// <summary>
    /// Returns a default <see cref="HarborConfig"/> (kilocode provider, claude-3.5-sonnet model, code agent, ansi TUI, jsonl storage).
    /// </summary>
    public static HarborConfig Default => new();
}

/// <summary>
/// Compaction-related configuration.
/// </summary>
public sealed class CompactionConfig
{
    /// <summary>
    /// Number of tokens to reserve below the model's context window before triggering compaction.
    /// </summary>
    public int ReserveTokens { get; set; } = 16384;

    /// <summary>
    /// Target token count for the kept tail when compacting.
    /// </summary>
    public int KeepRecentTokens { get; set; } = 20000;

    /// <summary>
    /// Minimum number of recent turns to keep verbatim after compaction.
    /// </summary>
    public int TailTurns { get; set; } = 2;
}

/// <summary>
/// User-supplied provider config entry (overrides the bundled JSON presets).
/// </summary>
public sealed class ProviderConfigEntry
{
    /// <summary>
    /// The provider's base URL (e.g. <c>https://api.example.com/v1</c>).
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The API type (e.g. <c>openai-compatible</c>, <c>anthropic</c>).
    /// </summary>
    public string ApiType { get; set; } = "openai-compatible";

    /// <summary>
    /// Optional URL to fetch the model list from.
    /// </summary>
    public string? ModelsUrl { get; set; }
}

/// <summary>
/// Configuration store — reads/writes ~/.harbor/config.json.
/// Implements Repository pattern for config.
/// </summary>
/// <remarks>
/// <para>
/// The configuration store is the single source of truth for user preferences and API keys.
/// The default <see cref="JsonConfigStore"/> reads and writes a JSON file at
/// <see cref="GetDefaultPath"/>; alternative implementations can back the store with a
/// database, OS keychain, etc.
/// </para>
/// <para>
/// Implementations MUST be thread-safe. <see cref="JsonConfigStore"/> uses a process-wide
/// lock to serialize reads/writes.
/// </para>
/// </remarks>
public interface IConfigStore
{
    /// <summary>
    /// Load the current configuration.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The loaded <see cref="HarborConfig"/>, or failure with an error message.</returns>
    Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Save the supplied configuration atomically.
    /// </summary>
    /// <param name="config">The configuration to save.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with an error message.</returns>
    Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default);

    /// <summary>
    /// Load → mutate → save in one atomic operation. The supplied <paramref name="updater"/>
    /// function receives the current config and returns the modified copy.
    /// </summary>
    /// <param name="updater">Pure function that maps the current config to the new one.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with an error message.</returns>
    Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default);
}

/// <summary>
/// JSON-backed <see cref="IConfigStore"/> implementation. Reads/writes ~/.harbor/config.json.
/// </summary>
public sealed class JsonConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _configPath;
    private readonly ILogger<JsonConfigStore>? _logger;
    private readonly object _lock = new();

    /// <summary>
    /// Construct a JSON-backed config store.
    /// </summary>
    /// <param name="configPath">Optional path override; defaults to <see cref="GetDefaultPath"/>.</param>
    /// <param name="logger">Optional logger.</param>
    public JsonConfigStore(string? configPath = null, ILogger<JsonConfigStore>? logger = null)
    {
        _configPath = configPath ?? GetDefaultPath();
        _logger = logger;
    }

    /// <summary>
    /// Returns the default config file path (<c>~/.harbor/config.json</c>).
    /// </summary>
    /// <returns>The absolute default path.</returns>
    public static string GetDefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var harborDir = Path.Combine(home, ".harbor");
        return Path.Combine(harborDir, "config.json");
    }

    /// <inheritdoc/>
    public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    _logger?.LogInformation("Config file not found, using defaults");
                    return Task.FromResult(Result.Success(HarborConfig.Default));
                }

                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<HarborConfig>(json, JsonOptions) ?? HarborConfig.Default;
                return Task.FromResult(Result.Success(config));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load config");
                return Task.FromResult(Result.Failure<HarborConfig>(ex.Message));
            }
        }
    }

    /// <inheritdoc/>
    public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default)
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(_configPath, json);
                _logger?.LogDebug("Config saved to {Path}", _configPath);
                return Task.FromResult(Result.Success());
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to save config");
                return Task.FromResult(Result.Failure(ex.Message));
            }
        }
    }

    /// <inheritdoc/>
    public async Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default)
    {
        var loadResult = await LoadAsync(ct).ConfigureAwait(false);
        if (loadResult.IsFailure) return loadResult;

        var updated = updater(loadResult.Value);
        return await SaveAsync(updated, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Auth store — manages API keys per provider.
/// Stored in ~/.harbor/config.json (in ApiKeys dictionary).
/// Future: use OS keychain.
/// </summary>
/// <remarks>
/// Resolution order: config file → preset env var (e.g. <c>KILO_API_KEY</c>) →
/// conventional env var (<c>&lt;PROVIDER&gt;_API_KEY</c>).
/// </remarks>
public sealed class AuthStore
{
    private readonly IConfigStore _configStore;
    private readonly ILogger<AuthStore>? _logger;

    /// <summary>
    /// Construct an auth store backed by the supplied config store.
    /// </summary>
    /// <param name="configStore">The config store to read/write API keys.</param>
    /// <param name="logger">Optional logger.</param>
    public AuthStore(IConfigStore configStore, ILogger<AuthStore>? logger = null)
    {
        _configStore = configStore;
        _logger = logger;
    }

    /// <summary>
    /// Look up the API key for the named provider. Resolution order: config file, preset
    /// env var (e.g. <c>KILO_API_KEY</c>), conventional env var (<c>&lt;PROVIDER&gt;_API_KEY</c>).
    /// </summary>
    /// <param name="providerId">The provider id (e.g. <c>anthropic</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success with the key, or failure listing all the env var names that were tried.</returns>
    public async Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        // 1. Check config file
        var configResult = await _configStore.LoadAsync(ct).ConfigureAwait(false);
        if (configResult.IsSuccess)
        {
            if (configResult.Value.ApiKeys.TryGetValue(providerId, out var key) && !string.IsNullOrEmpty(key))
                return Result.Success(key);
        }

        // 2. Check preset env var name (e.g. KILO_API_KEY for kilocode)
        var preset = ProviderPresets.Find(providerId);
        if (preset?.EnvVarName is not null)
        {
            var presetEnv = Environment.GetEnvironmentVariable(preset.EnvVarName);
            if (!string.IsNullOrEmpty(presetEnv))
            {
                _logger?.LogDebug("Using API key from preset env var {Name}", preset.EnvVarName);
                return Result.Success(presetEnv);
            }
        }

        // 3. Fall back to conventional env var: PROVIDERID_API_KEY
        var envName = providerId.ToUpperInvariant().Replace('-', '_') + "_API_KEY";
        var envValue = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(envValue))
        {
            _logger?.LogDebug("Using API key from env var {Name}", envName);
            return Result.Success(envValue);
        }

        // 4. Build helpful error mentioning all possible env var names
        var envVars = new List<string> { envName };
        if (preset?.EnvVarName is not null && preset.EnvVarName != envName)
            envVars.Add(preset.EnvVarName);

        return Result.Failure<string>(
            $"No API key for '{providerId}'. " +
            $"Run `harbor auth set {providerId} <key>` or set one of: " +
            envVars.Select(v => $"${v}").JoinToString(", ") + " env var.");
    }

    /// <summary>
    /// Persist the API key for a provider into the config file.
    /// </summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="apiKey">The API key to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with an error message.</returns>
    public async Task<Result> SetApiKeyAsync(string providerId, string apiKey, CancellationToken ct = default)
    {
        return await _configStore.UpdateAsync(c =>
        {
            c.ApiKeys[providerId] = apiKey;
            return c;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove the stored API key for a provider from the config file.
    /// </summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with an error message.</returns>
    public async Task<Result> RemoveApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        return await _configStore.UpdateAsync(c =>
        {
            c.ApiKeys.Remove(providerId);
            return c;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// List all known API keys with their availability status (configured in config file OR
    /// available via env var). The boolean is <see langword="true"/> when the key is set.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A map of provider id → present? </returns>
    public async Task<Result<IReadOnlyDictionary<string, bool>>> ListApiKeysAsync(CancellationToken ct = default)
    {
        var configResult = await _configStore.LoadAsync(ct).ConfigureAwait(false);
        if (configResult.IsFailure) return Result.Failure<IReadOnlyDictionary<string, bool>>(configResult.Error);

        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in configResult.Value.ApiKeys)
        {
            result[kv.Key] = !string.IsNullOrEmpty(kv.Value);
        }
        // Also check env vars
        foreach (var env in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>())
        {
            var key = env.Key?.ToString();
            if (key is null) continue;
            if (key.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase))
            {
                var pid = key[..^"_API_KEY".Length].ToLowerInvariant().Replace('_', '-');
                if (!result.ContainsKey(pid)) result[pid] = env.Value is string s && !string.IsNullOrEmpty(s);
            }
        }
        return Result.Success<IReadOnlyDictionary<string, bool>>(result);
    }
}

/// <summary>
/// Default provider presets — no JSON authoring required for the user.
/// These are the "always available" defaults; user just picks one during onboarding.
/// </summary>
/// <remarks>
/// The presets list is the single source of truth for the onboarding wizard's provider
/// picker. New providers are added here in code (not via JSON) to keep the onboarding UX
/// stable and discoverable.
/// </remarks>
public static class ProviderPresets
{
    /// <summary>
    /// A provider preset record.
    /// </summary>
    /// <param name="Id">Stable lowercase provider id.</param>
    /// <param name="DisplayName">Human-readable name shown in onboarding.</param>
    /// <param name="Description">One-line description shown when the user types <c>list</c> in onboarding.</param>
    /// <param name="DefaultModel">Default model id for this provider.</param>
    /// <param name="RequiresApiKey">Whether the provider needs an API key.</param>
    /// <param name="EnvVarName">Optional preset env var name (e.g. <c>KILO_API_KEY</c>).</param>
    /// <param name="SetupHint">Optional setup hint URL/message shown in onboarding.</param>
    public sealed record Preset(
        string Id,
        string DisplayName,
        string Description,
        string DefaultModel,
        bool RequiresApiKey,
        string? EnvVarName,
        string? SetupHint);

    /// <summary>
    /// All builtin provider presets, ordered by onboarding recommendation.
    /// </summary>
    public static readonly IReadOnlyList<Preset> All = new[]
    {
        new Preset("kilocode", "Kilo Code Gateway", "Multi-provider gateway with FREE models (tencent/hy3:free)", "tencent/hy3:free", true, "KILO_API_KEY", "Get a free key at https://kilo.ai"),
        new Preset("anthropic", "Anthropic (Claude)", "Direct Anthropic API — Claude Opus, Sonnet, Haiku", "claude-sonnet-4-20250514", true, "ANTHROPIC_API_KEY", "Get a key at https://console.anthropic.com"),
        new Preset("openai", "OpenAI (GPT)", "Direct OpenAI API — GPT-4o, o3, o4-mini", "gpt-4o", true, "OPENAI_API_KEY", "Get a key at https://platform.openai.com"),
        new Preset("openrouter", "OpenRouter", "Multi-provider router with 200+ models", "anthropic/claude-3.5-sonnet", true, "OPENROUTER_API_KEY", "Get a key at https://openrouter.ai"),
        new Preset("deepseek", "DeepSeek", "DeepSeek V3 and R1 (reasoning)", "deepseek-chat", true, "DEEPSEEK_API_KEY", "Get a key at https://platform.deepseek.com"),
        new Preset("groq", "Groq", "Ultra-fast LPU inference (Llama, Mixtral)", "llama-3.3-70b-versatile", true, "GROQ_API_KEY", "Get a key at https://console.groq.com"),
        new Preset("mistral", "Mistral AI", "Mistral Large, Codestral, Pixtral", "mistral-large-latest", true, "MISTRAL_API_KEY", "Get a key at https://console.mistral.ai"),
        new Preset("xai", "xAI (Grok)", "Grok models from xAI", "grok-2-latest", true, "XAI_API_KEY", "Get a key at https://x.ai"),
        new Preset("together", "Together AI", "Hosted open-source models", "meta-llama/Llama-3.3-70B-Instruct-Turbo", true, "TOGETHER_API_KEY", "Get a key at https://api.together.xyz"),
        new Preset("fireworks", "Fireworks AI", "Fast inference for open-source models", "accounts/fireworks/models/llama-v3p1-70b-instruct", true, "FIREWORKS_API_KEY", "Get a key at https://fireworks.ai"),
        new Preset("cerebras", "Cerebras", "Cerebras ultra-fast inference", "llama3.1-70b", true, "CEREBRAS_API_KEY", "Get a key at https://cloud.cerebras.ai"),
        new Preset("ollama", "Ollama (local)", "Local LLM inference — no API key needed", "llama3.2", false, null, "Install from https://ollama.ai and run `ollama serve`"),
        new Preset("vllm", "vLLM (local)", "Local vLLM server — no API key needed", "meta-llama/Llama-3.2-1B-Instruct", false, null, "Run `vllm serve <model>`"),
    };

    /// <summary>
    /// Find a preset by id (case-insensitive). Returns <see langword="null"/> if not found.
    /// </summary>
    /// <param name="id">The provider id to look up.</param>
    /// <returns>The matching preset, or <see langword="null"/>.</returns>
    public static Preset? Find(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Default presets that work without an API key (local providers).
    /// </summary>
    /// <returns>A list of presets with <see cref="Preset.RequiresApiKey"/> = <see langword="false"/>.</returns>
    public static IReadOnlyList<Preset> GetNoAuth() => All.Where(p => !p.RequiresApiKey).ToList();
}
