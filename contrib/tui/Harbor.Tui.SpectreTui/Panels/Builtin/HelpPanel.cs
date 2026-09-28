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
///     Reads the active <see cref="ChatKeyMap" /> from the supplied
///     <see cref="PanelContext.Services" /> if present (the host registers a singleton
///     <c>ChatKeyMap</c> per interactive renderer). Hotkey rows come from the shared
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
        if (ctx.Services is null)
        {
            p.Lines.Add(TextLine.FromMarkup("  [grey](no service provider)[/]"));
        }
        else
        {
            var registry = ctx.Services.GetService(typeof(IPanelRegistry)) as IPanelRegistry;
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
            if (ctx.Services?.GetService(typeof(UiStore)) is UiStore store)
                store.Dispatch(new AppMsg.TogglePanel(Id));
            return true;
        }
        return false;
    }
}
