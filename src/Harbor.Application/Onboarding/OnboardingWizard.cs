using System.Globalization;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
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
    private readonly Abstractions.Agents.IAgentRegistry? _agents;
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
    /// <param name="agents">
    ///     Optional agent registry (#582). When present the agent step lists what
    ///     the registry holds, so a newly registered builtin agent appears here
    ///     without an edit to this class. When absent the step still runs, with
    ///     nothing to list — the same explicit-degradation shape the provider and
    ///     model steps already use rather than a silent default.
    /// </param>
    public OnboardingWizard(
        IConfigStore configStore,
        AuthStore authStore,
        ILogger<OnboardingWizard>? logger = null,
        Abstractions.Providers.IProviderHealthCheck? healthCheck = null,
        Abstractions.Providers.IProviderRegistry? providers = null,
        Abstractions.Agents.IAgentRegistry? agents = null)
    {
        _configStore = configStore;
        _authStore = authStore;
        _healthCheck = healthCheck;
        _providers = providers;
        _agents = agents;
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
        int emptyAttempts = 0;
        while (!ct.IsCancellationRequested)
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
            if (string.IsNullOrWhiteSpace(input))
            {
                emptyAttempts++;
                if (emptyAttempts >= 3)
                    return Result.Failure<ProviderPresets.Preset>("Non-interactive input or setup aborted.");
                continue;
            }

            emptyAttempts = 0;
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

            var byId = ProviderPresets.Find(input);
            if (byId is not null) return Result.Success(byId);

            writer($"Invalid selection: {input}");
        }

        return Result.Failure<ProviderPresets.Preset>("Setup cancelled.");
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
        {
            writer("  ⚠ Model list unavailable (provider registry unavailable) — manual entry.");
            return null;
        }

        // ROP boundary #101: shared TryCreate → GetClient preamble; every
        // unavailable-list reason prints the same warning as the client-failure
        // path below so degraded setup is always explicit, never silent.
        var clientResult = _providers.ResolveClient(provider.Id);
        if (clientResult.IsFailure)
        {
            writer($"  ⚠ Model list unavailable ({clientResult.Error.TrimEnd('.')}) — manual entry.");
            return null;
        }

        // ROP boundary #101: GetModelsAsync is Result-only by contract — the
        // expected path is the IsSuccess check below. The catches are
        // network-only insurance: our own 10 s budget firing, or a transport
        // fault escaping a client. Warning text matches every other
        // unavailable-list reason above.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Abstractions.Providers.IProviderHealthCheck.DefaultTimeout);
        try
        {
            #pragma warning disable CFE0001
            // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
            // return or continue, a control-flow shape the analyzer does not model.
            // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
            var result = await clientResult.Value.GetModelsAsync(cts.Token).ConfigureAwait(false);
            #pragma warning restore CFE0001
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

    /// <summary>
    ///     The agent step, projected from <see cref="IAgentRegistry" /> (#582).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This used to be three <c>writer("  [1] code — …")</c> literals and a
    ///         six-arm <c>switch</c> over <c>"1"</c>/<c>"2"</c>/<c>"3"</c> plus three
    ///         <c>Equals</c> arms — a hand-written copy of the agent set in the one
    ///         place a first-time user learns the names exist. Every other agent-set
    ///         consumer in the product already projects from the registry
    ///         (<c>TaskTool</c>'s available-sub-agent hint, both <c>/agent</c>
    ///         commands, <c>TaskRunRunner</c>, <c>ReplRunner</c>), so a fourth builtin
    ///         agent registered there simply did not appear here, with nothing failing.
    ///     </para>
    ///     <para>
    ///         <b>ORDER, AND WHY IT IS NOT THE REGISTRY'S.</b> <c>IAgentRegistry</c> is
    ///         backed by a <c>ConcurrentDictionary</c> whose enumeration order is
    ///         unspecified, so a numbered menu over it would number the same agents
    ///         differently on two runs. The order here is therefore stated rather than
    ///         inherited: the fallback agent is entry 1, and the rest follow
    ///         ordinally by name. Entry 1 is the fallback because the empty-input
    ///         default has always been the fallback and the hint says <c>default: 1</c>;
    ///         the rest are sorted rather than registration-ordered so the numbering
    ///         is a property of the SET, not of an unspecified hash layout.
    ///     </para>
    ///     <para>
    ///         Sub-agents are listed too. Filtering <c>IsSubAgent</c> here would be a
    ///         second place that has to be told which agents exist, and the menu
    ///         offered <c>explore</c> before this change.
    ///     </para>
    /// </remarks>
    private async Task<string> PickAgentAsync(Func<string, Task<string>> reader, Action<string> writer, CancellationToken ct)
    {
        IReadOnlyList<AgentDefinition> agents = SelectableAgents(_agents?.GetAllAgents());

        if (agents.Count == 0)
        {
            writer("");
            writer("⚠ No agents registered — using " + AgentName.Fallback + ".");
        }
        else
        {
            writer("");
        }

        writer("Pick a default agent (mode):");
        writer("Pick a default agent (mode):");
        for (int i = 0; i < agents.Count; i++)
        {
            AgentDefinition agent = agents[i];
            string suffix = agent.Name.Value == AgentName.Fallback ? "  (default)" : string.Empty;
            writer($"  [{i + 1}] {agent.Name.Value,-9} — {agent.Description}{suffix}");
        }

        string input = await reader("Enter number, or type an agent name (default: 1): ").ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(input))
        {
            return DefaultAgentName(agents);
        }

        string trimmed = input.Trim();
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            && index >= 1
            && index <= agents.Count)
        {
            return agents[index - 1].Name.Value;
        }

        foreach (AgentDefinition agent in agents)
        {
            if (string.Equals(agent.Name.Value, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return agent.Name.Value;
            }
        }

        return DefaultAgentName(agents);
    }

    /// <summary>
    ///     The agents the picker offers, in the order it lists them: the fallback
    ///     agent first, then every other registered agent ordered by name.
    /// </summary>
    /// <remarks>
    ///     The fallback agent is not guaranteed to be registered — a host may supply
    ///     its own registry — so the leading entry is "the fallback if it is there,
    ///     otherwise the first by name", and the returned list has no duplicates
    ///     either way. When there are no agents at all the picker still answers, with
    ///     the fallback name: the wizard must not deadlock a first run on an empty
    ///     registry, and the session factory falls back to it anyway.
    /// </remarks>
    private static IReadOnlyList<AgentDefinition> SelectableAgents(IReadOnlyList<AgentDefinition>? registered)
    {
        if (registered is null || registered.Count == 0)
        {
            return [];
        }

        // `registered` comes out of a ConcurrentDictionary, so its own order is
        // unspecified. Sorting the tail by name makes the numbering a property of
        // the SET, so two runs of the wizard on the same registry number the same
        // agents the same way.
        var rest = new List<AgentDefinition>(registered.Count);
        AgentDefinition? fallback = null;
        foreach (AgentDefinition agent in registered)
        {
            if (agent.Name.Value == AgentName.Fallback)
            {
                fallback = agent;
            }
            else
            {
                rest.Add(agent);
            }
        }

        rest.Sort(static (a, b) => string.CompareOrdinal(a.Name.Value, b.Name.Value));

        // The fallback leads because the empty-input default is the fallback and the
        // hint reads "default: 1". A host that registers no agent by that name gets
        // the alphabetically-first agent in its place, so entry 1 still means the
        // default the wizard will report back.
        var ordered = new List<AgentDefinition>(rest.Count + 1);
        ordered.Add(fallback ?? rest[0]);
        foreach (AgentDefinition agent in rest)
        {
            if (!ReferenceEquals(agent, ordered[0]))
            {
                ordered.Add(agent);
            }
        }

        return ordered;
    }

    /// <summary>The name the picker lands on when the answer is empty or unusable.</summary>
    private static string DefaultAgentName(IReadOnlyList<AgentDefinition> agents)
        => agents.Count > 0 ? agents[0].Name.Value : AgentName.Fallback;
}
