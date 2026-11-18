using System.Collections.Immutable;
using Harbor.Ui.Framework.Panels;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Generic terminal-UI state: zero chat/AI concerns. Input, scroll,
///     focus, panels and quit flag — everything a non-chat TUI needs.
///     BCL + framework panels only; no transcript, no costs, no agents.
/// </summary>
public sealed record TerminalUiState
{
    /// <summary>Editable prompt state (text + history navigation).</summary>
    public InputModel Input { get; init; } = InputModel.Empty;

    /// <summary>Which region currently owns the keyboard (drives highlight + routing).</summary>
    public FocusMode Focus { get; init; } = FocusMode.Input;

    /// <summary>
    ///     Logical viewport addressed by scroll geometry messages that do not
    ///     name one explicitly (#2357). The chat timeline owns this viewport;
    ///     a second virtualized region passes its own name instead.
    /// </summary>
    public const string DefaultViewport = "chat";

    /// <summary>
    ///     Scroll state of additional logical viewports keyed by name (#2357):
    ///     one entry owns the offset and measured geometry of exactly one
    ///     viewport, so two virtualized regions neither share an offset nor
    ///     overwrite each other's scrollbar thumb (hit) range. The
    ///     <see cref="DefaultViewport"/> keeps its existing scalar fields below
    ///     so every host and test stays behaviorally byte-identical.
    /// </summary>
    public ImmutableDictionary<string, (int Offset, int Viewport, int Total)> ViewportScrolls { get; init; }
        = ImmutableDictionary<string, (int Offset, int Viewport, int Total)>.Empty;

    /// <summary>
    ///     History scroll-back offset (0 = pinned to newest line, grows toward the
    ///     top). Clamped to <c>TotalLines - ViewportLines</c> by the reducer.
    /// </summary>
    public int ScrollOffset { get; init; }

    /// <summary>Number of history rows currently visible (reported by the renderer).</summary>
    public int ViewportLines { get; init; }

    /// <summary>Total number of wrapped history rows (reported by the renderer).</summary>
    public int TotalLines { get; init; }

    /// <summary>How far the history is scrolled, as a percentage (0 = bottom/live, 100 = top).</summary>
    public int ScrollPercent
    {
        get
        {
            int max = Math.Max(0, TotalLines - ViewportLines);
            if (max == 0) return 0;
            // ScrollOffset is rows lifted from the tail (0 = bottom).
            return (int)Math.Round(100.0 * ScrollOffset / max);
        }
    }

    /// <summary>
    ///     Per-panel runtime state. Mirrors the <c>PanelRegistry</c>; updated by
    ///     <c>AppReducer</c> on <c>TogglePanel</c> / <c>FocusPanel</c> /
    ///     <c>ResizePanel</c>. Renderers read this to decide which panels to render.
    /// </summary>
    public ImmutableDictionary<string, TuiPanelState> PanelStates { get; init; }
        = ImmutableDictionary<string, TuiPanelState>.Empty;

    /// <summary>
    ///     Per-panel size override (rows or cols, depending on the panel's placement).
    ///     <c>0</c> = use the provider's <c>DefaultSize</c>.
    /// </summary>
    public ImmutableDictionary<string, int> PanelSizes { get; init; }
        = ImmutableDictionary<string, int>.Empty;

    /// <summary>
    ///     Per-panel cursor position keyed by panel id (#360). Owns the
    ///     diagnostics / file-tree selection so providers stay stateless across
    ///     frames; updated by <see cref="AppReducer"/> on
    ///     <c>SetPanelCursor</c> / <c>SetPanelDirectory</c>. Missing key = 0.
    /// </summary>
    public ImmutableDictionary<string, int> PanelCursors { get; init; }
        = ImmutableDictionary<string, int>.Empty;

    /// <summary>
    ///     Per-panel directory keyed by panel id (#360). Owns the file-tree
    ///     current directory; the listing itself lives in
    ///     <see cref="FileTrees" />. Missing or empty key = the process working
    ///     directory - see <see cref="ResolvePanelDirectory" />, which is the
    ///     only place that rule is written down.
    /// </summary>
    public ImmutableDictionary<string, string> PanelDirs { get; init; }
        = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    ///     Per-panel file-tree listing keyed by panel id (#667). This is where
    ///     the entries went when they stopped being a provider-local cache: the
    ///     panel reads them here and the <c>FileTreeLoader</c> writes them here,
    ///     so the render thread never has to touch the filesystem to have
    ///     something to draw. Written by <c>AppMsg.SetFileTreePending</c> /
    ///     <c>SetFileTreeLoaded</c> / <c>SetFileTreeFailed</c>, cleared by
    ///     <c>AppMsg.InvalidateFileTree</c>. Missing key = nothing loaded yet.
    /// </summary>
    public ImmutableDictionary<string, FileTreeSnapshot> FileTrees { get; init; }
        = ImmutableDictionary<string, FileTreeSnapshot>.Empty;

    /// <summary>
    ///     Per-panel set of expanded directory paths (#1487). A path in the set
    ///     has its loaded <see cref="FileTreeEntry.ChildEntries" /> shown in place;
    ///     a path outside it is collapsed. Missing key means nothing is expanded.
    ///     This lives in the store beside <see cref="PanelCursors" /> and
    ///     <see cref="PanelDirs" /> for the same reason: expansion is selection
    ///     state, so it must survive repaints and refreshes (FP-005/TEA, #360).
    /// </summary>
    public ImmutableDictionary<string, ImmutableHashSet<string>> PanelExpandedDirs { get; init; }
        = ImmutableDictionary<string, ImmutableHashSet<string>>.Empty;

    /// <summary>Whether <paramref name="path" /> is expanded for <paramref name="id" />.</summary>
    /// <param name="id">The panel id.</param>
    /// <param name="path">The full path of the directory node.</param>
    public bool IsFileTreeNodeExpanded(string id, string path)
        => PanelExpandedDirs.TryGetValue(id, out ImmutableHashSet<string>? set) && set.Contains(path);

    /// <summary>
    ///     Per-panel PR review comments (#1488), keyed by panel id. Hosts that
    ///     already have review data in the session put it here with
    ///     <c>AppMsg.SetPrComments</c>; the cards panel reads it in <c>Build</c>
    ///     and performs no I/O. Missing key = empty state (honest placeholder).
    /// </summary>
    public ImmutableDictionary<string, ImmutableArray<PrCommentCard>> PrComments { get; init; }
        = ImmutableDictionary<string, ImmutableArray<PrCommentCard>>.Empty;

    /// <summary>
    ///     Id of the panel currently owning keyboard focus, or <see langword="null" />
    ///     when the chat / input box owns focus. Driven by <c>FocusPanel</c> /
    ///     <c>CyclePanelFocus</c> messages.
    /// </summary>
    public string? FocusedPanelId { get; init; }

    /// <summary>
    ///     Registered panel ids in registration order. Maintained by the host
    ///     (<c>PanelRegistry</c>) via <c>Dispatch(new AppMsg.SeedPanels(...))</c>.
    ///     Read by the reducer
    ///     for <c>CyclePanelFocus</c> so it stays pure (no IRegistry dependency).
    /// </summary>
    public ImmutableArray<string> RegisteredPanelIds { get; init; }
        = ImmutableArray<string>.Empty;

    /// <summary>Whether the user has requested to quit the interactive loop.</summary>
    public bool ShouldQuit { get; init; }

    /// <summary>
    ///     The concrete directory panel <paramref name="id" /> is pointed at: its
    ///     <see cref="PanelDirs" /> entry, or the process working directory when
    ///     the entry is missing or empty.
    /// </summary>
    /// <remarks>
    ///     Deliberately the ONLY place that rule exists (#667). Before the
    ///     file-tree listing moved into state, the panel and the loader each
    ///     carried their own copy of
    ///     <c>string.IsNullOrEmpty(stored) ? Environment.CurrentDirectory : stored</c>
    ///     and the reducer's staleness check compared against a third shape -
    ///     which is exactly the class of bug where the panel shows one directory
    ///     and the loader is loading another. One function, three callers, no
    ///     drift.
    /// </remarks>
    /// <param name="id">The panel id.</param>
    /// <returns>The resolved directory, never empty.</returns>
    public string ResolvePanelDirectory(string id)
        => PanelDirs.TryGetValue(id, out string? stored) && !string.IsNullOrEmpty(stored)
            ? stored
            : Environment.CurrentDirectory;

    /// <summary>
    ///     The listing for <paramref name="id" /> when it describes
    ///     <paramref name="directory" />, and <see cref="FileTreeSnapshot.None" />
    ///     otherwise. The directory check is what stops a late-arriving result
    ///     from painting a directory the user already navigated away from.
    /// </summary>
    /// <param name="id">The panel id.</param>
    /// <param name="directory">The directory the view is currently pointed at.</param>
    /// <returns>The matching snapshot, or the "nothing loaded" sentinel.</returns>
    public FileTreeSnapshot FileTreeFor(string id, string directory)
        => FileTrees.TryGetValue(id, out FileTreeSnapshot? snapshot) && snapshot.Covers(directory)
            ? snapshot
            : FileTreeSnapshot.None;

    /// <summary>The loaded comment cards for <paramref name="id" />, or an empty array.</summary>
    public ImmutableArray<PrCommentCard> PrCommentsFor(string id)
        => PrComments.TryGetValue(id, out var cards) ? cards : ImmutableArray<PrCommentCard>.Empty;

    public static readonly TerminalUiState Empty = new();

    /// <summary>Return a snapshot with the editable input model replaced.</summary>
    public TerminalUiState SetInput(InputModel input) => this with { Input = input };

    /// <summary>Return a snapshot with the keyboard focus replaced.</summary>
    public TerminalUiState SetFocus(FocusMode focus) => this with { Focus = focus };

    /// <summary>
    ///     Replace one entry of a panel-keyed immutable map. This is the single
    ///     generic form of the <c>Ui with { Field = Field.SetItem(id, value) }</c>
    ///     pattern used by the panel arms in <see cref="AppReducer"/>;
    ///     <paramref name="replace"/> writes the updated map back because C#
    ///     has no generic <c>with</c> expression.
    /// </summary>
    internal TerminalUiState UpdatePanelField<T>(
        Func<TerminalUiState, ImmutableDictionary<string, T>> field,
        Func<TerminalUiState, ImmutableDictionary<string, T>, TerminalUiState> replace,
        string id,
        T value)
        => replace(this, field(this).SetItem(id, value));

    /// <summary>Resolve a viewport name; null/empty addresses the default viewport.</summary>
    private static string ViewportKey(string? viewportName)
        => string.IsNullOrEmpty(viewportName) ? DefaultViewport : viewportName;

    private bool IsDefault(string? viewportName) => ViewportKey(viewportName) == DefaultViewport;

    /// <summary>
    ///     The complete scroll triple for <paramref name="viewportName"/>. A
    ///     missing named key is the honest "nothing measured yet" state: zeros.
    /// </summary>
    public (int Offset, int Viewport, int Total) ScrollOf(string? viewportName)
        => IsDefault(viewportName)
            ? (ScrollOffset, ViewportLines, TotalLines)
            : ViewportScrolls.TryGetValue(ViewportKey(viewportName), out var s) ? s : (0, 0, 0);

    /// <summary>Set the visible-row count of one viewport, touching no other viewport.</summary>
    public TerminalUiState SetViewportLines(string? viewportName, int lines)
    {
        if (IsDefault(viewportName)) return this with { ViewportLines = lines };
        var s = ScrollOf(viewportName);
        return this with
        {
            ViewportScrolls = ViewportScrolls.SetItem(ViewportKey(viewportName), (s.Offset, lines, s.Total))
        };
    }

    /// <summary>Set the measured total of one viewport, touching no other viewport.</summary>
    public TerminalUiState SetTotalLines(string? viewportName, int total)
    {
        if (IsDefault(viewportName)) return this with { TotalLines = total };
        var s = ScrollOf(viewportName);
        return this with
        {
            ViewportScrolls = ViewportScrolls.SetItem(ViewportKey(viewportName), (s.Offset, s.Viewport, total))
        };
    }

    /// <summary>Pin one viewport to its live tail, touching no other viewport.</summary>
    public TerminalUiState ResetScrollToTail(string? viewportName)
    {
        if (IsDefault(viewportName)) return this with { ScrollOffset = 0 };
        var s = ScrollOf(viewportName);
        return this with
        {
            ViewportScrolls = ViewportScrolls.SetItem(ViewportKey(viewportName), (0, s.Viewport, s.Total))
        };
    }

    /// <summary>Clamp one viewport's offset to a host-measured maximum.</summary>
    public TerminalUiState ClampScroll(string? viewportName, int maxScroll)
    {
        if (IsDefault(viewportName))
            return this with { ScrollOffset = Math.Clamp(ScrollOffset, 0, Math.Max(0, maxScroll)) };
        var s = ScrollOf(viewportName);
        int clamped = Math.Clamp(s.Offset, 0, Math.Max(0, maxScroll));
        return this with
        {
            ViewportScrolls = ViewportScrolls.SetItem(ViewportKey(viewportName), (clamped, s.Viewport, s.Total))
        };
    }

    /// <summary>Return a snapshot with one viewport's offset clamped to its own measured range.</summary>
    public TerminalUiState SetScroll(string? viewportName, int offset)
    {
        if (IsDefault(viewportName)) return SetScroll(offset);
        var s = ScrollOf(viewportName);
        int max = Math.Max(0, s.Total - s.Viewport);
        int clamped = Math.Clamp(offset, 0, max);
        return clamped == s.Offset ? this : this with
        {
            ViewportScrolls = ViewportScrolls.SetItem(ViewportKey(viewportName), (clamped, s.Viewport, s.Total))
        };
    }

    /// <summary>Return a snapshot with the default viewport's offset clamped to valid range.</summary>
    public TerminalUiState SetScroll(int offset)
    {
        int max = Math.Max(0, TotalLines - ViewportLines);
        int clamped = Math.Clamp(offset, 0, max);
        return clamped == ScrollOffset ? this : this with { ScrollOffset = clamped };
    }
}
