using System.Text;
using Harbor.Ui.Framework.State;
using Harbor.Tui.Termina.Handlers;
using Harbor.Tui.Termina.Rendering;

namespace Harbor.Tui.Termina.Views;

/// <summary>
///     Projects an immutable <see cref="UiState" /> snapshot into the
///     Termina-rendered chat transcript. Each <see cref="ChatLine" /> is
///     expanded through <see cref="TerminaMarkdownRenderer" /> with its
///     role color, prefixed by the <c>─ role ─</c> header band. Streaming
///     output (active text + thinking buffers) is appended as a live block.
/// </summary>
public sealed class ChatView
{
    /// <summary>
    ///     Build the list of display strings for the supplied state. The caller
    ///     appends each line to the Termina <c>StreamingTextNode</c>.
    /// </summary>
    public IReadOnlyList<string> Build(UiState s, int historyWidth)
    {
        var outp = new List<string>(s.Lines.Length + 8);
        int bodyWidth = Math.Max(0, historyWidth - 2);
        foreach (var line in ScrollHandler.VisibleSlice(s))
        {
            if (line.Role is not ChatRole.ToolResult)
                outp.Add(TerminaMarkdownRenderer.RenderHeader(line.Role));
            foreach (var body in TerminaMarkdownRenderer.RenderBody(line.Role, line.Text, bodyWidth))
                outp.Add("  " + body);
            outp.Add(" ");
        }

        // Live streaming preview (text + thinking) for the current turn.
        if (s.IsStreaming)
        {
            if (!string.IsNullOrEmpty(s.Active.ThinkBuffer))
            {
                outp.Add(TerminaMarkdownRenderer.RenderHeader(ChatRole.Thinking));
                foreach (var b in TerminaMarkdownRenderer.RenderBody(ChatRole.Thinking, s.Active.ThinkBuffer, bodyWidth))
                    outp.Add("  " + b);
            }
            if (!string.IsNullOrEmpty(s.Active.TextBuffer))
            {
                outp.Add(TerminaMarkdownRenderer.RenderHeader(ChatRole.Assistant));
                foreach (var b in TerminaMarkdownRenderer.RenderBody(ChatRole.Assistant, s.Active.TextBuffer, bodyWidth))
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
