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
        var segments = ImmutableArray.CreateBuilder<UiStatusSegment>();

        segments.Add(new UiStatusSegment(
            Text: $"{state.Provider}/{state.Model}",
            Align: Alignment.Left,
            Importance: 1,
            Style: UiSpanStyle.Default));

        string glyph = state.Status switch
        {
            "running" => "▌",
            "compacting" => "◐",
            "error" => "✗",
            _ => "○"
        };
        UiSpanStyle statusStyle = state.Status switch
        {
            "running" => UiSpanStyle.Accent,
            "error" => UiSpanStyle.Danger,
            _ => UiSpanStyle.Default
        };
        segments.Add(new UiStatusSegment(
            Text: $"{glyph} {state.Status}",
            Align: Alignment.Center,
            Importance: 2,
            Style: statusStyle));

        if (!string.IsNullOrEmpty(state.AgentName))
        {
            segments.Add(new UiStatusSegment(
                Text: $"agent {state.AgentName}",
                Align: Alignment.Right,
                Importance: 3,
                Style: UiSpanStyle.Default));
        }

        if (state.Cost.TokensIn > 0 || state.Cost.TokensOut > 0)
        {
            segments.Add(new UiStatusSegment(
                Text: $"{state.Cost.TokensIn}↑ {state.Cost.TokensOut}↓",
                Align: Alignment.Right,
                Importance: 2,
                Style: UiSpanStyle.Dim));
        }

        segments.Add(new UiStatusSegment(
            Text: $"{state.Cost.CostUsd:F4}",
            Align: Alignment.Right,
            Importance: 1,
            Style: UiSpanStyle.Dim));

        int maxScroll = Math.Max(0, state.TotalLines - Math.Max(1, state.ViewportLines));
        string scrollText = maxScroll == 0 ? "live" : $"scroll {state.ScrollOffset * 100 / maxScroll}%";
        segments.Add(new UiStatusSegment(
            Text: scrollText,
            Align: Alignment.Right,
            Importance: 0,
            Style: UiSpanStyle.Dim));

        return new UiStatusBarModel(Segments: segments.ToImmutable());
    }

    private static string ProjectFooter(UiState state)
    {
        var statusBar = ProjectStatusBar(state);
        var left = statusBar.Segments.Where(s => s.Align == Alignment.Left).OrderBy(s => s.Importance);
        var center = statusBar.Segments.Where(s => s.Align == Alignment.Center).OrderBy(s => s.Importance);
        var right = statusBar.Segments.Where(s => s.Align == Alignment.Right).OrderByDescending(s => s.Importance);

        return string.Join("  ", left.Select(s => s.Text))
               + (center.Any() ? "  " + string.Join("  ", center.Select(s => s.Text)) : "")
               + (right.Any() ? "  " + string.Join("  ", right.Select(s => s.Text)) : "");
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