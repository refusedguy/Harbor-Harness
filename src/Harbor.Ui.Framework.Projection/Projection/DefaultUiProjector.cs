using System.Collections.Immutable;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Projection;

/// <summary>
///     Projects the immutable <see cref="UiState" /> snapshot into a
///     renderer-agnostic <see cref="UiScreenModel" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>B7perf — revision-based incremental projection:</b> projecting
///         5000 transcript rows from scratch costs ~21 ms; renderers call
///         <see cref="Project" /> every frame. The projector now caches the
///         last projection keyed by cheap revision signals:
///         <list type="bullet">
///             <item>
///                 Same <see cref="UiState" /> instance (TEA reducers return
///                 the previous instance when nothing changed) → the cached
///                 <see cref="UiScreenModel" /> is returned as-is (zero work).
///             </item>
///             <item>
///                 History rows are cached against the
///                 <see cref="UiState.Lines" /> backing-array reference
///                 (<c>ImmutableArray.Equals</c> is reference equality, and the
///                 arrays are immutable, so reference equality implies content
///                 equality). When the transcript grows by append, only the NEW
///                 suffix rows are re-projected (common-prefix scan) and spliced
///                 onto the cached prefix — copy-on-write of the row arrays.
///             </item>
///             <item>
///                 Streaming-tail lines (thinking/text) are rebuilt only when
///                 the corresponding buffer reference changes; otherwise the
///                 cached tail is spliced in.
///             </item>
///             <item>
///                 Header / status bar / input models are rebuilt only when one
///                 of their scalar inputs changes (reference-compared strings +
///                 value-compared scalars).
///             </item>
///         </list>
///         Per-row block ids preserve the historical
///         <c>Lines.IndexOf(line)</c> first-occurrence semantics via a
///         first-occurrence dictionary built once per transcript revision
///         (O(n) instead of the previous O(n²) scan).
///     </para>
///     <para>
///         <b>Thread-safety:</b> the projector holds mutable cache state and
///         is NOT thread-safe. Call <see cref="Project" /> from a single render
///         loop — every built-in renderer already constructs its own instance.
///     </para>
/// </remarks>
public sealed class DefaultUiProjector : IUiProjector
{
    private ProjectionCache? _cache;

    /// <inheritdoc />
    public UiScreenModel Project(UiState state)
    {
        var cache = _cache;

        // Fast path: the reducer reuses the state instance when nothing
        // changed — return the previous projection untouched.
        if (cache is not null && ReferenceEquals(cache.State, state))
        {
            return cache.Screen;
        }

        // Streaming-tail buffers normalized: null when not streaming or empty
        // (null-safe like the original IsNullOrEmpty checks), so reference
        // equality fully identifies tail content.
        string? thinkRaw = state.Active.ThinkBuffer;
        string? textRaw = state.Active.TextBuffer;
        string? thinkBuf = state.IsStreaming && !string.IsNullOrEmpty(thinkRaw) ? thinkRaw : null;
        string? textBuf = state.IsStreaming && !string.IsNullOrEmpty(textRaw) ? textRaw : null;

        // ── Chrome fingerprint (header / status bar / input) ──
        bool chromeSame = cache is not null
            && ReferenceEquals(cache.Model, state.Model)
            && ReferenceEquals(cache.Provider, state.Provider)
            && ReferenceEquals(cache.AgentName, state.AgentName)
            && ReferenceEquals(cache.Status, state.Status)
            && ReferenceEquals(cache.InputText, state.Input.Text)
            && cache.IsAgentRunning == state.IsAgentRunning
            && cache.IsStreaming == state.IsStreaming
            && cache.ShouldQuit == state.ShouldQuit
            && cache.Focus == state.Focus
            && cache.Cost == state.Cost
            && cache.TotalLines == state.TotalLines
            && cache.ViewportLines == state.ViewportLines
            && cache.ScrollOffset == state.ScrollOffset;

        var header = chromeSame ? cache!.Header : new UiHeaderModel(
            Model: state.Model,
            Provider: state.Provider,
            AgentName: state.AgentName,
            IsAgentRunning: state.IsAgentRunning,
            IsStreaming: state.IsStreaming,
            ShouldQuit: state.ShouldQuit,
            Cost: state.Cost,
            FooterText: ProjectFooter(state));

        var statusBar = chromeSame ? cache!.StatusBar : ProjectStatusBar(state);

        var input = chromeSame ? cache!.Input : new UiInputModel(
            Text: state.Input.Text,
            Caret: state.Input.Text.Length,
            IsEnabled: !state.IsAgentRunning,
            Placeholder: state.IsAgentRunning ? "Agent is running…" : "Type a message…");

        // ── History rows ──
        bool linesSame = cache is not null && state.Lines.Equals(cache.Lines);
        ImmutableArray<UiRenderedLine> baseRendered;
        ImmutableArray<UiBlock> baseBlocks;
        if (linesSame)
        {
            baseRendered = cache!.BaseRendered;
            baseBlocks = cache.BaseBlocks;
        }
        else
        {
            // Common-prefix scan: transcript updates are append-only in the
            // common case (MessageEnd folds the streaming message into the
            // tail of Lines), so reuse the projection of the unchanged prefix.
            int commonPrefix = 0;
            if (cache is not null)
            {
                int limit = Math.Min(cache.Lines.Length, state.Lines.Length);
                while (commonPrefix < limit && cache.Lines[commonPrefix].Equals(state.Lines[commonPrefix]))
                {
                    commonPrefix++;
                }
            }

            // First-occurrence map preserves the historical
            // `Lines.IndexOf(line)` BlockId semantics (first equal line wins)
            // at O(n) total instead of O(n²).
            var firstIndex = new Dictionary<ChatLine, int>(state.Lines.Length);
            for (int i = 0; i < state.Lines.Length; i++)
            {
                firstIndex.TryAdd(state.Lines[i], i);
            }

            var renderedBuilder = ImmutableArray.CreateBuilder<UiRenderedLine>(state.Lines.Length);
            var blockBuilder = ImmutableArray.CreateBuilder<UiBlock>(state.Lines.Length);
            if (commonPrefix > 0)
            {
                renderedBuilder.AddRange(cache!.BaseRendered.AsSpan().Slice(0, commonPrefix));
                blockBuilder.AddRange(cache.BaseBlocks.AsSpan().Slice(0, commonPrefix));
            }

            for (int i = commonPrefix; i < state.Lines.Length; i++)
            {
                ChatLine line = state.Lines[i];
                string id = line.ToolCallId ?? BlockId(line.Role, firstIndex[line]);
                var spans = ResolveSpans(line.Role, line.Text);

                renderedBuilder.Add(new UiRenderedLine(
                    Id: id,
                    Spans: spans,
                    Kind: UiLineKind.Body,
                    TimestampUtc: line.TimestampUtc));

                blockBuilder.Add(new UiMessageBlock(
                    Id: id,
                    Role: line.Role,
                    Spans: spans,
                    Phase: MessageRenderPhase.Complete));
            }

            baseRendered = renderedBuilder.MoveToImmutable();
            baseBlocks = blockBuilder.MoveToImmutable();
        }

        // ── Streaming tail (rebuilt only when a buffer reference changed) ──
        bool tailSame = cache is not null
            && cache.IsStreaming == state.IsStreaming
            && ReferenceEquals(cache.ThinkBuf, thinkBuf)
            && ReferenceEquals(cache.TextBuf, textBuf);

        ImmutableArray<UiRenderedLine> tailRendered;
        ImmutableArray<UiBlock> tailBlocks;
        if (tailSame)
        {
            tailRendered = cache!.TailRendered;
            tailBlocks = cache.TailBlocks;
        }
        else
        {
            var renderedBuilder = ImmutableArray.CreateBuilder<UiRenderedLine>(2);
            var blockBuilder = ImmutableArray.CreateBuilder<UiBlock>(2);

            if (thinkBuf is not null)
            {
                const string thinkId = "streaming-thinking";
                var thinkSpans = ResolveSpans(ChatRole.Thinking, thinkBuf);
                renderedBuilder.Add(new UiRenderedLine(
                    Id: thinkId,
                    Spans: thinkSpans,
                    Kind: UiLineKind.Thinking,
                    TimestampUtc: DateTime.UtcNow));

                blockBuilder.Add(new UiMessageBlock(
                    Id: thinkId,
                    Role: ChatRole.Thinking,
                    Spans: thinkSpans,
                    Phase: MessageRenderPhase.Thinking));
            }

            if (textBuf is not null)
            {
                const string textId = "streaming-text";
                var textSpans = ResolveSpans(ChatRole.Assistant, textBuf);
                renderedBuilder.Add(new UiRenderedLine(
                    Id: textId,
                    Spans: textSpans,
                    Kind: UiLineKind.Body,
                    TimestampUtc: DateTime.UtcNow));

                blockBuilder.Add(new UiMessageBlock(
                    Id: textId,
                    Role: ChatRole.Assistant,
                    Spans: textSpans,
                    Phase: MessageRenderPhase.Streaming));
            }

            // Capacity-sized builders may hold fewer items (e.g. thinking
            // without text); MoveToImmutable would throw — ToImmutable is
            // count-safe on this small streaming-tail path.
            tailRendered = renderedBuilder.ToImmutable();
            tailBlocks = blockBuilder.ToImmutable();
        }

        // ── Compose the transcript (copy-on-write: only changed frames copy) ──
        string? streamingBlockId = state.IsStreaming ? "streaming" : null;
        bool transcriptSame = linesSame
            && tailSame
            && cache is not null;
        UiTranscriptModel transcript;
        if (transcriptSame)
        {
            transcript = cache!.Transcript;
        }
        else
        {
            var blockBuilder = ImmutableArray.CreateBuilder<UiBlock>(baseBlocks.Length + tailBlocks.Length);
            var renderedBuilder = ImmutableArray.CreateBuilder<UiRenderedLine>(baseRendered.Length + tailRendered.Length);
            blockBuilder.AddRange(baseBlocks.AsSpan());
            blockBuilder.AddRange(tailBlocks.AsSpan());
            renderedBuilder.AddRange(baseRendered.AsSpan());
            renderedBuilder.AddRange(tailRendered.AsSpan());

            transcript = new UiTranscriptModel(
                Blocks: blockBuilder.ToImmutable(),
                RenderedLines: renderedBuilder.ToImmutable(),
                StreamingBlockId: streamingBlockId);
        }

        // ── Screen assembly ──
        UiScreenModel screen;
        if (transcriptSame && chromeSame)
        {
            // Nothing observable changed — reuse the entire screen record.
            screen = cache!.Screen;
        }
        else
        {
            screen = new UiScreenModel(
                Header: header,
                Transcript: transcript,
                StatusBar: statusBar,
                Input: input,
                Focus: state.Focus,
                StateRevision: ComputeRevision(state));
        }

        _cache = new ProjectionCache
        {
            State = state,
            Screen = screen,
            Transcript = transcript,
            Lines = state.Lines,
            BaseRendered = baseRendered,
            BaseBlocks = baseBlocks,
            IsStreaming = state.IsStreaming,
            ThinkBuf = thinkBuf,
            TextBuf = textBuf,
            TailRendered = tailRendered,
            TailBlocks = tailBlocks,
            Model = state.Model,
            Provider = state.Provider,
            AgentName = state.AgentName,
            Status = state.Status,
            InputText = state.Input.Text,
            IsAgentRunning = state.IsAgentRunning,
            ShouldQuit = state.ShouldQuit,
            Focus = state.Focus,
            Cost = state.Cost,
            TotalLines = state.TotalLines,
            ViewportLines = state.ViewportLines,
            ScrollOffset = state.ScrollOffset,
            Header = header,
            StatusBar = statusBar,
            Input = input
        };

        return screen;
    }

    /// <summary>Immutable snapshot of the last projection and its cache keys.</summary>
    private sealed class ProjectionCache
    {
        public UiState State = null!;
        public UiScreenModel Screen = null!;
        public UiTranscriptModel Transcript = null!;

        // History rows: keyed by the Lines backing-array reference.
        public ImmutableArray<ChatLine> Lines;
        public ImmutableArray<UiRenderedLine> BaseRendered;
        public ImmutableArray<UiBlock> BaseBlocks;

        // Streaming tail: keyed by IsStreaming + normalized buffer references.
        public bool IsStreaming;
        public string? ThinkBuf;
        public string? TextBuf;
        public ImmutableArray<UiRenderedLine> TailRendered;
        public ImmutableArray<UiBlock> TailBlocks;

        // Chrome fingerprint + reusable chrome models.
        public string Model = string.Empty;
        public string Provider = string.Empty;
        public string AgentName = string.Empty;
        public string Status = string.Empty;
        public string? InputText;
        public bool IsAgentRunning;
        public bool ShouldQuit;
        public FocusMode Focus;
        public CostSnapshot Cost;
        public int TotalLines;
        public int ViewportLines;
        public int ScrollOffset;
        public UiHeaderModel Header = null!;
        public UiStatusBarModel StatusBar = null!;
        public UiInputModel Input = null!;
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
