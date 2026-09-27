using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>
///     Panels picker: every registered panel as a title-first row (live
///     visible/hidden state dimmed in the detail), fuzzy-filtered as you
///     type. Enter toggles the highlighted panel through the TEA store
///     (same <c>UiMsg.TogglePanel</c> the hotkeys route through) and closes
///     the picker. The hotkey-free entry point for terminals that swallow
///     panel chords. Non-interactive output stays textual
///     (<c>harbor panels</c> does not exist; the legacy dispatcher path is
///     untouched).
/// </summary>
internal sealed class PanelsCommand : IReplCommand
{
    public string Id => "panels";
    public IReadOnlyList<string> Aliases => ["panel"];
    public string Title => "Panels";
    public string Description => "list panels, toggle visible/hidden";
    public string Group => "Sessions";

    public Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;
        var registry = host.PanelRegistry;
        if (registry is null)
        {
            host.Bridge.AppendSystemLine("Panels: not available in this build.");
            host.WakeUp();
            return Task.CompletedTask;
        }

        var view = new PanelRegistryView(registry.All, host.Store.State);
        var items = new List<CommandItem>(view.Providers.Count);
        for (int i = 0; i < view.Providers.Count; i++)
        {
            var p = view.Providers[i];
            string state = view.GetState(p.Id) == TuiPanelState.Hidden ? "hidden" : "visible";
            items.Add(new CommandItem(p.Id, p.Title, $"{p.Id} · {state}", string.Empty, "Panels"));
        }

        host.Palette.PushFrame(new PaletteFrame(
            "Panels", "panels", items,
            OnCommitAsync: (selected, frameCt) => ToggleAsync(host, selected, frameCt)));
        host.WakeUp();
        return Task.CompletedTask;
    }

    private static Task ToggleAsync(IReplHost host, CommandItem selected, CancellationToken ct)
    {
        host.Store.Dispatch(new UiMsg.TogglePanel(selected.Id));
        host.Palette.Hide();
        host.WakeUp();
        return Task.CompletedTask;
    }
}
