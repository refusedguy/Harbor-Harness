using Harbor.Tui.TerminalGui.Rendering;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
namespace Harbor.Tui.TerminalGui.Views;
/// <summary>
///     Projects an immutable <see cref="UiScreenModel" /> snapshot into the
///     Terminal.Gui-rendered chat transcript. Each rendered line is expanded
///     through <see cref="TerminalGuiMarkdownRenderer" /> with its role color,
///     prefixed by the <c>─ role ─</c> header band. Streaming output arrives
///     through the projection: <c>DefaultUiProjector</c> appends the live
///     thinking + text tail to <see cref="UiTranscriptModel.RenderedLines" />
///     (issue #554).
/// </summary>
/// <remarks>
///     This view used to take the raw <see cref="UiState" />, slice the
///     transcript with <c>ScrollHandler.VisibleSlice</c> and append
///     <c>Active.ThinkBuffer</c> / <c>Active.TextBuffer</c> itself — the only one
///     of the three that bypassed the projection layer, which meant the streaming
///     tail had two independent owners. It now consumes the same projected model
///     as the RazorConsole and Termina copies, so "does streaming belong in the
///     transcript" has exactly one answer per backend.
/// </remarks>
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
            outp.Add(TerminalGuiMarkdownRenderer.RenderHeader(role));
            foreach (string body in TerminalGuiMarkdownRenderer.RenderBody(role, string.Join(string.Empty, line.Spans.Select(s => s.Text)), bodyWidth))
                outp.Add("  " + body);
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
            return $"▌ generating... {s.Chat.Active.TextBuffer.Length} chars";
        if (!string.IsNullOrEmpty(s.Chat.Active.ThinkBuffer))
            return $"▌ thinking... {s.Chat.Active.ThinkBuffer.Length} chars";
        return "▌ thinking...";
    }
}
