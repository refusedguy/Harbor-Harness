using Harbor.Tui.Termina.Handlers;
using Harbor.Tui.Termina.Rendering;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
namespace Harbor.Tui.Termina.Views;
/// <summary>
///     Projects an immutable <see cref="UiScreenModel" /> snapshot into the
///     Termina-rendered chat transcript. Each rendered line is
///     expanded through <see cref="TerminaMarkdownRenderer" /> with its
///     role color, prefixed by the <c>─ role ─</c> header band. Streaming
///     output is already embedded in the projected lines.
/// </summary>
public sealed class ChatView
{
    /// <summary>
    ///     Build the list of display strings for the supplied screen model.
    ///     The caller appends each line to the Termina <c>StreamingTextNode</c>.
    /// </summary>
    public IReadOnlyList<string> Build(UiScreenModel screen)
    {
        var outp = new List<string>(screen.Transcript.RenderedLines.Count + 8);
        foreach (var line in screen.Transcript.RenderedLines)
        {
            if (line.Kind == UiLineKind.Thinking)
                outp.Add(TerminaMarkdownRenderer.RenderHeader(ChatRole.Thinking));
            else
                outp.Add(TerminaMarkdownRenderer.RenderHeader(ChatRole.Assistant));

            foreach (string body in TerminaMarkdownRenderer.RenderBody(ChatRole.Assistant, string.Join(string.Empty, line.Spans.Select(s => s.Text)), 80 - 2))
                outp.Add("  " + body);
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
            return $"▌ generating... {s.Active.TextBuffer.Length} chars";
        if (!string.IsNullOrEmpty(s.Active.ThinkBuffer))
            return $"▌ thinking... {s.Active.ThinkBuffer.Length} chars";
        return "▌ thinking...";
    }
}