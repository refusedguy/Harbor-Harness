using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Application.Configuration;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Overlays;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>
///     Providers panel: all onboarding presets (the same
///     <c>ProviderPresets.All</c> source the setup wizard picks from) with
///     live auth status, fuzzy-filtered as you type. Enter opens the key
///     prompt (input frame); saving runs the same connection probe the
///     wizard runs after <c>/setup</c>, non-fatally. Local key-less
///     providers probe straight away. Non-interactive output stays textual
///     (<c>harbor providers</c> and the legacy <c>/providers</c> dispatcher
///     path are untouched).
/// </summary>
internal sealed class ProvidersCommand : IReplCommand
{
    public string Id => "providers";
    public IReadOnlyList<string> Aliases => [];
    public string Title => "Providers";
    public string Description => "browse providers, set keys, test connection";
    public string Group => "Config";

    public async Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;

        var seeds = new List<ProviderSeed>(ProviderPresets.All.Count);
        for (int i = 0; i < ProviderPresets.All.Count; i++)
        {
            var p = ProviderPresets.All[i];
            // Canonical "is auth configured?" check (config file → env),
            // same call the wizard makes before prompting for a key.
            var key = await host.AuthStore.GetApiKeyAsync(p.Id, ct).ConfigureAwait(false);
            seeds.Add(new ProviderSeed(p.Id, p.DisplayName, p.Description, p.RequiresApiKey, key.IsSuccess));
        }

        var model = new ProviderPanelModel();
        model.Show(seeds);

        var items = new List<CommandItem>(model.Results.Count);
        for (int i = 0; i < model.Results.Count; i++)
        {
            var e = model.Results[i];
            items.Add(new CommandItem(e.Id, e.Title, e.Detail, string.Empty, e.Group));
        }

        host.Palette.PushFrame(new PaletteFrame(
            "Providers", "providers", items,
            OnCommitAsync: (selected, frameCt) => HandleProviderAsync(host, selected, frameCt)));
        host.WakeUp();
    }

    private static Task HandleProviderAsync(IReplHost host, CommandItem selected, CancellationToken ct)
    {
        var preset = ProviderPresets.Find(selected.Id);
        if (preset is null)
        {
            host.Bridge.AppendSystemLine($"! Unknown provider '{selected.Id}'.");
            host.Palette.Hide();
            host.WakeUp();
            return Task.CompletedTask;
        }

        // Local providers need no key — probe the connection straight away.
        if (!preset.RequiresApiKey)
        {
            return CheckHealthAsync(host, preset.Id, ct);
        }

        host.Palette.PushFrame(new PaletteFrame(
            $"providers / {preset.Id}", $"providers / {preset.Id}",
            [],
            IsInput: true,
            InputPlaceholder: "paste API key...",
            OnInputSubmitAsync: (key, frameCt) => SaveKeyAsync(host, preset.Id, key, frameCt)));
        host.WakeUp();
        return Task.CompletedTask;
    }

    private static async Task SaveKeyAsync(IReplHost host, string providerId, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            host.Bridge.AppendSystemLine("No key entered — cancelled.");
            host.Palette.Hide();
            host.WakeUp();
            return;
        }

        var saved = await host.AuthStore.SetApiKeyAsync(providerId, key.Trim(), ct).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            host.Bridge.AppendSystemLine($"✗ Failed: {saved.Error}");
            host.Palette.Hide();
            host.WakeUp();
            return;
        }

        host.Bridge.AppendSystemLine($"✓ API key saved for {providerId}");
        await CheckHealthAsync(host, providerId, ct).ConfigureAwait(false);
    }

    private static async Task CheckHealthAsync(IReplHost host, string providerId, CancellationToken ct)
    {
        var healthCheck = host.HealthCheck;
        if (healthCheck is null)
        {
            // Same as the wizard: the probe is optional, the step is skipped.
            host.Palette.Hide();
            host.WakeUp();
            return;
        }

        var pidResult = ProviderId.TryCreate(providerId);
        if (pidResult.IsFailure)
        {
            host.Bridge.AppendSystemLine($"! Invalid provider id '{providerId}'.");
            host.Palette.Hide();
            host.WakeUp();
            return;
        }

        host.Bridge.AppendSystemLine($"  ⏳ Testing connection to {providerId}…");
        var result = await healthCheck.CheckAsync(pidResult.Value, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            host.Bridge.AppendSystemLine(
                $"  ✓ Connection OK — {result.Value.ModelsCount} model(s), {result.Value.LatencyMs} ms.");
        }
        else
        {
            // Non-fatal, same as the wizard: the reason may be transient and
            // the key stays saved for a later retry.
            host.Bridge.AppendSystemLine($"  ⚠ Connection test failed: {result.Error}");
            host.Bridge.AppendSystemLine($"    Fix the key later with `/auth reset {providerId}`.");
        }

        host.Palette.Hide();
        host.WakeUp();
    }
}
