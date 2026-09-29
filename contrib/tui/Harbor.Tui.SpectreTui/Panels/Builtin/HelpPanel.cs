using Harbor.Tui.SpectreTui.View;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.Panels.Builtin;
/// <summary>
///     Builtin panel that shows the global keymap, registered panel list with their
///     hotkeys (Alt+1..Alt+9), and the available slash commands. Toggled with <c>?</c>.
/// </summary>
/// <remarks>
///     Reads the registered panels from the <see cref="PanelServices" /> the host
///     attached to the <see cref="PanelContext" /> (#470 — a typed field, not a
///     service locator). Hotkey rows come from the shared
///     <see cref="HelpKeyMap" /> table (matching <see cref="ChatKeyMap" />'s default
///     entries) so the Spectre and CellForge help panels cannot drift apart.
/// </remarks>
public sealed class HelpPanel : IPanelProvider
{
    /// <inheritdoc />
    public string Id => "help";

    /// <inheritdoc />
    public string Title => "Help";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;

    /// <inheritdoc />
    public int DefaultSize => 48;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        var p = new Paragraph().Alignment(Justify.Left);
        p.Lines.Add(TextLine.FromMarkup("[bold cyan]Harbor — keymap & panels[/]"));
        p.Lines.Add(TextLine.FromMarkup("[grey]─────────────────────────────[/]"));
        p.Lines.Add(TextLine.FromMarkup("[bold]Hotkeys[/]"));
        foreach (HelpKeymap.Entry hotkey in HelpKeymap.Rows)
        {
            p.Lines.Add(TextLine.FromMarkup(
                $"  [grey]{ChatMarkup.Escape(hotkey.Key)}[/]   {hotkey.Description}"));
        }

        p.Lines.Add(TextLine.FromMarkup(string.Empty));

        // Registered panels section.
        p.Lines.Add(TextLine.FromMarkup("[bold]Panels[/]"));
        // #470: the registry is a typed field on PanelServices, not a per-frame
        // container lookup. "no bag at all" and "empty registry" stay distinct
        // messages so a mis-wired host stays visible.
        if (ctx.Services is null)
        {
            p.Lines.Add(TextLine.FromMarkup("  [grey](no service provider)[/]"));
        }
        else
        {
            var registry = ctx.Deps.PanelRegistry;
            if (registry is null || registry.All.Count == 0)
            {
                p.Lines.Add(TextLine.FromMarkup("  [grey](no panels registered)[/]"));
            }
            else
            {
                int i = 1;
                foreach (var panel in registry.All)
                {
                    // Read state directly from UiState — TEA single source of truth.
                    bool isFocused = panel.Id == ctx.State.Ui.FocusedPanelId;
                    var s = ctx.State.Ui.PanelStates.TryGetValue(panel.Id, out var ps)
                        ? ps
                        : TuiPanelState.Hidden;
                    string state = isFocused
                        ? "[aqua]*focused*[/]"
                        : s == TuiPanelState.Hidden
                            ? "[grey]hidden[/]"
                            : "[green]visible[/]";
                    string slot = i <= 9 ? $"[grey]Alt+{i}[/]" : "      ";
                    p.Lines.Add(TextLine.FromMarkup(
                        $"  {slot}  [bold]{ChatMarkup.Escape(panel.Id),-14}[/] " +
                        $"[grey]{ChatMarkup.Escape(panel.Title),-20}[/] {state}"));
                    i++;
                }
            }
        }
        p.Lines.Add(TextLine.FromMarkup(string.Empty));

        // Slash commands.
        p.Lines.Add(TextLine.FromMarkup("[bold]Slash commands[/]"));
        foreach (string cmd in ChatCommands.Slash)
            p.Lines.Add(TextLine.FromMarkup($"  [grey]{ChatMarkup.Escape(cmd)}[/]"));
        p.Lines.Add(TextLine.FromMarkup(string.Empty));
        p.Lines.Add(TextLine.FromMarkup("[grey]Press ? to close this panel.[/]"));
        return p;
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx)
    {
        // '?' while focused → toggle back off. Esc / 'q' are handled by the host
        // (ClosePanel) before the key reaches us, so we only deal with '?' here.
        if (key.Code == UiKeyCode.Char && key.Character == '?')
        {
            if (ctx.Deps.Store is { } store)
                store.Dispatch(new AppMsg.TogglePanel(Id));
            return true;
        }
        return false;
    }
}
