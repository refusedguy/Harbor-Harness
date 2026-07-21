using Harbor.Tui.TerminalGui.Handlers;
using Harbor.Tui.TerminalGui.Rendering;
using Harbor.Ui.Framework.State;
namespace Harbor.Tui.TerminalGui.Views;
/// <summary>
///     Projects an immutable <see cref="UiState" /> snapshot into the
///     Terminal.Gui-rendered chat transcript. Each <see cref="ChatLine" />
///     is expanded through <see cref="TerminalGuiMarkdownRenderer" /> with
///     its role color, prefixed by the <c>─ role ─</c> header band.
///     Streaming output (active text + thinking) is appended as a live block.
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
                outp.Add(TerminalGuiMarkdownRenderer.RenderHeader(line.Role));
            foreach (string body in TerminalGuiMarkdownRenderer.RenderBody(line.Role, line.Text, bodyWidth))
                outp.Add("  " + body);
            outp.Add(" ");
        }

        if (s.IsStreaming)
        {
            if (!string.IsNullOrEmpty(s.Active.ThinkBuffer))
            {
                outp.Add(TerminalGuiMarkdownRenderer.RenderHeader(ChatRole.Thinking));
                foreach (string b in TerminalGuiMarkdownRenderer.RenderBody(ChatRole.Thinking, s.Active.ThinkBuffer, bodyWidth))
                    outp.Add("  " + b);
            }
            if (!string.IsNullOrEmpty(s.Active.TextBuffer))
            {
                outp.Add(TerminalGuiMarkdownRenderer.RenderHeader(ChatRole.Assistant));
                foreach (string b in TerminalGuiMarkdownRenderer.RenderBody(ChatRole.Assistant, s.Active.TextBuffer, bodyWidth))
                    outp.Add("  " + b);
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
            return $"▌ generating... {s.Active.TextBuffer.Length} chars";
        if (!string.IsNullOrEmpty(s.Active.ThinkBuffer))
            return $"▌ thinking... {s.Active.ThinkBuffer.Length} chars";
        return "▌ thinking...";
    }
}
