using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Overlays;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>
///     Plugins panel: installed <c>*.cs</c> sources across both scopes with
///     name/version/status rows (loaded / installed / disabled),
///     fuzzy-filtered as you type. Opening runs a reload pass first (same as
///     the old text handler, so newly dropped files bind live); Enter
///     toggles enable/disable by renaming the source to/from
///     <c>.cs.disabled</c> (the loader glob skips those, so the toggle is
///     real) — a restart fully rebinds, hence the hint. Non-interactive
///     output stays textual (<c>harbor plugin list</c> and the legacy
///     <c>/plugins</c> dispatcher path are untouched).
/// </summary>
internal sealed class PluginsPanelCommand : IReplCommand
{
    /// <summary>Suffix marking a disabled plugin source (loader glob skips it).</summary>
    internal const string DisabledSuffix = ".disabled";

    public string Id => "plugins";
    public IReadOnlyList<string> Aliases => ["plugin"];
    public string Title => "Plugins";
    public string Description => "list installed plugins, toggle enable/disable";
    public string Group => "Config";

    public async Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;
        var reload = host.PluginReload;
        if (reload is null)
        {
            host.Bridge.AppendSystemLine("Plugins: not available in this build (HARBOR_MINIMAL).");
            host.WakeUp();
            return;
        }

        var summary = await reload.ReloadAsync(ct).ConfigureAwait(false);
        for (int i = 0; i < summary.Notes.Count; i++)
        {
            // #1055s3: reload registers nothing in-process — plugins run in
            // harbor-plugins-host — so the note is informational, not an error.
            host.Bridge.AppendSystemLine($"Plugin reload: {summary.Notes[i]}");
        }

        PushPanel(host, reload);
        host.Bridge.AppendSystemLine("Hint: plugins run in harbor-plugins-host — restart it to pick up changed scripts.");
        host.WakeUp();
    }

    private static void PushPanel(IReplHost host, Harbor.Hosting.PluginReloadService reload)
    {
        var installed = reload.ListInstalled();
        var seeds = new List<PluginSeed>(installed.Count);
        for (int i = 0; i < installed.Count; i++)
        {
            var p = installed[i];
            seeds.Add(new PluginSeed(p.Name, p.Version, p.Scope, p.FullPath, p.Enabled, Loaded: p.Version is not null));
        }

        var model = new PluginPanelModel();
        model.Show(seeds);

        if (model.Results.Count == 0)
        {
            host.Bridge.AppendSystemLine("No plugins installed (drop .cs files into ~/.harbor/plugins).");
            return;
        }

        var items = new List<CommandItem>(model.Results.Count);
        for (int i = 0; i < model.Results.Count; i++)
        {
            var e = model.Results[i];
            items.Add(new CommandItem(e.FullPath, e.Title, e.Detail, string.Empty, e.Group));
        }

        host.Palette.PushFrame(new PaletteFrame(
            "Plugins", "plugins", items,
            OnCommitAsync: (selected, frameCt) => ToggleAsync(host, reload, selected, frameCt)));
    }

    private static Task ToggleAsync(
        IReplHost host, Harbor.Hosting.PluginReloadService reload, CommandItem selected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string current = selected.Id;
        string target = ToggleTarget(current, out bool disabling);
        string name = selected.Title;

        try
        {
            File.Move(current, target);
        }
        catch (IOException ex)
        {
            host.Bridge.AppendSystemLine($"✗ Cannot toggle {name}: {ex.Message}");
            Refresh(host, reload);
            return Task.CompletedTask;
        }
        catch (UnauthorizedAccessException ex)
        {
            host.Bridge.AppendSystemLine($"✗ Cannot toggle {name}: {ex.Message}");
            Refresh(host, reload);
            return Task.CompletedTask;
        }

        host.Bridge.AppendSystemLine(disabling
            ? $"✓ Disabled {name} — restart Harbor to fully rebind."
            : $"✓ Enabled {name} — restart Harbor to fully rebind.");
        Refresh(host, reload);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Pure toggle-target math (TUnit-covered): disabling appends the
    ///     suffix, enabling strips it. The loader glob (<c>*.cs</c>) skips
    ///     suffixed files, so the rename is a real toggle.
    /// </summary>
    internal static string ToggleTarget(string current, out bool disabling)
    {
        disabling = !current.EndsWith(DisabledSuffix, StringComparison.Ordinal);
        return disabling ? current + DisabledSuffix : current[..^DisabledSuffix.Length];
    }

    private static void Refresh(IReplHost host, Harbor.Hosting.PluginReloadService reload)
    {
        // Re-push instead of mutating: frames are immutable, and hiding first
        // keeps Esc closing a fresh panel instead of a stale stacked one.
        host.Palette.Hide();
        PushPanel(host, reload);
        host.WakeUp();
    }
}
