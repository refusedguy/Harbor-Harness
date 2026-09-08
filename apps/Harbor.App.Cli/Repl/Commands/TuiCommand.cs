using Harbor.Application.Configuration;
using Harbor.Tui.CellForge.Widgets;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>TUI switcher: persist the default TUI renderer.</summary>
internal sealed class TuiCommand : IReplCommand
{
    public string Id => "tui";
    public IReadOnlyList<string> Aliases => [];
    public string Title => "TUI";
    public string Description => "set default TUI renderer";
    public string Group => "Runtime";

    public async Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;
        var configStore = host.Services.GetRequiredService<IConfigStore>();
        var configResult = await configStore.LoadAsync(ct).ConfigureAwait(false);
        if (configResult.IsFailure)
        {
            host.Bridge.AppendSystemLine($"! {configResult.Error}");
            host.WakeUp();
            return;
        }

        var current = configResult.Value.Tui;
        var items = new[] { "cellforge", "plain", "ansi", "spectre", "fullscreen" }
            .Select(id => new CommandItem(
                id,
                id,
                id == current ? "Currently active" : string.Empty,
                string.Empty,
                "TUI"))
            .ToList();

        host.Palette.PushFrame(new PaletteFrame(
            "Select TUI", "tui", items,
            OnCommitAsync: (item, frameCt) => ApplyTuiAsync(host, configStore, item, frameCt)));
        host.WakeUp();
    }

    private static async Task ApplyTuiAsync(IReplHost host, IConfigStore configStore, CommandItem item, CancellationToken ct)
    {
        var result = await configStore.UpdateAsync(c =>
        {
            c.Tui = item.Id;
            return c;
        }, ct).ConfigureAwait(false);

        host.Bridge.AppendSystemLine(result.IsSuccess
            ? $"✓ TUI set to {item.Id} (applies on restart or via /renderer)"
            : $"✗ Failed: {result.Error}");
        host.Palette.Hide();
        host.WakeUp();
    }
}
