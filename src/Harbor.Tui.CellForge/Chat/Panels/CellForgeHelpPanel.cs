using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native help panel: shared <see cref="HelpKeymap"/> hotkey rows plus one
///     row per registered panel (from <see cref="IPanelRegistry"/> on
///     <see cref="PanelServices"/>) plus the slash command list. <c>?</c> while
///     focused
///     dispatches <c>AppMsg.TogglePanel("help")</c>.
/// </summary>
public sealed class CellForgeHelpPanel : CellForgePanelBase
{
    /// <inheritdoc />
    public override string Id => "help";

    /// <inheritdoc />
    public override string Title => "Help";

    /// <inheritdoc />
    public override TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;

    /// <inheritdoc />
    public override int DefaultSize => 48;

    /// <inheritdoc />
    public override object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var rows = new List<string>(32);
        rows.Add("Harbor — keymap & panels");
        rows.Add(PanelText.Separator);
        rows.Add("Hotkeys");
        foreach (HelpKeymap.Entry hotkey in HelpKeymap.Rows)
        {
            rows.Add($"  {hotkey.Key,-12} {hotkey.Description}");
        }

        rows.Add(string.Empty);
        rows.Add("Panels");
        // #470: the registry is an explicitly typed field on PanelServices,
        // filled by the composition root (UiStore travels as Deps.Store).
        var registry = ctx.Deps.PanelRegistry;
        if (registry is null || registry.All.Count == 0)
        {
            rows.Add("  (no panels)");
        }
        else
        {
            var all = registry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var panel = all[i];
                bool isFocused = panel.Id == ctx.State.Ui.FocusedPanelId;
                var state = ctx.State.Ui.PanelStates.TryGetValue(panel.Id, out var ps) ? ps : TuiPanelState.Hidden;
                string stateText = isFocused ? "*focused*" : state == TuiPanelState.Hidden ? "hidden" : "visible";
                string slot = i < 9 ? $"Alt+{i + 1}" : "      ";
                rows.Add($"  {slot}  {panel.Id}  {panel.Title}  {stateText}");
            }
        }

        rows.Add(string.Empty);
        rows.Add("Slash commands");
        foreach (string cmd in ChatCommands.Slash)
        {
            rows.Add($"  {cmd}");
        }

        rows.Add(string.Empty);
        rows.Add("Press ? to close this panel.");
        return PanelText.Clip(rows, ctx.Width, ctx.Height);
    }

    /// <inheritdoc />
    public override bool OnKey(UiKey key, PanelContext ctx)
    {
        if (key.Code == UiKeyCode.Char && key.Character == '?')
        {
            // #833: a chord is not a toggle. The panel holds no text, so it has
            // no claim on a modified rune — Ctrl+? belongs to the host. The gate
            // admits Shift, because `?` IS a shifted rune and a strict
            // "no modifiers" rule would refuse this panel's own key.
            if (key.Mods.IsCommandModifier())
            {
                return false;
            }

            // #470: the store is an explicit field on PanelServices — no lookup.
            if (ctx.Deps.Store is { } store)
            {
                _ = store.Dispatch(new AppMsg.TogglePanel(Id));
            }

            return true;
        }

        return false;
    }
}
