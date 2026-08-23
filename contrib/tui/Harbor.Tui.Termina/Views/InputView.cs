using System.Text;
using Harbor.Tui.Termina.Rendering;
using Harbor.Ui.Framework.State;
using TerminaColor = Termina.Terminal.Color;

namespace Harbor.Tui.Termina.Views;
/// <summary>
///     Projects <see cref="InputModel" /> into a display string with a
///     caret, history navigation hint, and slash-command autocomplete marker.
///     Multi-line content is preserved; the live edit cursor is shown as
///     <c>▍</c> so the user always sees where the next char lands.
/// </summary>
public sealed class InputView
{
    /// <summary>Render the input box body for the supplied state.</summary>
    public string Build(UiState s)
    {
        var sb = new StringBuilder(64 + s.Input.Text.Length);
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.Cyan, "❯ "));

        bool slash = s.Input.Text.StartsWith('/');
        var color = slash ? TerminaColor.Yellow : TerminaColor.White;
        sb.Append(TerminaMarkdownRenderer.Ansi(color, s.Input.Text));
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.Cyan, "▍"));

        if (slash && !s.Input.Text.EndsWith(' '))
        {
            string? match = ChatCommands.Slash.FirstOrDefault(c =>
                c.StartsWith(s.Input.Text, StringComparison.OrdinalIgnoreCase) && c != s.Input.Text);
            if (match is not null)
                sb.Append(' ').Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, $"↹ {match}"));
        }

        return sb.ToString();
    }

    /// <summary>Hint line shown beneath the input box.</summary>
    public static string Hint(UiState s) => s.Focus == FocusMode.Chat
        ? "F2 → input  ↑/↓ scroll  PgUp/PgDn page  Home/End top/bottom  Esc quit"
        : "Enter send  Alt+↑/↓ history  Tab complete  Esc quit  F2 → chat";
}
