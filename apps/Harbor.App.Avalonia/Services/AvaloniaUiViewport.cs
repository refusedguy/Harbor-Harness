using System.Collections.Immutable;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.App.Avalonia.Services;

public sealed class AvaloniaUiViewport : IUiViewport
{
    private Action<bool> _setIsStreaming = _ => { };
    private Action<bool> _setIsThinking = _ => { };
    private Action<bool> _setIsAgentRunning = _ => { };
    private Action<string> _setStatusMessage = _ => { };
    private Action<string> _setStreamingBuffer = _ => { };
    private Action<string> _setInputText = _ => { };

    public void SetCallbacks(
        Action<bool> setIsStreaming,
        Action<bool> setIsThinking,
        Action<bool> setIsAgentRunning,
        Action<string> setStatusMessage,
        Action<string> setStreamingBuffer,
        Action<string> setInputText)
    {
        _setIsStreaming = setIsStreaming;
        _setIsThinking = setIsThinking;
        _setIsAgentRunning = setIsAgentRunning;
        _setStatusMessage = setStatusMessage;
        _setStreamingBuffer = setStreamingBuffer;
        _setInputText = setInputText;
    }

    public void Apply(UiScreenModel screen)
    {
        _setIsStreaming(screen.Header.IsStreaming);
        _setIsThinking(screen.Header.IsAgentRunning && !screen.Header.IsStreaming);
        _setIsAgentRunning(screen.Header.IsAgentRunning);
        _setStatusMessage(screen.Header.IsAgentRunning
            ? screen.Header.IsStreaming ? "Streaming response…" : "Agent is running…"
            : "Idle");
        _setStreamingBuffer(GetStreamingText(screen));

        _setInputText(screen.Input.Text);
    }

    private static string GetStreamingText(UiScreenModel screen)
    {
        if (!screen.Header.IsStreaming)
            return string.Empty;

        var streamingLine = screen.Transcript.RenderedLines.LastOrDefault(l => l.Id == "streaming-text" && l.Spans.Count > 0);
        if (streamingLine != null)
            return streamingLine.Spans[0].Text;

        return string.Empty;
    }
}