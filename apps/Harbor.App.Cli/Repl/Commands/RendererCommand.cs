using CSharpFunctionalExtensions;
using Harbor.Abstractions.Extensions;
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
        Ensure.NotNull(ctx);
        var host = ctx.Host;
        var pipeline = host.RendererPipeline;
        if (pipeline is null)
        {
            host.Bridge.AppendSystemLine("⇄ renderer swap недоступен: хост без IRendererPipeline");
            host.WakeUp();
            return Task.CompletedTask;
        }

        // #1509: absence is Maybe, not an exception — the disposed pipeline
        // has no active backend, and that is an expected state, not an invariant.
        Maybe<string> activeBackend = pipeline.TryGetBackendId();
        var items = pipeline.AvailableBackends
            .Select(id => new CommandItem(
                id,
                id,
                activeBackend.Match(backendId => backendId == id, static () => false) ? "Currently active" : string.Empty,
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
