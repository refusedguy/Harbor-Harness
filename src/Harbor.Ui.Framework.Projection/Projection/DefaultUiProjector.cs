using System.Collections.Immutable;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

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
///                 <see cref="UiState.Chat.Lines" /> backing-array reference
///                 (<c>ImmutableArray.Equals</c> is reference equality, and the
///                 arrays are immutable, so reference equality implies content
///                 equality). When the transcript grows by append, only the NEW
///                 suffix rows are re-projected (common-prefix scan) and spliced
///                 onto the cached prefix — copy-on-write of the row arrays.
///             </item>
///             <item>
///                 Streaming-tail lines (thinking/text) are rebuilt only when
///                 the corresponding buffer reference changes; otherwise the
///                 cached tail is spliced in. Pending (unflushed) deltas are
///                 deliberately NOT projected (bounded visibility lag) — see
///                 the Project method note on O(H·N).
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
///         (O(n) instead of the previous O(n²) scan). Lines carrying a
///         <c>ToolCallId</c> use it as the block id via
///         <see cref="Harbor.Ui.Framework.State.ToolCallKey"/> — the same key
///         the tool-card paths join on.
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

        // Stale-drop (#94): CAS success and Changed delivery are not atomic
        // across threads, so notifications can arrive out of order. A state
        // older than the last projected one reuses the cached screen instead
        // of rewinding visible output. Revision 0 = hand-built state
        // (tests/replays without a store): always project those.
        if (cache is not null && state.Revision != 0 && cache.State.Revision > state.Revision)
        {
            return cache.Screen;
        }

        // Streaming-tail buffers normalized: null when not streaming or empty
        // (null-safe like the original IsNullOrEmpty checks), so reference
        // equality fully identifies tail content. Only the synced prefix is
        // projected — unflushed pending deltas stay invisible until
        // ShouldFlush fires (bounded lag, max 2048 chars). Projecting pending
        // immediately would rebuild the tail AND recompose the whole
        // transcript (O(history)) on every delta — the O(H·N) the flush
        // policy exists to prevent (see StreamingFrequencyTests).
        string? thinkRaw = state.Chat.Active.ThinkBuffer;
        string? textRaw = state.Chat.Active.TextBuffer;
        string? thinkBuf = state.Chat.IsStreaming && !string.IsNullOrEmpty(thinkRaw) ? thinkRaw : null;
        string? textBuf = state.Chat.IsStreaming && !string.IsNullOrEmpty(textRaw) ? textRaw : null;

        ChromeModels chrome = ProjectChrome(state, cache);

        HistoryModels history = ProjectHistory(state, cache);

        TailModels tail = ProjectTail(state, cache, thinkBuf, textBuf);

        // ── Compose the transcript (copy-on-write: only changed frames copy) ──
        string? streamingBlockId = state.Chat.IsStreaming ? "streaming" : null;
        bool transcriptSame = history.Unchanged
            && tail.Unchanged
            && cache is not null;
        UiTranscriptModel transcript;
        if (transcriptSame)
        {
            transcript = cache!.Transcript;
        }
        else
        {
            var blockBuilder = ImmutableArray.CreateBuilder<UiBlock>(history.Blocks.Length + tail.Blocks.Length);
            var renderedBuilder = ImmutableArray.CreateBuilder<UiRenderedLine>(history.Rendered.Length + tail.Rendered.Length);
            blockBuilder.AddRange(history.Blocks.AsSpan());
            blockBuilder.AddRange(tail.Blocks.AsSpan());
            renderedBuilder.AddRange(history.Rendered.AsSpan());
            renderedBuilder.AddRange(tail.Rendered.AsSpan());

            transcript = new UiTranscriptModel(
                Blocks: blockBuilder.ToImmutable(),
                RenderedLines: renderedBuilder.ToImmutable(),
                StreamingBlockId: streamingBlockId);
        }

        // ── Screen assembly ──
        UiScreenModel screen;
        if (transcriptSame && chrome.Unchanged)
        {
            // Nothing observable changed — reuse the entire screen record.
            screen = cache!.Screen;
        }
        else
        {
            screen = new UiScreenModel(
                Header: chrome.Header,
                Transcript: transcript,
                StatusBar: chrome.StatusBar,
                Input: chrome.Input,
                Focus: state.Ui.Focus,
                StateRevision: ComputeRevision(state));
        }

        _cache = new ProjectionCache
        {
            State = state,
            Screen = screen,
            Transcript = transcript,
            Lines = state.Chat.Lines,
            BaseRendered = history.Rendered,
            BaseBlocks = history.Blocks,
            IsStreaming = state.Chat.IsStreaming,
            ThinkBuf = thinkBuf,
            TextBuf = textBuf,
            TailRendered = tail.Rendered,
            TailBlocks = tail.Blocks,
            Model = state.Chat.Model,
            Provider = state.Chat.Provider,
            AgentName = state.Chat.AgentName,
            Status = state.Chat.Status,
            InputText = state.Ui.Input.Text,
            IsAgentRunning = state.Chat.IsAgentRunning,
            ShouldQuit = state.Ui.ShouldQuit,
            Focus = state.Ui.Focus,
            Cost = state.Chat.Cost,
            TotalLines = state.Ui.TotalLines,
            ViewportLines = state.Ui.ViewportLines,
            ScrollOffset = state.Ui.ScrollOffset,
            Header = chrome.Header,
            StatusBar = chrome.StatusBar,
            Input = chrome.Input
        };

        return screen;
    }

    /// <summary>Projected chrome models plus whether the cached ones were reused.</summary>
    private readonly record struct ChromeModels(UiHeaderModel Header, UiStatusBarModel StatusBar, UiInputModel Input, bool Unchanged);

    /// <summary>Projected history rows plus whether the cached ones were reused.</summary>
    private readonly record struct HistoryModels(ImmutableArray<UiRenderedLine> Rendered, ImmutableArray<UiBlock> Blocks, bool Unchanged);

    /// <summary>Projected streaming tail plus whether the cached one was reused.</summary>
    private readonly record struct TailModels(ImmutableArray<UiRenderedLine> Rendered, ImmutableArray<UiBlock> Blocks, bool Unchanged);

    /// <summary>Projects header / status bar / input, reusing cached models when the chrome fingerprint matches.</summary>
    private static ChromeModels ProjectChrome(UiState state, ProjectionCache? cache)
    {
        // ── Chrome fingerprint (header / status bar / input) ──
        bool chromeSame = cache is not null
            && ReferenceEquals(cache.Model, state.Chat.Model)
            && ReferenceEquals(cache.Provider, state.Chat.Provider)
            && ReferenceEquals(cache.AgentName, state.Chat.AgentName)
            && ReferenceEquals(cache.Status, state.Chat.Status)
            && ReferenceEquals(cache.InputText, state.Ui.Input.Text)
            && cache.IsAgentRunning == state.Chat.IsAgentRunning
            && cache.IsStreaming == state.Chat.IsStreaming
            && cache.ShouldQuit == state.Ui.ShouldQuit
            && cache.Focus == state.Ui.Focus
            && cache.Cost == state.Chat.Cost
            && cache.TotalLines == state.Ui.TotalLines
            && cache.ViewportLines == state.Ui.ViewportLines
            && cache.ScrollOffset == state.Ui.ScrollOffset;

        var header = chromeSame ? cache!.Header : new UiHeaderModel(
            Model: state.Chat.Model,
            Provider: state.Chat.Provider,
            AgentName: state.Chat.AgentName,
            IsAgentRunning: state.Chat.IsAgentRunning,
            IsStreaming: state.Chat.IsStreaming,
            ShouldQuit: state.Ui.ShouldQuit,
            Cost: state.Chat.Cost,
            FooterText: ProjectFooter(state));

        var statusBar = chromeSame ? cache!.StatusBar : ProjectStatusBar(state);

        var input = chromeSame ? cache!.Input : new UiInputModel(
            Text: state.Ui.Input.Text,
            Caret: state.Ui.Input.Text.Length,
            IsEnabled: !state.Chat.IsAgentRunning,
            Placeholder: state.Chat.IsAgentRunning ? "Agent is running…" : "Type a message…");

        return new ChromeModels(header, statusBar, input, chromeSame);
    }

    /// <summary>Projects history rows, reusing the cached prefix on append-only transcript growth.</summary>
    private static HistoryModels ProjectHistory(UiState state, ProjectionCache? cache)
    {
        // ── History rows ──
        bool linesSame = cache is not null && state.Chat.Lines.Equals(cache.Lines);
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
                int limit = Math.Min(cache.Lines.Length, state.Chat.Lines.Length);
                while (commonPrefix < limit && cache.Lines[commonPrefix].Equals(state.Chat.Lines[commonPrefix]))
                {
                    commonPrefix++;
                }
            }

            // First-occurrence map preserves the historical
            // `Lines.IndexOf(line)` BlockId semantics (first equal line wins)
            // at O(n) total instead of O(n²).
            var firstIndex = new Dictionary<ChatLine, int>(state.Chat.Lines.Length);
            for (int i = 0; i < state.Chat.Lines.Length; i++)
            {
                firstIndex.TryAdd(state.Chat.Lines[i], i);
            }

            var renderedBuilder = ImmutableArray.CreateBuilder<UiRenderedLine>(state.Chat.Lines.Length);
            var blockBuilder = ImmutableArray.CreateBuilder<UiBlock>(state.Chat.Lines.Length);
            if (commonPrefix > 0)
            {
                renderedBuilder.AddRange(cache!.BaseRendered.AsSpan().Slice(0, commonPrefix));
                blockBuilder.AddRange(cache.BaseBlocks.AsSpan().Slice(0, commonPrefix));
            }

            for (int i = commonPrefix; i < state.Chat.Lines.Length; i++)
            {
                ChatLine line = state.Chat.Lines[i];
                string id = ToolCallKey.TranscriptBlockId(line, firstIndex[line]);
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

        return new HistoryModels(baseRendered, baseBlocks, linesSame);
    }

    /// <summary>Projects the streaming tail, rebuilding only when a buffer reference changed.</summary>
    private static TailModels ProjectTail(UiState state, ProjectionCache? cache, string? thinkBuf, string? textBuf)
    {
        // ── Streaming tail (rebuilt only when a buffer reference changed) ──
        // Reference equality suffices: buffers are immutable strings replaced
        // wholesale on flush, so a changed reference IS changed content.
        bool tailSame = cache is not null
            && cache.IsStreaming == state.Chat.IsStreaming
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
            // Deterministic tail timestamp (#94): stamping DateTime.UtcNow here
            // made identical states project different models, so tail
            // memoization never stabilized. Reuse the newest transcript line
            // timestamp (falling back to the cached tail stamp, then default)
            // — a pure function of the projected state.
            DateTime tailTimestamp = ResolveTailTimestamp(state, cache);
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
                    TimestampUtc: tailTimestamp));

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
                    TimestampUtc: tailTimestamp));

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

        return new TailModels(tailRendered, tailBlocks, tailSame);
    }

    /// <summary>
    ///     Snapshot of the last projection and its cache keys. Written once, in a single
    ///     object initializer at the end of <see cref="Project" />, and read-only thereafter.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The six members marked <see langword="required" /> are <b>never</b> absent —
    ///         they are either assigned by that initializer or the type does not exist yet.
    ///         They used to be declared <c>= null!</c>, which is a lie the compiler was asked
    ///         to accept: nothing stopped a second construction site from forgetting one, and
    ///         a forgotten member would surface as a <see cref="NullReferenceException" /> at
    ///         one of the three reuse sites below (<see cref="Project" /> lines 70, 112 and
    ///         134) with nothing pointing back at the cache.
    ///     </para>
    ///     <para>
    ///         <see langword="required" /> is the fix rather than
    ///         <c>Maybe&lt;T&gt;</c>: absence is not the model here — there is no
    ///         "widget in another state" and no working default to fall back to, so making
    ///         the members optional would only move the null somewhere else. Requiring them
    ///         makes the compiler state the invariant instead of a comment.
    ///     </para>
    ///     <para>
    ///         Every dereference of this cache is guarded by a <c>ReferenceEquals</c> or a
    ///         fingerprint comparison against the state being projected, so <b>publication</b>
    ///         is the one remaining assumption: the cache is written by the render thread and
    ///         read by it. See #605 — the field holding it is not <c>volatile</c>, and the
    ///         desktop host registers the projector as a singleton.
    ///     </para>
    /// </remarks>
    private sealed class ProjectionCache
    {
        public required UiState State;
        public required UiScreenModel Screen;
        public required UiTranscriptModel Transcript;

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
        public required UiHeaderModel Header;
        public required UiStatusBarModel StatusBar;
        public required UiInputModel Input;
    }

    private static UiStatusBarModel ProjectStatusBar(UiState state)
    {
        return StatusProjector.ProjectStatusBar(state);
    }

    private static string ProjectFooter(UiState state)
    {
        return StatusProjector.ProjectFooter(state);
    }

    private static IReadOnlyList<StyledSpan> ResolveSpans(ChatRole role, string text) =>
        ImmutableArray.Create(
            new StyledSpan(text, null, null, false, false, false, false, SpanStyle(role)));

    /// <summary>
    ///     The projected-span vocabulary for a role (UiSpanStyle — a different
    ///     vocabulary from ChatRolePresentation's label/markdown/slot policy).
    ///     Every arm named, no wildcard (docs/PATTERNS.md §"Type unions"): a new
    ///     ChatRole falls out of the switch and hits the throw below, instead of
    ///     being projected as Default.
    /// </summary>
    private static UiSpanStyle SpanStyle(ChatRole role)
    {
        switch (role)
        {
            case ChatRole.User: return UiSpanStyle.RoleUser;
            case ChatRole.Assistant: return UiSpanStyle.RoleAssistant;
            case ChatRole.Thinking: return UiSpanStyle.Default;
            case ChatRole.Tool: return UiSpanStyle.Tool;
            case ChatRole.ToolResult: return UiSpanStyle.Default;
            case ChatRole.System: return UiSpanStyle.RoleSystem;
            case ChatRole.Error: return UiSpanStyle.Danger;
        }

        throw ChatRolePresentation.Unhandled(role);
    }

    private static DateTime ResolveTailTimestamp(UiState state, ProjectionCache? cache)
    {
        if (state.Chat.Lines.Length > 0)
        {
            DateTime lineTs = state.Chat.Lines[state.Chat.Lines.Length - 1].TimestampUtc;
            if (lineTs != default)
                return lineTs;
        }

        if (cache is not null)
        {
            foreach (var row in cache.TailRendered)
            {
                if (row.TimestampUtc != default)
                    return row.TimestampUtc;
            }
        }

        return default;
    }

    private static string ComputeRevision(UiState state)
    {
        // Store revision first (stale-drop ordering), then the visible text
        // lengths INCLUDING unflushed pending deltas (#94) so the revision
        // string moves as soon as newly arrived text becomes visible.
        int textLen = state.Chat.Active.TextBuffer.Length + state.Chat.PendingStreamText.Length;
        int thinkLen = state.Chat.Active.ThinkBuffer.Length + state.Chat.PendingStreamThink.Length;
        return $"{state.Revision}:{state.Chat.Lines.Length}:{state.Chat.IsStreaming}:{textLen}:{thinkLen}";
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
