using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Tui.CellForge.Panels;

/// <summary>
///     Cell-native help panel: shared <see cref="HelpKeymap"/> hotkey rows plus one
///     row per registered panel (from <see cref="IPanelRegistry"/> in
///     <c>ctx.Services</c>) plus the slash command list. <c>?</c> while focused
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
        // #63 legitimate: framework-created panels cannot take DI — the
        // registry lookup stays on ctx.Services (UiStore travels via ctx.Store).
        var registry = ctx.Services?.GetService<IPanelRegistry>();
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
            // #63: explicit store from the host (no Services lookup).
            if (ctx.Store is UiStore store)
            {
                _ = store.Dispatch(new AppMsg.TogglePanel(Id));
            }

            return true;
        }

        return false;
    }
}
