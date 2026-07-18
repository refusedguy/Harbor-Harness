using Harbor.Tui.Abstractions.Panels;
using Harbor.Tui.Abstractions.State;
using Harbor.Tui.SpectreTui.View;
using Spectre.Console;
using Spectre.Tui;
namespace Harbor.Tui.SpectreTui.Panels.Builtin;

/// <summary>
///     Builtin panel that shows the global keymap, registered panel list with their
///     hotkeys (Alt+1..Alt+9), and the available slash commands. Toggled with <c>?</c>.
/// </summary>
/// <remarks>
///     Reads the active <see cref="ChatKeyMap" /> from the supplied
///     <see cref="PanelContext.Services" /> if present (the host registers a singleton
///     <c>ChatKeyMap</c> per interactive renderer). When no keymap is available, falls
///     back to a built-in table matching <see cref="ChatKeyMap" />'s default entries.
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
        p.Lines.Add(TextLine.FromMarkup("  [grey]Alt+1..9[/]   toggle Nth panel"));
        p.Lines.Add(TextLine.FromMarkup("  [grey]Ctrl+Tab[/]   cycle panel focus"));
        p.Lines.Add(TextLine.FromMarkup("  [grey]Ctrl+↑/↓[/]   grow / shrink focused panel"));
        p.Lines.Add(TextLine.FromMarkup("  [grey]q / Esc[/]    return focus to chat"));
        p.Lines.Add(TextLine.FromMarkup("  [grey]?[/]          toggle this help panel"));
        p.Lines.Add(TextLine.FromMarkup("  [grey]F2[/]         toggle input/chat focus"));
        p.Lines.Add(TextLine.FromMarkup("  [grey]Ctrl+L[/]     clear transcript"));
        p.Lines.Add(TextLine.FromMarkup("  [grey]Ctrl+C[/]     abort running agent"));
        p.Lines.Add(TextLine.FromMarkup("  [grey]Esc[/]        quit"));
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
                    bool isFocused = panel.Id == ctx.State.FocusedPanelId;
                    TuiPanelState s = ctx.State.PanelStates.TryGetValue(panel.Id, out var ps)
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
        foreach (var cmd in ChatCommands.Slash)
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
                store.Dispatch(new UiMsg.TogglePanel(Id));
            return true;
        }
        return false;
    }
}
