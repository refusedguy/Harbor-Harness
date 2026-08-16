using System.Collections.Immutable;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Projection;

public sealed class DefaultUiProjector : IUiProjector
{
    public UiScreenModel Project(UiState state)
    {
        var header = new UiHeaderModel(
            Model: state.Model,
            Provider: state.Provider,
            AgentName: state.AgentName,
            IsAgentRunning: state.IsAgentRunning,
            IsStreaming: state.IsStreaming,
            ShouldQuit: state.ShouldQuit,
            Cost: state.Cost,
            FooterText: ProjectFooter(state));

        var transcript = ProjectTranscript(state);

        var statusBar = ProjectStatusBar(state);

        var input = new UiInputModel(
            Text: state.Input.Text,
            Caret: state.Input.Text.Length,
            IsEnabled: !state.IsAgentRunning,
            Placeholder: state.IsAgentRunning ? "Agent is running…" : "Type a message…");

        return new UiScreenModel(
            Header: header,
            Transcript: transcript,
            StatusBar: statusBar,
            Input: input,
            Focus: state.Focus,
            StateRevision: ComputeRevision(state));
    }

    private static UiTranscriptModel ProjectTranscript(UiState state)
    {
        var blocks = ImmutableArray.CreateBuilder<UiBlock>();
        var renderedLines = ImmutableArray.CreateBuilder<UiRenderedLine>();
        string? streamingBlockId = null;

        foreach (var line in state.Lines)
        {
            var id = line.ToolCallId ?? BlockId(line.Role, state.Lines.IndexOf(line));
            var spans = ResolveSpans(line.Role, line.Text);

            renderedLines.Add(new UiRenderedLine(
                Id: id,
                Spans: spans,
                Kind: UiLineKind.Body,
                TimestampUtc: line.TimestampUtc));

            var phase = MessageRenderPhase.Complete;
            var block = new UiMessageBlock(
                Id: id,
                Role: line.Role,
                Spans: spans,
                Phase: phase);
            blocks.Add(block);
        }

        if (state.IsStreaming)
        {
            streamingBlockId = "streaming";

            if (!string.IsNullOrEmpty(state.Active.ThinkBuffer))
            {
                var thinkId = "streaming-thinking";
                var thinkSpans = ResolveSpans(ChatRole.Thinking, state.Active.ThinkBuffer);
                renderedLines.Add(new UiRenderedLine(
                    Id: thinkId,
                    Spans: thinkSpans,
                    Kind: UiLineKind.Thinking,
                    TimestampUtc: DateTime.UtcNow));

                blocks.Add(new UiMessageBlock(
                    Id: thinkId,
                    Role: ChatRole.Thinking,
                    Spans: thinkSpans,
                    Phase: MessageRenderPhase.Thinking));
            }

            if (!string.IsNullOrEmpty(state.Active.TextBuffer))
            {
                var textId = "streaming-text";
                var textSpans = ResolveSpans(ChatRole.Assistant, state.Active.TextBuffer);
                renderedLines.Add(new UiRenderedLine(
                    Id: textId,
                    Spans: textSpans,
                    Kind: UiLineKind.Body,
                    TimestampUtc: DateTime.UtcNow));

                blocks.Add(new UiMessageBlock(
                    Id: textId,
                    Role: ChatRole.Assistant,
                    Spans: textSpans,
                    Phase: MessageRenderPhase.Streaming));
            }
        }

        return new UiTranscriptModel(
            Blocks: blocks.ToImmutable(),
            RenderedLines: renderedLines.ToImmutable(),
            StreamingBlockId: streamingBlockId);
    }

    private static UiStatusBarModel ProjectStatusBar(UiState state)
    {
        return StatusProjector.ProjectStatusBar(state);
    }

    private static string ProjectFooter(UiState state)
    {
        return StatusProjector.ProjectFooter(state);
    }

    private static IReadOnlyList<StyledSpan> ResolveSpans(ChatRole role, string text)
    {
        var spans = ImmutableArray.CreateBuilder<StyledSpan>();

        var style = role switch
        {
            ChatRole.User => UiSpanStyle.RoleUser,
            ChatRole.Assistant => UiSpanStyle.RoleAssistant,
            ChatRole.Thinking => UiSpanStyle.Default,
            ChatRole.Tool => UiSpanStyle.Tool,
            ChatRole.ToolResult => UiSpanStyle.Default,
            ChatRole.System => UiSpanStyle.RoleSystem,
            ChatRole.Error => UiSpanStyle.Danger,
            _ => UiSpanStyle.Default
        };

        spans.Add(new StyledSpan(text, null, null, false, false, false, false, style));

        return spans.ToImmutable();
    }

    private static string BlockId(ChatRole role, int index)
    {
        return role switch
        {
            ChatRole.Tool => $"tool:{index}",
            ChatRole.ToolResult => $"tool-result:{index}",
            _ => $"msg:{index}"
        };
    }

    private static string ComputeRevision(UiState state)
    {
        return $"{state.Lines.Length}:{state.IsStreaming}:{state.Active.TextBuffer?.Length ?? 0}:{state.Active.ThinkBuffer?.Length ?? 0}";
    }

    /// <summary>
    ///     Extract rendered lines from a <see cref="UiScreenModel" /> preserving
    ///     per-span styling (foreground, background, bold, italic, underline, dim,
    ///     and semantic <see cref="UiSpanStyle" />). All viewports consume this
    ///     instead of duplicating the span-to-text stripping logic.
    /// </summary>
    public static ImmutableArray<UiRenderedLine> ExtractRenderedLines(UiScreenModel screen)
    {
        return screen.Transcript.RenderedLines.ToImmutableArray();
    }
}