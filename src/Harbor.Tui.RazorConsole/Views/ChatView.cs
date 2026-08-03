using Harbor.Tui.RazorConsole.Handlers;
using Harbor.Tui.RazorConsole.Rendering;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using UiChatLine = Harbor.Ui.Framework.State.ChatLine;

namespace Harbor.Tui.RazorConsole.Views;
/// <summary>
///     Projects an immutable <see cref="UiScreenModel" /> snapshot into a list of
///     Spectre markup strings for RazorConsole. Each rendered line is
///     expanded through <see cref="RazorMarkdownRenderer" /> with its
///     role color. Streaming output is already embedded in the projected lines.
/// </summary>
public sealed class ChatView
{
    /// <summary>Build the list of display strings for the supplied screen model.</summary>
    public IReadOnlyList<string> Build(UiScreenModel screen)
    {
        var outp = new List<string>(screen.Transcript.RenderedLines.Count + 8);
        foreach (var line in screen.Transcript.RenderedLines)
        {
            var role = line.Kind == UiLineKind.Thinking ? ChatRole.Thinking : ChatRole.Assistant;
            outp.Add(RazorMarkdownRenderer.RenderHeader(role));
            foreach (string body in RazorMarkdownRenderer.RenderBody(role, string.Join(string.Empty, line.Spans.Select(s => s.Text)), 80 - 2))
                outp.Add(body);
            outp.Add(" ");
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