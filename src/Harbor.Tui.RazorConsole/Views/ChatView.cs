using Harbor.Tui.RazorConsole.Handlers;
using Harbor.Tui.RazorConsole.Rendering;
using Harbor.Ui.Framework.State;
using UiChatLine = Harbor.Ui.Framework.State.ChatLine;

namespace Harbor.Tui.RazorConsole.Views;
/// <summary>
///     Projects an immutable <see cref="UiState" /> snapshot into a list of
///     Spectre markup strings for RazorConsole. Each <see cref="UiChatLine" />
///     is expanded through <see cref="RazorMarkdownRenderer" /> with its
///     role color, prefixed by the <c>─ role ─</c> header band. Streaming
///     output (active text + thinking) is appended as a live block.
/// </summary>
public sealed class ChatView
{
    /// <summary>Build the list of display strings for the supplied state.</summary>
    public IReadOnlyList<string> Build(UiState s, int historyWidth)
    {
        var outp = new List<string>(s.Lines.Length + 8);
        int bodyWidth = Math.Max(0, historyWidth - 2);
        foreach (var line in ScrollHandler.VisibleSlice(s))
        {
            if (line.Role is not ChatRole.ToolResult)
                outp.Add(RazorMarkdownRenderer.RenderHeader(line.Role));
            foreach (string body in RazorMarkdownRenderer.RenderBody(line.Role, line.Text, bodyWidth))
                outp.Add(body);
            outp.Add(" ");
        }

        if (s.IsStreaming)
        {
            if (!string.IsNullOrEmpty(s.Active.ThinkBuffer))
            {
                outp.Add(RazorMarkdownRenderer.RenderHeader(ChatRole.Thinking));
                foreach (string b in RazorMarkdownRenderer.RenderBody(ChatRole.Thinking, s.Active.ThinkBuffer, bodyWidth))
                    outp.Add(b);
            }
            if (!string.IsNullOrEmpty(s.Active.TextBuffer))
            {
                outp.Add(RazorMarkdownRenderer.RenderHeader(ChatRole.Assistant));
                foreach (string b in RazorMarkdownRenderer.RenderBody(ChatRole.Assistant, s.Active.TextBuffer, bodyWidth))
                    outp.Add(b);
            }
        }

        return outp;
    }

    /// <summary>Stream-bar text shown during streaming: <c>▌ generating... 1234 chars</c>.</summary>
    public static string StreamBar(UiState s)
    {
        if (!s.IsStreaming)
            return string.Empty;
        if (!string.IsNullOrEmpty(s.Active.TextBuffer))
            return $"[cyan]▌ generating... {s.Active.TextBuffer.Length} chars[/]";
        if (!string.IsNullOrEmpty(s.Active.ThinkBuffer))
            return $"[cyan]▌ thinking... {s.Active.ThinkBuffer.Length} chars[/]";
        return "[cyan]▌ thinking...[/]";
    }
}
