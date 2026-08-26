using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Application.Configuration;
using Microsoft.Extensions.Logging;
namespace Harbor.Application.Onboarding;
/// <summary>
///     First-run onboarding wizard. Walks user through:
///     1. Pick a provider (from presets)
///     2. Enter API key (if needed)
///     3. Pick a model
///     4. Pick a default agent (mode)
///     5. Save config
///     No env vars, no JSON authoring. Pure interactive UX.
/// </summary>
public sealed class OnboardingWizard
{
    private readonly AuthStore _authStore;
    private readonly IConfigStore _configStore;
    private readonly Abstractions.Providers.IProviderHealthCheck? _healthCheck;
    private readonly Abstractions.Providers.IProviderRegistry? _providers;
    private readonly ILogger<OnboardingWizard>? _logger;

    /// <summary>Cap on the numbered live-model list shown during setup.</summary>
    public const int MaxListedModels = 15;

    /// <summary>
    ///     Construct an <see cref="OnboardingWizard" /> wired to the supplied config and auth stores.
    /// </summary>
    /// <param name="configStore">The config store to persist the selected provider/model/agent.</param>
    /// <param name="authStore">The auth store to persist the entered API key.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="healthCheck">
    ///     Optional "test connection" probe (PRODUI-0 З.2). When present, the
    ///     wizard verifies the key right after it is saved instead of failing
    ///     on the first chat turn. When absent the step is skipped.
    /// </param>
    /// <param name="providers">
    ///     Optional provider registry (PROD-UI-0 З.4). When present the model
    ///     step shows a live list from <c>GetModelsAsync</c>; on failure it
    ///     degrades explicitly to manual entry.
    /// </param>
    public OnboardingWizard(
        IConfigStore configStore,
        AuthStore authStore,
        ILogger<OnboardingWizard>? logger = null,
        Abstractions.Providers.IProviderHealthCheck? healthCheck = null,
        Abstractions.Providers.IProviderRegistry? providers = null)
    {
        _configStore = configStore;
        _authStore = authStore;
        _healthCheck = healthCheck;
        _providers = providers;
        _logger = logger;
    }

    /// <summary>
    ///     Run the wizard. Returns success when config is saved.
    /// </summary>
    /// <remarks>
    ///     <b>ROP-B П.15:</b> the wizard scenario is a single Bind chain —
    ///     each step runs only when the previous one succeeded, so a failed
    ///     step short-circuits without a ladder of
    ///     <c>if (…IsFailure) return …;</c> passthroughs.
    /// </remarks>
    public async Task<Result> RunAsync(Func<string, Task<string>> reader, Action<string> writer, CancellationToken ct = default)
    {
        WriteBanner(writer);

        return await PickProviderAsync(reader, writer, ct)
            .Tap(p =>
            {
                writer("");
                writer($"✓ Selected provider: {p.DisplayName}");
            })
            .Bind(p => SaveApiKeyIfNeededAsync(p, reader, writer, ct))
            .Bind(p => TestConnectionAsync(p, writer, ct))
            .Bind(async p => Result.Success((
                Provider: p,
                Model: await PickModelAsync(reader, writer, p, ct).ConfigureAwait(false))))
            .Bind(async x => Result.Success((
                x.Provider,
                x.Model,
                Agent: await PickAgentAsync(reader, writer, ct).ConfigureAwait(false))))
            .Bind(x => _configStore.UpdateAsync(c =>
            {
                c.Provider = x.Provider.Id;
                c.Model = x.Model;
                c.Agent = x.Agent;
                c.Onboarded = true;
                return c;
            }, ct).Map(() => x))
            .Tap(x => WriteCompletionBox(writer, x.Provider.Id, x.Model, x.Agent))
            .Map(static _ => Result.Success())
            .ConfigureAwait(false);
    }

    private static void WriteBanner(Action<string> writer)
    {
        writer("╔══════════════════════════════════════════════════════════════╗");
        writer("║                 Welcome to Harbor!                            ║");
        writer("║     Let's set up your AI coding agent in 30 seconds.          ║");
        writer("╚══════════════════════════════════════════════════════════════╝");
        writer("");
    }

    private static void WriteCompletionBox(Action<string> writer, string providerId, string model, string agent)
    {
        writer("");
        writer("╔══════════════════════════════════════════════════════════════╗");
        writer("║                 Setup complete!                               ║");
        writer($"║  Provider: {providerId,-50}║");
        writer($"║  Model:    {model,-50}║");
        writer($"║  Agent:    {agent,-50}║");
        writer("║                                                               ║");
        writer("║  Type your prompt and press Enter to start.                   ║");
        writer("║  Type /help for commands, /exit to quit.                      ║");
        writer("╚══════════════════════════════════════════════════════════════╝");
    }

    /// <summary>
    ///     PROD-UI-0 З.2: probe the provider right after the key is saved so a
    ///     bad key / wrong URL surfaces now, not on the first chat turn.
    ///     Non-fatal: the wizard continues even when the check fails — the
    ///     reason may be transient (offline machine) and the user can fix it
    ///     later via /auth or Settings.
    /// </summary>
    private async Task<Result<ProviderPresets.Preset>> TestConnectionAsync(
        ProviderPresets.Preset provider,
        Action<string> writer,
        CancellationToken ct)
    {
        if (_healthCheck is null)
            return Result.Success(provider);

        var pidResult = ProviderId.TryCreate(provider.Id);
        if (pidResult.IsFailure)
            return Result.Success(provider); // malformed preset id — not a connection problem

        writer("");
        writer($"  ⏳ Testing connection to {provider.Id}…");
        var result = await _healthCheck.CheckAsync(pidResult.Value, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            writer($"  ✓ Connection OK — {result.Value.ModelsCount} model(s), {result.Value.LatencyMs} ms.");
            return Result.Success(provider);
        }

        writer($"  ⚠ Connection test failed: {result.Error}");
        writer("    You can continue — fix the key later with `/auth set`.");
        return Result.Success(provider);
    }

    private async Task<Result<ProviderPresets.Preset>> PickProviderAsync(Func<string, Task<string>> reader, Action<string> writer, CancellationToken ct)
    {
        var presets = ProviderPresets.All;
        while (true)
        {
            writer("");
            writer("Pick a provider (recommended: kilocode — has FREE models):");
            for (int i = 0; i < presets.Count; i++)
            {
                var p = presets[i];
                string marker = p.RequiresApiKey ? "  " : "🔧";
                string freeHint = p.Id == "kilocode" ? " (FREE models available)" : "";
                writer($"  {marker} [{i + 1}] {p.DisplayName}{freeHint}");
            }
            writer("");
            string input = await reader("Enter number (or 'list' for details): ").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(input)) continue;

            if (input.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var p in presets)
                {
                    writer($"  {p.Id}: {p.Description}");
                    if (p.SetupHint is not null) writer($"    → {p.SetupHint}");
                }
                continue;
            }

            if (int.TryParse(input, out int idx) && idx >= 1 && idx <= presets.Count)
                return Result.Success(presets[idx - 1]);

            // Try as provider ID
            var byId = ProviderPresets.Find(input);
            if (byId is not null) return Result.Success(byId);

            writer($"Invalid selection: {input}");
        }
    }

    /// <summary>Prompt + persist the API key when the preset needs one; pass the provider through otherwise.</summary>
    private async Task<Result<ProviderPresets.Preset>> SaveApiKeyIfNeededAsync(
        ProviderPresets.Preset provider,
        Func<string, Task<string>> reader,
        Action<string> writer,
        CancellationToken ct)
    {
        if (!provider.RequiresApiKey)
            return Result.Success(provider);

        string? key = await PromptApiKeyAsync(reader, writer, provider, ct).ConfigureAwait(false);
        if (key is null)
            return Result.Failure<ProviderPresets.Preset>("No API key provided.");

        return await _authStore.SetApiKeyAsync(provider.Id, key, ct)
            .Map(() => provider)
            .Tap(_ => writer($"✓ API key saved for {provider.Id}"))
            .ConfigureAwait(false);
    }

    private async Task<string?> PromptApiKeyAsync(
        Func<string, Task<string>> reader,
        Action<string> writer,
        ProviderPresets.Preset provider,
        CancellationToken ct)
    {
        writer("");
        if (provider.SetupHint is not null)
            writer($"  ℹ {provider.SetupHint}");

        // Check if already set
        var existing = await _authStore.GetApiKeyAsync(provider.Id, ct).ConfigureAwait(false);
        if (existing.IsSuccess) // §4.6-ok: предикат UI-ветвления (сообщение «уже установлен»), не лесенка.
        {
            writer($"  ✓ API key for {provider.Id} already set (use `/auth reset {provider.Id}` to change).");
            return existing.Value;
        }

        writer("");
        string input = await reader($"Enter API key for {provider.Id}: ").ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(input) ? null : input.Trim();
    }

    private async Task<string> PickModelAsync(
        Func<string, Task<string>> reader,
        Action<string> writer,
        ProviderPresets.Preset provider,
        CancellationToken ct)
    {
        // PROD-UI-0 З.4: try a live model list first; degrade explicitly to
        // free-text when the provider is unreachable or the registry absent.
        IReadOnlyList<string>? liveModels = await TryFetchLiveModelsAsync(provider, writer, ct).ConfigureAwait(false);
        if (liveModels is not null)
            return await PickFromLiveListAsync(reader, writer, provider, liveModels).ConfigureAwait(false);

        return await PickFreeTextAsync(reader, writer, provider).ConfigureAwait(false);
    }

    /// <summary>
    ///     Fetch the provider's models with the standard 10 s budget.
    ///     Returns <see langword="null" /> (with a printed reason) when the
    ///     list is unavailable — never throws.
    /// </summary>
    private async Task<IReadOnlyList<string>?> TryFetchLiveModelsAsync(
        ProviderPresets.Preset provider, Action<string> writer, CancellationToken ct)
    {
        if (_providers is null)
            return null;

        var pid = Abstractions.Models.Identifiers.ProviderId.TryCreate(provider.Id);
        if (pid.IsFailure)
            return null;

        var clientResult = _providers.GetClient(pid.Value);
        if (clientResult.IsFailure)
        {
            writer($"  ⚠ Model list unavailable ({clientResult.Error.TrimEnd('.')}) — manual entry.");
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Abstractions.Providers.IProviderHealthCheck.DefaultTimeout);
        try
        {
            var result = await clientResult.Value.GetModelsAsync(cts.Token).ConfigureAwait(false);
            if (result.IsSuccess && result.Value.Count > 0)
                return result.Value.Select(m => m.Id).ToList();

            writer($"  ⚠ Model list unavailable ({(result.IsSuccess ? "provider exposes no models" : result.Error.TrimEnd('.'))}) — manual entry.");
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            writer("  ⚠ Model list unavailable (timed out) — manual entry.");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Live model list fetch failed for {Provider}", provider.Id);
            writer($"  ⚠ Model list unavailable ({ex.Message.TrimEnd('.')}) — manual entry.");
            return null;
        }
    }

    /// <summary>Numbered picker over the live list; accepts a number or any raw model id.</summary>
    private static async Task<string> PickFromLiveListAsync(
        Func<string, Task<string>> reader,
        Action<string> writer,
        ProviderPresets.Preset provider,
        IReadOnlyList<string> liveModels)
    {
        writer("");
        writer($"Available models for {provider.DisplayName} ({liveModels.Count}):");
        int shown = Math.Min(liveModels.Count, MaxListedModels);
        int defaultIndex = 0;
        string presetDefault = $"{provider.Id}/{provider.DefaultModel}";
        for (int i = 0; i < shown; i++)
        {
            writer($"  [{i + 1}] {liveModels[i]}");
            if (string.Equals(liveModels[i], presetDefault, StringComparison.OrdinalIgnoreCase))
                defaultIndex = i;
        }

        if (liveModels.Count > shown)
            writer($"  … and {liveModels.Count - shown} more (type the id to select one)");

        string input = await reader($"Enter number, or type a model id (default: {defaultIndex + 1}): ").ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(input))
            return presetDefault;

        if (int.TryParse(input, out int idx) && idx >= 1 && idx <= liveModels.Count)
            return $"{provider.Id}/{liveModels[idx - 1]}";

        // Raw model id — prepend provider prefix unless already present.
        if (!input.Contains('/'))
            return $"{provider.Id}/{input.Trim()}";
        return input.Trim();
    }

    /// <summary>The original free-text fallback (preset default on Enter).</summary>
    private static async Task<string> PickFreeTextAsync(
        Func<string, Task<string>> reader,
        Action<string> writer,
        ProviderPresets.Preset provider)
    {
        string defaultModel = $"{provider.Id}/{provider.DefaultModel}";

        writer("");
        writer($"Default model: {defaultModel}");
        string input = await reader("Press Enter to use default, or type a model name: ").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(input)) return defaultModel;

        // If user typed just a model name without provider/, prepend it
        if (!input.Contains('/')) return $"{provider.Id}/{input}";
        return input.Trim();
    }

    private async Task<string> PickAgentAsync(Func<string, Task<string>> reader, Action<string> writer, CancellationToken ct)
    {
        writer("");
        writer("Pick a default agent (mode):");
        writer("  [1] code    — Default. Can read/write/edit files and run commands.");
        writer("  [2] plan    — Read-only planning. Cannot modify files.");
        writer("  [3] explore — Fast read-only codebase exploration.");

        string input = await reader("Enter number (default: 1): ").ConfigureAwait(false);
        return input switch
        {
            "" or "1" => "code",
            "2" => "plan",
            "3" => "explore",
            _ when input.Equals("code", StringComparison.OrdinalIgnoreCase) => "code",
            _ when input.Equals("plan", StringComparison.OrdinalIgnoreCase) => "plan",
            _ when input.Equals("explore", StringComparison.OrdinalIgnoreCase) => "explore",
            _ => "code"
        };
    }
}
