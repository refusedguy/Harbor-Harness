using Harbor.Hosting.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>Renderer switcher: swap the active render backend live.</summary>
internal sealed class RendererCommand : IReplCommand
{
    public string Id => "renderer";
    public IReadOnlyList<string> Aliases => [];
    public string Title => "Renderer";
    public string Description => "swap render backend live";
    public string Group => "Runtime";

    public Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;
        var pipeline = host.RendererPipeline;
        if (pipeline is null)
        {
            host.Bridge.AppendSystemLine("⇄ renderer swap недоступен: хост без IRendererPipeline");
            host.WakeUp();
            return Task.CompletedTask;
        }

        var items = pipeline.AvailableBackends
            .Select(id => new CommandItem(
                id,
                id,
                id == pipeline.CurrentBackendId ? "Currently active" : string.Empty,
                string.Empty,
                "Renderer"))
            .ToList();

        host.Palette.PushFrame(new PaletteFrame(
            "Select Renderer", "renderer", items,
            OnCommitAsync: (item, frameCt) => ApplyRendererAsync(host, pipeline, item, frameCt)));
        host.WakeUp();
        return Task.CompletedTask;
    }

    private static async Task ApplyRendererAsync(IReplHost host, IRendererPipeline pipeline, CommandItem item, CancellationToken ct)
    {
        bool swapped = await pipeline.SwapRendererAsync(item.Id, ct).ConfigureAwait(false);
        host.Bridge.AppendSystemLine(swapped
            ? $"✓ Renderer swapped to {item.Id}"
            : $"✗ Failed to swap renderer to {item.Id}");
        host.Palette.Hide();
        host.WakeUp();
    }
}
