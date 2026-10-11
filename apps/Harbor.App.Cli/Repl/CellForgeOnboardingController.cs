using Harbor.Application.Configuration;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Tui.CellForge.Onboarding;
using Harbor.Ui.Framework.Rendering.Input;

namespace Harbor.App.Cli.Repl;

/// <summary>
/// Interactive setup flow inside CellForge (issue #1248, slice 1): owns one
/// <see cref="OnboardingFlow"/> (provider → key → model), mirrors its dialog
/// into <c>Screen.Dialog</c> for painting, and persists the outcome to the
/// config/auth stores. No legacy renderer and no line reader — keys arrive
/// from the input loop, answers leave through the stores.
/// </summary>
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
                if (existing.IsSuccess)
                {
                    configured.Add(preset.Id);
                }
            }

            _flow = new OnboardingFlow(catalogue, configured);
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
                c.Onboarded = true;
                return c;
            }, ct).ConfigureAwait(false);
            if (saved.IsFailure)
            {
                _host.Bridge.AppendSystemLine($"! Setup was not saved: {saved.Error}");
                return;
            }

            _host.Bridge.AppendSystemLine($"Setup complete: {providerId} / {model}.");
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
