using System.Text;
using Harbor.Ui.Framework.State;
using Harbor.Tui.RazorConsole.Rendering;

namespace Harbor.Tui.RazorConsole.Views;

/// <summary>
///     Projects <see cref="InputModel" /> into a Spectre markup string with a
///     caret and slash-command autocomplete marker.
/// </summary>
public sealed class InputView
{
    /// <summary>Render the input box body for the supplied state.</summary>
    public string Build(UiState s)
    {
        var sb = new StringBuilder(64 + s.Input.Text.Length);
        bool slash = s.Input.Text.StartsWith('/');
        string color = slash ? "yellow" : "white";
        sb.Append($"[cyan]❯ [/][{color}]{RazorMarkdownRenderer.Escape(s.Input.Text)}[/][cyan]▍[/]");

        if (slash && !s.Input.Text.EndsWith(' '))
        {
            string? match = ChatCommands.Slash.FirstOrDefault(c =>
                c.StartsWith(s.Input.Text, StringComparison.OrdinalIgnoreCase) && c != s.Input.Text);
            if (match is not null)
                sb.Append($" [grey]↹ {RazorMarkdownRenderer.Escape(match)}[/]");
        }

        return sb.ToString();
    }

    /// <summary>Hint line shown beneath the input box.</summary>
    public static string Hint(UiState s) => s.Focus == FocusMode.Chat
        ? "F2 → input  ↑/↓ scroll  PgUp/PgDn page  Home/End top/bottom  Esc quit"
        : "Enter send  Alt+↑/↓ history  Tab complete  Esc quit  F2 → chat";
}
