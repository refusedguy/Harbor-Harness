using Harbor.Tui.RazorConsole.Rendering;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Tui.RazorConsole.Views;
/// <summary>
///     Projects an immutable <see cref="UiScreenModel" /> snapshot into a list of
///     Spectre markup strings for RazorConsole. Each rendered line is
///     expanded through <see cref="RazorMarkdownRenderer" /> with its
///     role color. Streaming output is already embedded in the projected lines
///     (<c>DefaultUiProjector</c> appends the streaming tail to
///     <see cref="UiTranscriptModel.RenderedLines" />), so this view never reads
///     the raw streaming buffers — the Termina and TerminalGui copies do the same
///     (issue #554).
/// </summary>
public sealed class ChatView
{
    /// <summary>
    ///     Build the list of display strings for the supplied screen model.
    ///     <paramref name="bodyWidth" /> is the wrap width handed to the markdown
    ///     renderer (tables); the trio shares this signature so a divergence in how
    ///     a line is built cannot hide in one backend (issue #554).
    /// </summary>
    public IReadOnlyList<string> Build(UiScreenModel screen, int bodyWidth = 78)
    {
        var outp = new List<string>(screen.Transcript.RenderedLines.Count + 8);
        foreach (var line in screen.Transcript.RenderedLines)
        {
            var role = line.Kind == UiLineKind.Thinking ? ChatRole.Thinking : ChatRole.Assistant;
            outp.Add(RazorMarkdownRenderer.RenderHeader(role));
            foreach (string body in RazorMarkdownRenderer.RenderBody(role, string.Join(string.Empty, line.Spans.Select(s => s.Text)), bodyWidth))
                outp.Add(body);
            outp.Add(" ");
        }

        return outp;
    }

    /// <summary>Stream-bar text shown during streaming: <c>▌ generating... 1234 chars</c>.</summary>
    public static string StreamBar(UiState s)
    {
        if (!s.Chat.IsStreaming)
            return string.Empty;
        if (!string.IsNullOrEmpty(s.Chat.Active.TextBuffer))
            return $"[cyan]▌ generating... {s.Chat.Active.TextBuffer.Length} chars[/]";
        if (!string.IsNullOrEmpty(s.Chat.Active.ThinkBuffer))
            return $"[cyan]▌ thinking... {s.Chat.Active.ThinkBuffer.Length} chars[/]";
        return "[cyan]▌ thinking...[/]";
    }
}
