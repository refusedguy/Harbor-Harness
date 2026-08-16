using System.Collections.Immutable;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Spectre.Tui;
using Spectre.Tui.App;

namespace Harbor.Tui.SpectreTui.View;

internal sealed class SpectreUiViewport : IUiViewport
{
    private readonly ChatViewProjector _layout;

    public SpectreUiViewport(ChatViewProjector layout)
    {
        _layout = layout;
    }

    public void Apply(UiScreenModel screen)
    {
        _layout.IsStreaming = screen.Header.IsStreaming;
        _layout.IsReadingInput = !screen.Header.IsAgentRunning;
        _layout.StreamBuffer = GetStreamingText(screen);
        _layout.ThinkBuffer = GetThinkingText(screen);
        _layout.StatusBar = screen.StatusBar;

        var lines = ExtractLines(screen);
        _layout.SetLines(lines, screen.Header.IsStreaming, ActiveMessage.Empty, 80 - 2);

        _layout.InputText = screen.Input.Text;
        _layout.Focus = screen.Focus;
        _layout.FooterText = screen.Header.FooterText;
    }

    private static ImmutableArray<ChatLine> ExtractLines(UiScreenModel screen)
    {
        var builder = ImmutableArray.CreateBuilder<ChatLine>();
        foreach (var line in DefaultUiProjector.ExtractRenderedLines(screen))
        {
            var text = string.Join(string.Empty, line.Spans.Select(s => s.Text));
            builder.Add(new ChatLine(line.Kind == UiLineKind.Thinking ? ChatRole.Thinking : ChatRole.Assistant, text));
        }
        return builder.ToImmutable();
    }

    private static string GetStreamingText(UiScreenModel screen)
    {
        var line = screen.Transcript.RenderedLines.LastOrDefault(l => l.Kind == UiLineKind.Body && l.Spans.Count > 0);
        if (line != null && line.Spans.Count > 0)
            return line.Spans[0].Text;
        return string.Empty;
    }

    private static string GetThinkingText(UiScreenModel screen)
    {
        var line = screen.Transcript.RenderedLines.LastOrDefault(l => l.Kind == UiLineKind.Thinking && l.Spans.Count > 0);
        if (line != null && line.Spans.Count > 0)
            return line.Spans[0].Text;
        return string.Empty;
    }
}