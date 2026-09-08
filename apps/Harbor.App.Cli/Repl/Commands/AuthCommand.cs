using System.Text;
using Harbor.Application.Configuration;
using Harbor.Tui.CellForge.Widgets;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>Auth menu: list / set / remove API keys (set uses an input frame).</summary>
internal sealed class AuthCommand : IReplCommand
{
    public string Id => "auth";
    public IReadOnlyList<string> Aliases => ["key"];
    public string Title => "Auth";
    public string Description => "manage provider API keys";
    public string Group => "Config";

    public Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;

        host.Palette.PushFrame(new PaletteFrame(
            "Auth", "auth",
            new List<CommandItem>
            {
                new("list", "List Configured Keys", "Show configured providers", string.Empty, "Actions"),
                new("set", "Set API Key", "Configure API key for a provider", string.Empty, "Actions"),
                new("reset", "Remove API Key", "Clear stored key for a provider", string.Empty, "Actions")
            },
            OnCommitAsync: (item, frameCt) => HandleMenuAsync(host, item, frameCt)));
        host.WakeUp();
        return Task.CompletedTask;
    }

    private static async Task HandleMenuAsync(IReplHost host, CommandItem item, CancellationToken ct)
    {
        if (item.Id == "list")
        {
            await ListKeysAsync(host, ct).ConfigureAwait(false);
            return;
        }

        if (item.Id == "reset")
        {
            await ShowResetListAsync(host, ct).ConfigureAwait(false);
            return;
        }

        if (item.Id == "set")
        {
            ShowSetList(host);
        }
    }

    private static async Task ListKeysAsync(IReplHost host, CancellationToken ct)
    {
        var authStore = host.Services.GetRequiredService<AuthStore>();
        var keysResult = await authStore.ListApiKeysAsync(ct).ConfigureAwait(false);
        if (keysResult.IsSuccess)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Configured API keys:");
            foreach (var kv in keysResult.Value)
            {
                sb.AppendLine($"  {kv.Key}: {(kv.Value ? "set" : "missing")}");
            }
            host.Bridge.AppendSystemLine(sb.ToString());
        }
        else
        {
            host.Bridge.AppendSystemLine($"! {keysResult.Error}");
        }
        host.Palette.Hide();
        host.WakeUp();
    }

    private static async Task ShowResetListAsync(IReplHost host, CancellationToken ct)
    {
        var authStore = host.Services.GetRequiredService<AuthStore>();
        var keysResult = await authStore.ListApiKeysAsync(ct).ConfigureAwait(false);
        if (keysResult.IsFailure)
        {
            host.Bridge.AppendSystemLine($"! {keysResult.Error}");
            host.Palette.Hide();
            host.WakeUp();
            return;
        }

        var providerItems = keysResult.Value.Keys
            .Select(k => new CommandItem(k, k, string.Empty, string.Empty, "Providers"))
            .ToList();

        host.Palette.PushFrame(new PaletteFrame(
            "Remove API Key", "auth / reset", providerItems,
            OnCommitAsync: (providerItem, frameCt) => RemoveKeyAsync(host, authStore, providerItem, frameCt)));
        host.WakeUp();
    }

    private static async Task RemoveKeyAsync(IReplHost host, AuthStore authStore, CommandItem providerItem, CancellationToken ct)
    {
        var result = await authStore.RemoveApiKeyAsync(providerItem.Id, ct).ConfigureAwait(false);
        host.Bridge.AppendSystemLine(result.IsSuccess
            ? $"✓ API key removed for {providerItem.Id}"
            : $"✗ Failed: {result.Error}");
        host.Palette.Hide();
        host.WakeUp();
    }

    private static void ShowSetList(IReplHost host)
    {
        var providerItems = ProviderPresets.All
            .Select(p => new CommandItem(p.Id, p.DisplayName, p.Description, string.Empty, "Providers"))
            .ToList();

        host.Palette.PushFrame(new PaletteFrame(
            "Set API Key", "auth / set", providerItems,
            OnCommitAsync: (providerItem, frameCt) => ShowKeyInput(host, providerItem)));
        host.WakeUp();
    }

    private static Task ShowKeyInput(IReplHost host, CommandItem providerItem)
    {
        var authStore = host.Services.GetRequiredService<AuthStore>();
        host.Palette.PushFrame(new PaletteFrame(
            $"auth / set / {providerItem.Id}", $"auth / set / {providerItem.Id}",
            [],
            IsInput: true,
            InputPlaceholder: "paste API key...",
            OnInputSubmitAsync: (key, frameCt) => SaveKeyAsync(host, authStore, providerItem.Id, key, frameCt)));
        host.WakeUp();
        return Task.CompletedTask;
    }

    private static async Task SaveKeyAsync(IReplHost host, AuthStore authStore, string providerId, string key, CancellationToken ct)
    {
        var result = await authStore.SetApiKeyAsync(providerId, key, ct).ConfigureAwait(false);
        host.Bridge.AppendSystemLine(result.IsSuccess
            ? $"✓ API key saved for {providerId}"
            : $"✗ Failed: {result.Error}");
        host.Palette.Hide();
        host.WakeUp();
    }
}
