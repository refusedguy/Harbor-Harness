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

    public static readonly TerminalUiState Empty = new();

    /// <summary>Return a snapshot with the editable input model replaced.</summary>
    public TerminalUiState SetInput(InputModel input) => this with { Input = input };

    /// <summary>Return a snapshot with the keyboard focus replaced.</summary>
    public TerminalUiState SetFocus(FocusMode focus) => this with { Focus = focus };

    /// <summary>Return a snapshot with the history scroll offset clamped to valid range.</summary>
    public TerminalUiState SetScroll(int offset)
    {
        int max = Math.Max(0, TotalLines - ViewportLines);
        int clamped = Math.Clamp(offset, 0, max);
        return clamped == ScrollOffset ? this : this with { ScrollOffset = clamped };
    }
}
