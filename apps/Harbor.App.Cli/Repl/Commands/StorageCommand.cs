using Harbor.Application.Configuration;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>Storage switcher: persist the session storage backend.</summary>
internal sealed class StorageCommand : IReplCommand
{
    public string Id => "storage";
    public IReadOnlyList<string> Aliases => [];
    public string Title => "Storage";
    public string Description => "set session storage backend";
    public string Group => "Runtime";

    public async Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;
        var configStore = host.ConfigStore;
        var configResult = await configStore.LoadAsync(ct).ConfigureAwait(false);
        if (configResult.IsFailure)
        {
            host.Bridge.AppendSystemLine($"! {configResult.Error}");
            host.WakeUp();
            return;
        }

        var current = configResult.Value.Storage;
        var items = new[] { "jsonl", "sqlite", "memory" }
            .Select(id => new CommandItem(
                id,
                id,
                id == current ? "Currently active" : string.Empty,
                string.Empty,
                "Storage"))
            .ToList();

        host.Palette.PushFrame(new PaletteFrame(
            "Select Storage", "storage", items,
            OnCommitAsync: (item, frameCt) => ApplyStorageAsync(host, configStore, item, frameCt)));
        host.WakeUp();
    }

    private static async Task ApplyStorageAsync(IReplHost host, IConfigStore configStore, CommandItem item, CancellationToken ct)
    {
        var result = await configStore.UpdateAsync(c =>
        {
            c.Storage = item.Id;
            return c;
        }, ct).ConfigureAwait(false);

        host.Bridge.AppendSystemLine(result.IsSuccess
            ? $"✓ Storage set to {item.Id}"
            : $"✗ Failed: {result.Error}");
        host.Palette.Hide();
        host.WakeUp();
    }
}
