using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Application.Configuration;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Tui.CellForge.Onboarding;
using Harbor.Ui.Framework.Rendering.Input;

namespace Harbor.App.Cli.Repl;

/// <summary>
/// Interactive setup flow inside CellForge (issue #1248, slice 1): owns one
/// <see cref="OnboardingFlow"/> (provider → key → model → agent), mirrors its dialog
/// into <c>Screen.Dialog</c> for painting, and persists the outcome to the
/// config/auth stores. No legacy renderer and no line reader — keys arrive
/// from the input loop, answers leave through the stores.
/// </summary>
/// <remarks>
/// Slice 3: the model step is live where reachable — the host reads each
/// preset's catalog through the provider registry cache (the same catalog
/// whose entries come from that provider's <c>modelsUrl</c> in
/// <c>providers/*.json</c>) and hands the id lists to the flow; unreachable providers degrade to the preset-default free text,
/// the same explicit degradation the console wizard uses. The agent step is
/// projected from the agent registry (fallback first), also persisted.
/// </remarks>
internal sealed class CellForgeOnboardingController
{
    private readonly CellForgeReplRunner _host;
    private OnboardingFlow? _flow;

    internal CellForgeOnboardingController(CellForgeReplRunner host)
    {
        _host = host;
    }

    /// <summary>True while the flow is up and owns the keyboard.</summary>
    internal bool Active => _flow is { IsComplete: false };

    /// <summary>
    /// Open the flow (the <c>/setup</c> entry). Always starts at Welcome — a
    /// finished run never resumes mid-step, it restarts.
    /// </summary>
    internal async Task OpenAsync(CancellationToken ct)
    {
        try
        {
            var host = (IReplHost)_host;
            var catalogue = new List<OnboardingProvider>(ProviderPresets.All.Count);
            var configured = new List<string>();
            foreach (var preset in ProviderPresets.All)
            {
                catalogue.Add(new OnboardingProvider(
                    preset.Id, preset.DisplayName, preset.DefaultModel,
                    preset.RequiresApiKey, preset.SetupHint));
                if (!preset.RequiresApiKey)
                {
                    continue;
                }

                var existing = await host.AuthStore.GetApiKeyAsync(preset.Id, ct).ConfigureAwait(false);
                if (existing.IsFailure)
                {
                    // No key stored yet — the auth step stays, nothing to report.
                    continue;
                }

                configured.Add(preset.Id);
            }

            _flow = new OnboardingFlow(catalogue, configured, await FetchLiveModelsAsync(host, catalogue, ct).ConfigureAwait(false), SelectableAgents(host));
            _flow.Start();
            if (_flow.IsComplete)
            {
                // Empty catalogue: Start cancels immediately — persist the
                // outcome (the "cancelled" line) instead of a silent no-op.
                await FinishAsync(_flow, ct).ConfigureAwait(false);
                return;
            }

            SyncDialog();
            _host._wake.Writer.TryWrite(null);
        }
        catch (OperationCanceledException)
        {
            // Shutting down — never leave a half-opened flow behind.
            _flow = null;
            SyncDialog();
        }
    }

    /// <summary>
    /// Best-effort live model lists for the flow's model step: one
    /// <c>GetModelsCachedAsync</c> per preset, bounded by a single shared budget so
    /// opening setup never blocks on a dead endpoint. A provider that fails
    /// (no key yet, offline, unknown) is simply absent — the flow degrades
    /// that provider to the preset-default free text.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FetchLiveModelsAsync(
        IReplHost host,
        IReadOnlyList<OnboardingProvider> catalogue,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(IProviderHealthCheck.DefaultTimeout);
        var token = cts.Token;

        var tasks = new Task<(string Id, IReadOnlyList<string>? Models)>[catalogue.Count];
        for (int i = 0; i < catalogue.Count; i++)
        {
            string id = catalogue[i].Id;
            tasks[i] = FetchOneAsync(host, id, token);
        }

        var live = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i].Models is { Count: > 0 } models)
            {
                live[results[i].Id] = models;
            }
        }
        return live;
    }

    private static async Task<(string Id, IReadOnlyList<string>? Models)> FetchOneAsync(
        IReplHost host,
        string providerId,
        CancellationToken ct)
    {
        try
        {
            var pid = ProviderId.TryCreate(providerId);
            if (pid.IsFailure)
            {
                return (providerId, null);
            }
            var models = await host.ProviderRegistry.GetModelsCachedAsync(pid.Value, ct).ConfigureAwait(false);
            if (models.IsFailure)
            {
                return (providerId, null);
            }
            var list = models.Value;
            if (list.Count == 0)
            {
                return (providerId, null);
            }
            var ids = new List<string>(list.Count);
            for (int i = 0; i < list.Count && ids.Count < OnboardingFlow.MaxListedEntries * 5; i++)
            {
                string id = list[i].Id;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
            return (providerId, ids.Count > 0 ? ids : null);
        }
        catch (OperationCanceledException)
        {
            return (providerId, null);
        }
        catch (Exception)
        {
            // Best-effort probe: any transport fault degrades to free text.
            return (providerId, null);
        }
    }

    /// <summary>
    /// Agent catalogue for the flow's agent step, fallback first then ordinal
    /// by name — the same set-ordering the console wizard states rather than
    /// inherits, so the numbered menu is stable across runs.
    /// </summary>
    private static IReadOnlyList<OnboardingAgent> SelectableAgents(IReplHost host)
    {
        var registered = host.AgentRegistry.GetAllAgents();
        if (registered.Count == 0)
        {
            return Array.Empty<OnboardingAgent>();
        }
        string? fallbackDisplay = null;
        var rest = new List<OnboardingAgent>(registered.Count);
        for (int i = 0; i < registered.Count; i++)
        {
            var agent = new OnboardingAgent(registered[i].Name.Value, registered[i].Description);
            if (string.Equals(agent.Id, AgentName.Fallback, StringComparison.OrdinalIgnoreCase))
            {
                fallbackDisplay = agent.DisplayName;
            }
            else
            {
                rest.Add(agent);
            }
        }
        rest.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        var ordered = new List<OnboardingAgent>(rest.Count + 1)
        {
            new(fallbackDisplay is null && rest.Count > 0 ? rest[0].Id : AgentName.Fallback,
                fallbackDisplay ?? (rest.Count > 0 ? rest[0].DisplayName : AgentName.Fallback)),
        };
        for (int i = 0; i < rest.Count; i++)
        {
            if (!string.Equals(rest[i].Id, ordered[0].Id, StringComparison.OrdinalIgnoreCase))
            {
                ordered.Add(rest[i]);
            }
        }
        return ordered;
    }

    /// <summary>
    /// Offer a key to the flow. Returns true only while the flow is (or was,
    /// for this key) active — a consumed key must not reach the composer.
    /// The key that completes the flow persists the outcome first.
    /// </summary>
    internal async Task<bool> HandleKeyAsync(KeyEvent key, CancellationToken ct)
    {
        var flow = _flow;
        if (flow is null || flow.IsComplete)
        {
            return false;
        }

        flow.HandleKey(key);
        if (flow.IsComplete)
        {
            await FinishAsync(flow, ct).ConfigureAwait(false);
            return true;
        }

        SyncDialog();
        return true;
    }

    private async Task FinishAsync(OnboardingFlow flow, CancellationToken ct)
    {
        var host = (IReplHost)_host;
        try
        {
            var outcome = flow.Result;
            if (outcome.HasNoValue)
            {
                _host.Bridge.AppendSystemLine("Setup cancelled — config unchanged.");
                return;
            }

            var result = outcome.Value;
            if (!result.Completed)
            {
                _host.Bridge.AppendSystemLine("Setup cancelled — config unchanged.");
                return;
            }

            if (!result.KeyAlreadyConfigured && result.ApiKey is { } apiKey)
            {
                var keyed = await host.AuthStore
                    .SetApiKeyAsync(result.ProviderId, apiKey, ct).ConfigureAwait(false);
                if (keyed.IsFailure)
                {
                    _host.Bridge.AppendSystemLine($"! API key was not saved: {keyed.Error}");
                    return;
                }
            }

            string providerId = result.ProviderId;
            string model = result.Model;
            var saved = await host.ConfigStore.UpdateAsync(c =>
            {
                c.Provider = providerId;
                c.Model = model;
                c.Agent = result.Agent;
                c.Onboarded = true;
                return c;
            }, ct).ConfigureAwait(false);
            if (saved.IsFailure)
            {
                _host.Bridge.AppendSystemLine($"! Setup was not saved: {saved.Error}");
                return;
            }

            _host.Bridge.AppendSystemLine($"Setup complete: {providerId} / {model} / {result.Agent}.");
        }
        finally
        {
            _flow = null;
            SyncDialog();
            _host._wake.Writer.TryWrite(null);
        }
    }

    /// <summary>
    /// Copy the flow's dialog onto the screen's dialog (or hide it when no
    /// flow is up). The screen's layer paints what lands here.
    /// </summary>
    private void SyncDialog()
    {
        if (_flow is { } flow)
        {
            _host.Screen.Dialog.SyncFrom(flow.Dialog);
        }
        else
        {
            _host.Screen.Dialog.Dismiss();
        }
    }
}
