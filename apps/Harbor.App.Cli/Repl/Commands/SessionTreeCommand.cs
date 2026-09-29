using Harbor.Abstractions.Sessions;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Overlays;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>
///     Session branch tree as a human-readable panel: title-first rows (the
///     full hash lives dimmed in the detail line), grouped by date bucket,
///     fuzzy-filtered as you type, Enter switches straight into the session
///     (same <c>SwitchToSessionAsync</c> the jump palette confirms through).
///     The palette's built-in <c>… +N</c> overflow counter caps huge stores.
///     Non-interactive output stays textual (<c>harbor sessions tree</c> and
///     the legacy <c>/tree</c> dispatcher path both keep
///     <c>SessionTreeRunner</c>).
/// </summary>
internal sealed class SessionTreeCommand : IReplCommand
{
    public string Id => "tree";
    public IReadOnlyList<string> Aliases => [];
    public string Title => "Branch Tree";
    public string Description => "show session fork / lineage tree";
    public string Group => "Sessions";

    public async Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;
        var store = host.SessionStore;
        if (store is null)
        {
            host.Bridge.AppendSystemLine("⇄ переключение недоступно: хост без хранилища сессий");
            host.WakeUp();
            return;
        }

        var result = await store.ListAsync().ConfigureAwait(false);
        if (result.IsFailure)
        {
            host.Bridge.AppendSystemLine($"! {result.Error}");
            host.WakeUp();
            return;
        }

        if (result.Value.Count == 0)
        {
            host.Bridge.AppendSystemLine("No sessions.");
            host.WakeUp();
            return;
        }

        var seeds = new List<SessionTreeSeed>(result.Value.Count);
        for (int i = 0; i < result.Value.Count; i++)
        {
            #pragma warning disable CFE0001
            // CFE0001 baseline: docs/ROP-API-INVENTORY.md 5.
            // Guard upstream is an early return.
            var s = result.Value[i];
            #pragma warning restore CFE0001
            seeds.Add(new SessionTreeSeed(
                s.Id, s.Title, s.Directory, s.Agent, s.Model,
                s.CreatedAt, s.UpdatedAt, s.ParentSessionId,
                s.Id.Equals(host.SessionModel.Id, StringComparison.Ordinal)));
        }

        var model = new SessionTreePanelModel();
        model.Show(SessionTreeModel.BuildEntries(seeds));

        var items = new List<CommandItem>(model.Results.Count);
        for (int i = 0; i < model.Results.Count; i++)
        {
            var e = model.Results[i];
            items.Add(new CommandItem(e.SessionId, e.Title, e.Detail, string.Empty, e.Group));
        }

        host.Palette.PushFrame(new PaletteFrame(
            "Session Tree", "sessions / tree", items,
            OnCommitAsync: (selected, frameCt) => SwitchAsync(host, selected, frameCt),
            PreserveOrder: true));
        host.WakeUp();
    }

    private static async Task SwitchAsync(IReplHost host, CommandItem selected, CancellationToken ct)
    {
        await host.SwitchToSessionAsync(selected.Id, ct).ConfigureAwait(false);
        host.Palette.Hide();
        host.WakeUp();
    }
}
