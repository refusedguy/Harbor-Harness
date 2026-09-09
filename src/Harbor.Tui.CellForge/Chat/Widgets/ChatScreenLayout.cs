using Harbor.Tui.CellForge.Panels;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using FrameworkStatusMappers = Harbor.Ui.Framework.Converters.StatusMappers;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Bottom composer leaf: paints the <see cref="Rendering.ComposerController"/>
/// snapshot with a reverse-video caret cell. Input handling stays in the
/// focus router — this panel is projection-only.
/// </summary>
public sealed class ComposerPanel : Panel
{
    private static readonly CellStyle CaretStyle = new(attrs: StyleAttr.Reverse);
    private static readonly CellStyle PlaceholderStyle = ChatPalette.Dim;

    public ComposerPanel(string id, Rendering.ComposerController composer, int minWidth, int minHeight, int priority = 5)
        : base(id, new Size(minWidth, minHeight), priority)
    {
        Composer = composer;
    }

    public Rendering.ComposerController Composer { get; }

    public string? Placeholder { get; set; }

    public override void Paint(ScreenBuffer buffer)
    {
        if (Rect.Width <= 0 || Rect.Height <= 0)
        {
            return;
        }

        // Zero-alloc steady-state paint (renderer-moat): iterate the live
        // buffer span instead of SnapshotText()+Split, which allocated a
        // string plus an array every frame.
        ReadOnlySpan<char> snapshot = Composer.Buffer.AsSpan();
        int caret = Composer.Buffer.Cursor;

        // Locate the caret row/col inside the logical lines.
        int caretRow = 0, caretCol = 0, seen = 0;
        while (true)
        {
            int newline = snapshot[seen..].IndexOf('\n');
            int len = newline < 0 ? snapshot.Length - seen : newline;
            if (caret <= seen + len)
            {
                caretCol = caret - seen;
                break;
            }

            if (newline < 0)
            {
                caretCol = snapshot.Length - seen;
                break;
            }

            seen += newline + 1; // '\n'
            caretRow = Math.Min(caretRow + 1, Rect.Height - 1);
            caretCol = 0;
        }

        // Erase the previous frame's composer content first: the back buffer
        // persists across frames and SetText("") is a no-op, so any shrink
        // (Ctrl+C clear, Ctrl+U/K kill, backspace, shorter history recall)
        // would otherwise leave ghost characters on the emulated grid.
        buffer.Fill(Rect, Cell.Blank);

        int rowStart = 0;
        for (int row = 0; row < Rect.Height; row++)
        {
            int newline = snapshot[rowStart..].IndexOf('\n');
            int lineLen = newline < 0 ? snapshot.Length - rowStart : newline;
            var text = snapshot.Slice(rowStart, lineLen);
            buffer.SetText(Rect.X, Rect.Y + row, text, CellStyle.Plain);

            if (text.Length == 0 && snapshot.Length == 0 && !string.IsNullOrEmpty(Placeholder))
            {
                buffer.SetText(Rect.X, Rect.Y, Placeholder, PlaceholderStyle);
            }

            if (newline < 0)
            {
                break;
            }

            rowStart += newline + 1;
        }

        if (caretRow < Rect.Height && caretCol <= Rect.Width)
        {
            buffer.SetStyleAt(Math.Min(Rect.X + caretCol, Rect.Right - 1), Rect.Y + caretRow, CaretStyle);
        }
    }
}

/// <summary>
/// CF-D-002 projector seam: builds <see cref="StatusSeg"/> rows from the
/// shared <see cref="UiState"/> source of truth instead of hand-assembled
/// view-model strings. Glyphs (<c>▌/◐/✗/○</c>) and the <c>live</c>/<c>scroll N%</c>
/// segment come verbatim from <see cref="StatusProjector.ProjectStatusBar"/>
/// (classified by its stable <c>(Align, Importance)</c> contract, never by
/// text sniffing); numbers are reformatted through
/// <see cref="FrameworkStatusMappers"/> (<c>TokensToCompact</c> /
/// <c>CostToUsd</c> / <c>DurationToText</c>). Zero cost is hidden (no data ⇒
/// no segment, never <c>$0.0000</c>). The spinner (<see cref="SpinnerStrip"/>)
/// and retry (<see cref="RetryCountdown"/>) slots stay tick-/host-driven —
/// this projector only owns their segment placement, not their clocks.
/// </summary>
public static class StatusProjectorPanel
{
    /// <summary>Capacity: chrome, status, agent, retry, scroll, elapsed, tokens, cost.</summary>
    public const int MaxSegments = 8;

    /// <summary>Maps <see cref="UiState.Status"/> text to the footer machine mode.</summary>
    public static StatusBarMode MapMode(string? status) => status switch
    {
        "running" => StatusBarMode.Running,
        "compacting" => StatusBarMode.Compacting,
        _ => StatusBarMode.Idle,
    };

    /// <summary>Spinner rhythm for a footer mode (null = no spinner slot).</summary>
    public static SpinnerRhythm? MapRhythm(StatusBarMode mode) => mode switch
    {
        StatusBarMode.Running or StatusBarMode.Compacting => SpinnerRhythm.Working,
        StatusBarMode.AwaitingApproval => SpinnerRhythm.Awaiting,
        _ => null,
    };

    /// <summary>Maps a projector span style to a footer accent.</summary>
    public static StatusAccent MapAccent(UiSpanStyle? style) => style switch
    {
        UiSpanStyle.Dim => StatusAccent.Dim,
        UiSpanStyle.Accent => StatusAccent.Accent,
        UiSpanStyle.Danger => StatusAccent.Error,
        UiSpanStyle.Success => StatusAccent.Success,
        _ => StatusAccent.Neutral,
    };

    /// <summary>
    /// Fills <paramref name="workspace"/> left-to-right from
    /// <see cref="StatusProjector.ProjectStatusBar"/>; returns segment count.
    /// Order keeps the documented truncation contract (tokens/cost rightmost,
    /// die first): chrome, status, agent, retry, scroll, elapsed, tokens, cost.
    /// </summary>
    /// <param name="state">Projected UI snapshot (source of truth).</param>
    /// <param name="workspace">Target span (at least <see cref="MaxSegments"/> cells).</param>
    /// <param name="retryLine">Precomputed <see cref="RetryCountdown.Line"/> text; null when no retry is pending.</param>
    /// <param name="elapsed">Optional run duration, formatted via <c>DurationToText</c> (sub-ms hides).</param>
    public static int BuildSegments(UiState state, Span<StatusSeg> workspace, string? retryLine = null, TimeSpan? elapsed = null)
    {
        var bar = StatusProjector.ProjectStatusBar(state);

        string? chrome = null;
        string? statusText = null;
        StatusAccent statusAccent = StatusAccent.Neutral;
        string? agent = null;
        StatusAccent agentAccent = StatusAccent.Neutral;
        string? scroll = null;
        StatusAccent scrollAccent = StatusAccent.Dim;

        foreach (var seg in bar.Segments)
        {
            switch (seg.Align, seg.Importance)
            {
                case (Alignment.Left, 1):
                    chrome = seg.Text;
                    break;
                case (Alignment.Center, 2):
                    statusText = seg.Text;
                    statusAccent = MapAccent(seg.Style);
                    break;
                case (Alignment.Right, 3):
                    agent = seg.Text;
                    agentAccent = MapAccent(seg.Style);
                    break;
                case (Alignment.Right, 0):
                    scroll = seg.Text;
                    scrollAccent = MapAccent(seg.Style);
                    break;
            }
        }

        // The projector always emits "provider/model" verbatim — even "/" when
        // both are unknown. No data ⇒ no segment, never a bare separator.
        if (string.IsNullOrEmpty(state.Provider) && string.IsNullOrEmpty(state.Model))
        {
            chrome = null;
        }
        else if (chrome is not null && (string.IsNullOrEmpty(state.Provider) || string.IsNullOrEmpty(state.Model)))
        {
            chrome = string.IsNullOrEmpty(state.Provider) ? state.Model : state.Provider;
        }

        string? tokens = null;
        if (state.Cost.TokensIn > 0 || state.Cost.TokensOut > 0)
        {
            tokens = FrameworkStatusMappers.TokensToCompact(state.Cost.TokensIn)
                + "↑ "
                + FrameworkStatusMappers.TokensToCompact(state.Cost.TokensOut)
                + "↓";
        }

        // Zero/negative cost hides (grok None-semantics) instead of "$0.0000".
        string? cost = state.Cost.CostUsd > 0
            ? FrameworkStatusMappers.CostToUsd(state.Cost.CostUsd)
            : null;

        string? elapsedText = elapsed.HasValue
            ? FrameworkStatusMappers.DurationToText(elapsed.Value)
            : null;
        if (string.IsNullOrEmpty(elapsedText))
        {
            elapsedText = null;
        }

        int n = 0;
        if (chrome is not null && n < workspace.Length)
        {
            workspace[n++] = new StatusSeg(chrome, StatusAccent.Accent, FixedPriority: true);
        }

        if (statusText is not null && n < workspace.Length)
        {
            workspace[n++] = new StatusSeg(statusText, statusAccent, FixedPriority: true);
        }

        if (agent is not null && n < workspace.Length)
        {
            workspace[n++] = new StatusSeg(agent, agentAccent, FixedPriority: false);
        }

        if (!string.IsNullOrEmpty(retryLine) && n < workspace.Length)
        {
            workspace[n++] = new StatusSeg(retryLine!, StatusAccent.Warning, FixedPriority: true);
        }

        if (scroll is not null && n < workspace.Length)
        {
            workspace[n++] = new StatusSeg(scroll, scrollAccent, FixedPriority: false);
        }

        if (elapsedText is not null && n < workspace.Length)
        {
            workspace[n++] = new StatusSeg(elapsedText, StatusAccent.Dim, FixedPriority: false);
        }

        if (tokens is not null && n < workspace.Length)
        {
            workspace[n++] = new StatusSeg(tokens, StatusAccent.Dim, FixedPriority: false);
        }

        if (cost is not null && n < workspace.Length)
        {
            workspace[n++] = new StatusSeg(cost, StatusAccent.Dim, FixedPriority: false);
        }

        return n;
    }
}

/// <summary>
/// Status footer leaf: spinner glyph (mode-driven rhythm) + fitted
/// <see cref="StatusViewModel"/> segments on one row, with an ambient
/// <see cref="AmbientMascot"/> framed at the trailing edge on wide terminals
/// (sprint UI-V2 P6.1 — the mascot is ambient, so it never competes with
/// status segments on narrow rows).
/// CF-D-002: when <see cref="ProjectedState"/> is set, segments come from
/// <see cref="StatusProjectorPanel"/> over <see cref="UiState"/> (glyphs +
/// scroll from <c>StatusProjector</c>, numbers via <c>StatusMappers</c>)
/// instead of <see cref="StatusViewModel.BuildSegments"/>; geometry, spinner
/// rhythm mapping, truncation and mascot behavior are unchanged.
/// </summary>
public sealed class StatusPanel : Panel
{
    private const int MascotGap = 2;

    /// <summary>Minimum row width for the mascot (frame + gap + usable segments).</summary>
    public const int MascotMinWidth = 100;

    /// <summary>Idle uptime (ms) before the mascot dozes off.</summary>
    public const int MascotSleepAfterMs = 60_000;

    /// <summary>
    /// HARBOR_MASCOT=off disables the ambient cat (accessibility, CI determinism);
    /// resolved once via <see cref="MascotModeEnv" />, never per-frame.
    /// </summary>
    private static readonly bool MascotEnabled = MascotModeEnv.Value is not MascotMode.Off;

    /// <summary>
    /// Footer-cat gate — <see cref="ChatScreen.Build" /> clears it when the
    /// panel mode owns the cat or the mascot is off entirely.
    /// </summary>
    public bool FooterMascotEnabled { get; set; } = true;

    private readonly StatusSeg[] _compose = new StatusSeg[12];
    private readonly MascotDirector _director = new();
    private byte _lastMode;
    private bool _modeSeen;
    private long _modeFlipTick = long.MinValue;

    public StatusPanel(string id, StatusViewModel status, int minWidth, int minHeight, int priority = int.MaxValue)
        : base(id, new Size(minWidth, minHeight), priority)
    {
        Vm = status;
    }

    public StatusViewModel Vm { get; }

    /// <summary>
    /// CF-D-002 projector feed: when set, the row projects
    /// <see cref="StatusProjectorPanel.BuildSegments"/> from this snapshot
    /// instead of <see cref="StatusViewModel.BuildSegments"/>. Null (default)
    /// keeps the legacy view-model path, so existing hosts are unaffected.
    /// Mascot mood/phase still derive from <see cref="Vm"/>; only the mode
    /// (spinner rhythm + flip crossfade) derives from the projected status.
    /// </summary>
    public UiState? ProjectedState { get; set; }

    /// <summary>
    /// Precomputed <see cref="RetryCountdown.Line"/> text for the projected
    /// path (set once per change via <see cref="SetProjectedRetry"/>, never
    /// interpolated per frame). Null when no retry is pending.
    /// </summary>
    public string? ProjectedRetry { get; set; }

    /// <summary>
    /// Optional run duration for the projected path, formatted via
    /// <c>StatusMappers.DurationToText</c> (sub-ms hides the segment).
    /// </summary>
    public TimeSpan? ProjectedElapsed { get; set; }

    /// <summary>Feeds the projected retry slot from attempt counters.</summary>
    public void SetProjectedRetry(int attempt, int maxAttempts, int secondsRemaining) =>
        ProjectedRetry = RetryCountdown.Line(attempt, maxAttempts, secondsRemaining);

    private UiState? _projectedCacheState;
    private string? _projectedCacheRetry;
    private TimeSpan? _projectedCacheElapsed;
    private readonly StatusSeg[] _projectedCache = new StatusSeg[StatusProjectorPanel.MaxSegments];
    private int _projectedCacheCount;

    /// <summary>
    /// Cached projection: <see cref="UiState"/> snapshots are immutable, so a
    /// steady frame reuses the last row (zero-alloc); only the spinner slot
    /// above it is tick-dependent. Invalidated on snapshot/retry/elapsed change.
    /// </summary>
    private int ProjectProjected(UiState state, Span<StatusSeg> target)
    {
        string? retry = ProjectedRetry;
        TimeSpan? elapsed = ProjectedElapsed;
        if (!ReferenceEquals(_projectedCacheState, state)
            || _projectedCacheRetry != retry
            || _projectedCacheElapsed != elapsed)
        {
            _projectedCacheCount = StatusProjectorPanel.BuildSegments(state, _projectedCache, retry, elapsed);
            _projectedCacheState = state;
            _projectedCacheRetry = retry;
            _projectedCacheElapsed = elapsed;
        }

        _projectedCache.AsSpan(0, _projectedCacheCount).CopyTo(target);
        return _projectedCacheCount;
    }

    /// <summary>Frame tick source — incremented once per paint by the pipeline.</summary>
    public long Tick { get; private set; }

    public override void Paint(ScreenBuffer buffer)
    {
        Tick++;
        if (Rect.Width <= 2 || Rect.Height <= 0)
        {
            return;
        }

        // Smooth state transition: on a mode flip (running ⇄ approval-wait ⇄
        // compaction …) crossfade the whole row in over the HDS micro fade.
        // CF-D-002: the projected path derives the mode from UiState.Status.
        UiState? projected = ProjectedState;
        StatusBarMode effectiveMode = projected is not null
            ? StatusProjectorPanel.MapMode(projected.Status)
            : Vm.Mode;
        byte mode = (byte)effectiveMode;
        if (!_modeSeen)
        {
            _lastMode = mode;
            _modeSeen = true;
        }
        else if (mode != _lastMode)
        {
            _lastMode = mode;
            _modeFlipTick = Tick;
        }

        int n = projected is not null
            ? ProjectProjected(projected, _compose.AsSpan(1))
            : Vm.BuildSegments(_compose.AsSpan(1));
        int total = n + 1;

        SpinnerRhythm? rhythm = StatusProjectorPanel.MapRhythm(effectiveMode);

        if (rhythm is null)
        {
            ShiftLeft(n); // drop the reserved slot
            total = n;
        }
        else
        {
            _compose[0] = new StatusSeg(SpinnerStrip.FrameString(Tick, rhythm.Value), StatusAccent.Accent, FixedPriority: true);
        }

        bool footerMascot = MascotEnabled && FooterMascotEnabled && Rect.Width >= MascotMinWidth;
        string? mascot = null;
        var mascotStyle = ChatPalette.Dim;
        if (footerMascot)
        {
            // Footer mode: the footer owns the one-shot event signal.
            MascotReaction signal = Vm.ConsumeMascotSignal();
            if (signal != MascotReaction.None)
            {
                _director.Notify(signal, Tick);
            }

            MascotMood mood = _director.Advance(Vm, Tick);
            if (_director.TryReactionFrame(Tick, out MascotReaction active, out int ridx))
            {
                mascot = AmbientMascot.ReactionFramesOf(active)[ridx];
                mascotStyle = MascotDirector.ReactionStyle(active);
            }
            else
            {
                mascot = AmbientMascot.Frame(Tick, mood);
            }
        }

        Span<StatusSeg> span = _compose;
        int budget = Rect.Width - 1 - (mascot is null ? 0 : AmbientMascot.Width(mascot) + MascotGap);
        int kept = StatusBarLayout.Fit(span[..total], budget);
        StatusBarWidget.Paint(buffer, new Rect(Rect.X, Rect.Y, Rect.Width, 1), span[..kept]);

        if (mascot is not null)
        {
            buffer.SetText(Rect.Right - mascot.Length, Rect.Y, mascot, mascotStyle);
        }

        long flip = _modeFlipTick;
        bool rowCrossfading = false;
        if (flip != long.MinValue)
        {
            double ramp = PanelFx.AccentRamp(flip, Tick);
            if (ramp >= 1.0)
            {
                _modeFlipTick = long.MinValue;
            }
            else
            {
                PanelFx.BlendRegion(buffer, new Rect(Rect.X, Rect.Y, Rect.Width, 1), ramp);
                rowCrossfading = true;
            }
        }

        // Mascot-region crossfades (mascot-brand T1/T3) — skipped while the
        // whole-row mode crossfade already covers them; the reaction overlay
        // wins over the mood crossfade while it owns the cells.
        if (!rowCrossfading && mascot is not null)
        {
            var mascotRect = new Rect(Rect.Right - mascot.Length, Rect.Y, mascot.Length, 1);
            if (!_director.BlendReaction(buffer, mascotRect, Tick))
            {
                _ = _director.BlendMoodCrossfade(buffer, mascotRect, Tick);
            }
        }
    }

    private void ShiftLeft(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _compose[i] = _compose[i + 1];
        }
    }
}

/// <summary>
/// Right sidebar leaf (sprint UI-V2 P4): paints <see cref="SideBarView" />
/// into the resolved rect. Minimum width pins the 42-column context panel on
/// wide terminals; priority 5 makes it collapse first when the terminal is
/// too narrow (auto-show policy lives in <see cref="SideBarLayout" />).
/// </summary>
public sealed class SideBarPanel : Panel
{
    public const string DefaultId = "chat.sidebar";

    private volatile SideBarState _state = SideBarState.Empty;

    public SideBarPanel(string id, int minWidth = SideBarLayout.DefaultWidth, int priority = 5)
        : base(id, new Size(minWidth, 1), priority)
    {
    }

    /// <summary>Latest sidebar snapshot — replaced wholesale by the host.</summary>
    public SideBarState State
    {
        get => _state;
        set => _state = value ?? SideBarState.Empty;
    }

    /// <summary>Plugin-contributed slots painted below the built-in sections.</summary>
    public IReadOnlyList<SideBarSlot>? Slots { get; set; }

    public override void Paint(ScreenBuffer buffer)
    {
        if (Rect.Width <= 0 || Rect.Height <= 0)
        {
            return;
        }

        SideBarView.Paint(buffer, Rect, State, Slots);
    }
}

/// <summary>Assembled chat screen: timeline above, composer below, status footer.</summary>
public sealed record ChatScreen(LayoutTree Tree, ChatTimelinePanel Timeline, ComposerPanel Composer, StatusPanel Status, SideBarPanel? Sidebar = null, MascotPanel? Mascot = null)
{
    public const string TimelineId = "chat.timeline";
    public const string ComposerId = "chat.composer";
    public const string StatusId = "chat.status";
    public const string SidebarId = SideBarPanel.DefaultId;
    public const string MascotId = MascotPanel.DefaultId;

    public static ChatScreen Build(
        Rendering.ComposerController composer,
        StatusViewModel status,
        float timelineRatio = 0.82f,
        int minComposerRows = 3,
        bool includeSidebar = true,
        MascotMode? mascotMode = null)
    {
        var tree = new LayoutTree();
        // Pin the auto-show policy (SideBarLayout.AutoShowMinWidth = 120):
        // with the sidebar present the timeline keeps
        // (AutoShowMinWidth − DefaultWidth − gap) columns minimum, so the
        // solver collapses the sidebar (priority 5) below 120 total columns
        // in favor of the timeline (priority 10).
        int timelineMinWidth = includeSidebar
            ? SideBarLayout.AutoShowMinWidth - SideBarLayout.DefaultWidth - 1
            : 20;
        var timeline = new ChatTimelinePanel(TimelineId, minWidth: timelineMinWidth, minHeight: 4, priority: 10);
        var composerPanel = new ComposerPanel(ComposerId, composer, minWidth: 10, minHeight: minComposerRows, priority: 50);
        var statusRow = new StatusPanel(StatusId, status, minWidth: 10, minHeight: 1, priority: int.MaxValue);
        timeline.Timeline.EnableEntranceFx();

        SideBarPanel? sidebar = includeSidebar
            ? new SideBarPanel(SidebarId, minWidth: SideBarLayout.DefaultWidth, priority: 5)
            : null;

        tree.AddRoot(timeline);
        tree.Split(TimelineId, SplitDir.Vertical, timelineRatio, composerPanel, gap: 0);
        tree.Split(ComposerId, SplitDir.Vertical, ratio: 1f - (1f / Math.Max(2, minComposerRows)), statusRow, gap: 0);
        if (sidebar is not null)
        {
            tree.Split(TimelineId, SplitDir.Horizontal, 0.74f, sidebar, gap: 1);
        }

        // Panel-mode mascot (mascot-brand T2): sits beside the composer so it
        // gets composer-height rows while the status row keeps the full width.
        // The status row spans both because the horizontal split nests INSIDE
        // the composer branch, below the composer⇄status vertical split.
        MascotPanel? mascotPanel = null;
        MascotMode resolved = mascotMode ?? MascotModeEnv.Value;
        if (resolved is MascotMode.Panel && MascotModeEnv.Value is not MascotMode.Off)
        {
            mascotPanel = new MascotPanel(MascotId, status, priority: 4);
            statusRow.FooterMascotEnabled = false;
            tree.Split(ComposerId, SplitDir.Horizontal, 0.88f, mascotPanel, gap: 1);
        }
        else if (resolved is MascotMode.Off)
        {
            statusRow.FooterMascotEnabled = false;
        }

        return new ChatScreen(tree, timeline, composerPanel, statusRow, sidebar, mascotPanel);
    }
}

/// <summary>
/// CF-E-002 wiring (TOP-1 #27): <see cref="LayoutTree"/> leaf hosting every
/// visible panel of one dock placement. The leaf owns no state of its own — each
/// frame it renders its providers through
/// <see cref="CellForgePanelAdapter.RenderToRows(IPanelProvider, UiState, int, int, IServiceProvider)"/>
/// into its resolved <see cref="Panel.Rect"/> and blits the rows as plain cells.
/// <see cref="StatusPanel"/> / <see cref="ComposerPanel"/> are untouched: dock
/// leaves are extra splits around the timeline band, never replacements.
/// </summary>
public sealed class CellForgeDockPanel : Panel
{
    /// <summary>Layout-tree id of the left-dock leaf.</summary>
    public const string LeftId = "chat.panels.left";

    /// <summary>Layout-tree id of the right-dock leaf.</summary>
    public const string RightId = "chat.panels.right";

    /// <summary>Layout-tree id of the bottom-dock leaf.</summary>
    public const string BottomId = "chat.panels.bottom";

    /// <summary>Collapse priority of the side docks (same as the sidebar: they collapse first).</summary>
    public const int SidePriority = 5;

    /// <summary>Collapse priority of the bottom dock (after side docks, before the timeline).</summary>
    public const int BottomPriority = 6;

    public CellForgeDockPanel(string id, TuiPanelPlacement placement, int priority)
        : base(id, new Size(1, 1), priority)
    {
        Placement = placement;
    }

    /// <summary>Which placement this leaf hosts (Left, Right or Bottom).</summary>
    public TuiPanelPlacement Placement { get; }

    /// <summary>Visible providers of <see cref="Placement"/> (refreshed by <see cref="ChatScreenPanelDock"/>).</summary>
    public IReadOnlyList<IPanelProvider> Providers { get; set; } = Array.Empty<IPanelProvider>();

    /// <summary>Registry view carrying per-panel size overrides (null = every provider uses <c>DefaultSize</c>).</summary>
    public PanelRegistryView? View { get; set; }

    /// <summary>Latest UI snapshot (refreshed by <see cref="ChatScreenPanelDock"/>).</summary>
    public UiState State { get; set; } = new UiState();

    /// <summary>DI services for panels that need them (help/logs); null degrades gracefully.</summary>
    public IServiceProvider? Services { get; set; }

    public override void Paint(ScreenBuffer buffer)
    {
        if (Rect.Width <= 0 || Rect.Height <= 0 || Providers.Count == 0)
        {
            return;
        }

        // Erase first: the back buffer persists across frames, so a shrinking
        // panel (toggle-off, resize, shorter rows) must not leave ghosts.
        buffer.Fill(Rect, Cell.Blank);

        if (Placement is TuiPanelPlacement.Left or TuiPanelPlacement.Right)
        {
            PaintSide(buffer);
        }
        else
        {
            PaintStacked(buffer);
        }
    }

    /// <summary>
    /// Side docks: every provider spans the full leaf width (the region width
    /// already is the max provider size — see <see cref="ChatScreenPanelDock"/>)
    /// and providers share the leaf height in equal slices (last takes the
    /// remainder). Equal shares keep toggle churn out of the geometry: showing
    /// a second side panel never re-wraps the first one's rows.
    /// </summary>
    private void PaintSide(ScreenBuffer buffer)
    {
        int y = Rect.Y;
        int remaining = Rect.Height;
        for (int i = 0; i < Providers.Count && remaining > 0; i++)
        {
            int left = Providers.Count - i;
            int h = Math.Max(1, remaining / left);
            h = Math.Min(h, remaining);
            var rows = CellForgePanelAdapter.RenderToRows(Providers[i], State, Rect.Width, h, Services);
            Blit(buffer, Rect.X, y, Rect.Width, rows, h);
            y += h;
            remaining -= h;
        }
    }

    /// <summary>
    /// Bottom dock (and any other vertical placement): providers stack top-down,
    /// each reserving its own size (<see cref="PanelRegistryView.GetSize"/> or
    /// <c>DefaultSize</c>) so a panel's rows never shift when a neighbour below
    /// toggles.
    /// </summary>
    private void PaintStacked(ScreenBuffer buffer)
    {
        int y = Rect.Y;
        int remaining = Rect.Height;
        for (int i = 0; i < Providers.Count && remaining > 0; i++)
        {
            int h = Math.Min(ChatScreenPanelDock.SizeOf(View, Providers[i]), remaining);
            var rows = CellForgePanelAdapter.RenderToRows(Providers[i], State, Rect.Width, h, Services);
            Blit(buffer, Rect.X, y, Rect.Width, rows, h);
            y += h;
            remaining -= h;
        }
    }

    /// <summary>
    /// Blits at most <paramref name="h"/> rows at (<paramref name="x"/>, <paramref name="y"/>).
    /// Rows are hard-sliced to <paramref name="w"/> columns as belt-and-braces:
    /// builtins pre-clip to the passed geometry themselves, but a third-party
    /// provider that ignores it must not overwrite the neighbouring rect.
    /// </summary>
    private static void Blit(ScreenBuffer buffer, int x, int y, int w, IReadOnlyList<string> rows, int h)
    {
        int n = Math.Min(rows.Count, h);
        for (int i = 0; i < n; i++)
        {
            string row = rows[i] ?? string.Empty;
            buffer.SetText(x, y + i, row.AsSpan(0, Math.Min(row.Length, w)), CellStyle.Plain);
        }
    }
}

/// <summary>
/// CF-E-002 wiring (TOP-1 #27): attaches <see cref="CellForgeDockPanel"/> leaves
/// to a <see cref="ChatScreen"/> tree and routes focused-panel input. Only the
/// Left / Right / Bottom placements get dock regions; Top / Center / FloatingTab
/// providers (and every visible panel when no dock leaf exists) render through
/// the <see cref="PaintBottomStack"/> minimum fallback instead — a plain
/// bottom-stack under the timeline that needs no <see cref="LayoutTree"/> space
/// reservation. Prefer <see cref="AttachPanels"/> (it reserves solver space so
/// panels never paint over the composer/status rows); use the fallback only when
/// the host never attached docks (e.g. lightweight tests, narrow viewports where
/// the solver collapsed every dock).
/// </summary>
public static class ChatScreenPanelDock
{
    /// <summary>
    /// Effective size of a provider: the <see cref="PanelRegistryView.GetSize"/>
    /// override (rows for Top/Bottom, columns for Left/Right) or the provider's
    /// <c>DefaultSize</c> when no override is stored. At least 1.
    /// </summary>
    internal static int SizeOf(PanelRegistryView? view, IPanelProvider provider)
    {
        int size = view?.GetSize(provider.Id) ?? 0;
        return Math.Max(1, size > 0 ? size : provider.DefaultSize);
    }

    /// <summary>True when at least one dock leaf is currently attached to the tree.</summary>
    public static bool HasDocks(ChatScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        foreach (var panel in screen.Tree.Panels)
        {
            if (panel is CellForgeDockPanel)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Attaches one <see cref="CellForgeDockPanel"/> per placement that has a
    /// visible panel (Left / Right / Bottom), sized from
    /// <see cref="PanelRegistryView.GetSize"/> (fallback: <c>DefaultSize</c>).
    /// Idempotent: previous dock leaves are removed first, so re-attaching on a
    /// visibility/size change never duplicates leaves. The tree is left solved
    /// for (<paramref name="viewportWidth"/>, <paramref name="viewportHeight"/>);
    /// <see cref="StatusPanel"/> / <see cref="ComposerPanel"/> keep their ids,
    /// rects and paint path — docks only split the timeline band.
    /// Geometry: the bottom dock splits the timeline vertically (lands between
    /// timeline and composer, like Spectre's Bottom-above-Input); side docks
    /// split the timeline horizontally with a 1-column gap (like the sidebar).
    /// <see cref="LayoutTree.Split"/> always appends the new leaf as B
    /// (right/below), so the Left dock lands immediately right of the timeline
    /// rather than left of it — Right is attached first so the visual order is
    /// timeline | left | right. True left-of-timeline docking is follow-up work.
    /// </summary>
    public static void AttachPanels(
        ChatScreen screen,
        PanelRegistry registry,
        UiState state,
        IServiceProvider? services,
        int viewportWidth,
        int viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(state);

        foreach (string id in new[] { CellForgeDockPanel.LeftId, CellForgeDockPanel.RightId, CellForgeDockPanel.BottomId })
        {
            foreach (var panel in screen.Tree.Panels)
            {
                if (panel.Id == id)
                {
                    screen.Tree.Remove(id);
                    break;
                }
            }
        }

        var view = registry.View(state);
        var left = view.GetVisibleByPlacement(TuiPanelPlacement.Left);
        var right = view.GetVisibleByPlacement(TuiPanelPlacement.Right);
        var bottom = view.GetVisibleByPlacement(TuiPanelPlacement.Bottom);

        bool timelinePresent = false;
        foreach (var panel in screen.Tree.Panels)
        {
            if (panel.Id == ChatScreen.TimelineId)
            {
                timelinePresent = true;
                break;
            }
        }

        if (viewportWidth > 0 && viewportHeight > 0)
        {
            screen.Tree.Solve(viewportWidth, viewportHeight);
        }

        if (!timelinePresent)
        {
            return;
        }

        // Bottom first (vertical split under the timeline), then Right, then
        // Left — see the visual-order note above.
        if (bottom.Count > 0)
        {
            int h = 0;
            for (int i = 0; i < bottom.Count; i++)
            {
                h += SizeOf(view, bottom[i]);
            }

            var leaf = new CellForgeDockPanel(CellForgeDockPanel.BottomId, TuiPanelPlacement.Bottom, CellForgeDockPanel.BottomPriority)
            {
                Providers = bottom,
                View = view,
                State = state,
                Services = services,
            };
            int avail = Math.Max(1, screen.Timeline.Rect.Height);
            float ratio = Math.Clamp((float)(avail - Math.Min(h, avail - 1)) / avail, 0.05f, 0.95f);
            screen.Tree.Split(ChatScreen.TimelineId, SplitDir.Vertical, ratio, leaf, gap: 0);
            if (viewportWidth > 0 && viewportHeight > 0)
            {
                screen.Tree.Solve(viewportWidth, viewportHeight);
            }
        }

        if (right.Count > 0)
        {
            AttachSide(screen, view, right, TuiPanelPlacement.Right, CellForgeDockPanel.RightId, state, services, viewportWidth, viewportHeight);
        }

        if (left.Count > 0)
        {
            AttachSide(screen, view, left, TuiPanelPlacement.Left, CellForgeDockPanel.LeftId, state, services, viewportWidth, viewportHeight);
        }

        if (viewportWidth > 0 && viewportHeight > 0)
        {
            screen.Tree.Solve(viewportWidth, viewportHeight);
        }
    }

    private static void AttachSide(
        ChatScreen screen,
        PanelRegistryView view,
        IReadOnlyList<IPanelProvider> providers,
        TuiPanelPlacement placement,
        string leafId,
        UiState state,
        IServiceProvider? services,
        int viewportWidth,
        int viewportHeight)
    {
        int w = 1;
        for (int i = 0; i < providers.Count; i++)
        {
            w = Math.Max(w, SizeOf(view, providers[i]));
        }

        var leaf = new CellForgeDockPanel(leafId, placement, CellForgeDockPanel.SidePriority)
        {
            Providers = providers,
            View = view,
            State = state,
            Services = services,
        };
        int avail = Math.Max(1, screen.Timeline.Rect.Width);
        const int gap = 1;
        int usable = Math.Max(1, avail - gap);
        float ratio = Math.Clamp((float)(usable - Math.Min(w, usable - 1)) / usable, 0.05f, 0.95f);
        screen.Tree.Split(ChatScreen.TimelineId, SplitDir.Horizontal, ratio, leaf, gap: gap);
        if (viewportWidth > 0 && viewportHeight > 0)
        {
            screen.Tree.Solve(viewportWidth, viewportHeight);
        }
    }

    /// <summary>
    /// Refreshes the payload (providers / view / state / services) of already
    /// attached dock leaves without tree surgery. Call once per frame (or on
    /// every <see cref="UiState"/> change) so rows track the latest snapshot;
    /// call <see cref="AttachPanels"/> only when the visible set or sizes change.
    /// </summary>
    public static void UpdatePanels(ChatScreen screen, PanelRegistry registry, UiState state, IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(state);

        var view = registry.View(state);
        foreach (var panel in screen.Tree.Panels)
        {
            if (panel is CellForgeDockPanel dock)
            {
                dock.Providers = dock.Placement switch
                {
                    TuiPanelPlacement.Left => view.GetVisibleByPlacement(TuiPanelPlacement.Left),
                    TuiPanelPlacement.Right => view.GetVisibleByPlacement(TuiPanelPlacement.Right),
                    _ => view.GetVisibleByPlacement(TuiPanelPlacement.Bottom),
                };
                dock.View = view;
                dock.State = state;
                dock.Services = services;
            }
        }
    }

    /// <summary>
    /// Routes a key to the focused panel's <c>OnKey</c> via
    /// <see cref="CellForgePanelAdapter.RouteKey"/>. Returns false (falls through
    /// to the host's default key map) when nothing is focused, the focused id is
    /// unknown to the registry, or the panel is not in
    /// <see cref="TuiPanelState.Focused"/> — <c>Build</c> runs for every visible
    /// state, but <c>OnKey</c> is a focus-only contract.
    /// </summary>
    public static bool RoutePanelKey(
        PanelRegistry registry,
        UiState state,
        UiKey key,
        IServiceProvider? services,
        int width = 80,
        int height = 24)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(state);

        string? id = state.FocusedPanelId;
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        var provider = registry.Get(id);
        if (provider is null)
        {
            return false;
        }

        var view = registry.View(state);
        if (view.GetState(id) != TuiPanelState.Focused)
        {
            return false;
        }

        // Geometry mirrors the dock layout: side panels are width-bound, stacked
        // panels height-bound; the other axis gets the caller's fallback.
        int w = provider.DefaultPlacement is TuiPanelPlacement.Left or TuiPanelPlacement.Right
            ? SizeOf(view, provider)
            : Math.Max(1, width);
        int h = provider.DefaultPlacement is TuiPanelPlacement.Left or TuiPanelPlacement.Right
            ? Math.Max(1, height)
            : SizeOf(view, provider);
        return CellForgePanelAdapter.RouteKey(provider, key, new PanelContext(state, w, h, services));
    }

    /// <summary>
    /// Minimum fallback when no dock regions exist (docks never attached, or the
    /// solver collapsed every dock on a narrow viewport): paints every visible
    /// panel — any placement — as a bottom-stack under
    /// <paramref name="timelineRect"/>, each reserving its own size. Returns the
    /// rows painted. Unlike <see cref="AttachPanels"/> this reserves no
    /// <see cref="LayoutTree"/> space: slots are blanked before blitting, so the
    /// stack paints over whatever the tree put below the timeline (usually the
    /// composer/status rows). Hosts that can afford tree surgery must prefer
    /// <see cref="AttachPanels"/>.
    /// </summary>
    public static int PaintBottomStack(
        ScreenBuffer buffer,
        Rect timelineRect,
        PanelRegistry registry,
        UiState state,
        IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(state);

        var view = registry.View(state);
        var visible = view.GetVisible();
        if (visible.Count == 0 || timelineRect.Width <= 0)
        {
            return 0;
        }

        int y = timelineRect.Bottom;
        int painted = 0;
        for (int i = 0; i < visible.Count && y < buffer.Rows; i++)
        {
            var provider = visible[i];
            int h = Math.Min(SizeOf(view, provider), buffer.Rows - y);
            if (h <= 0)
            {
                break;
            }

            buffer.Fill(new Rect(timelineRect.X, y, timelineRect.Width, h), Cell.Blank);
            var rows = CellForgePanelAdapter.RenderToRows(provider, state, timelineRect.Width, h, services);
            int n = Math.Min(rows.Count, h);
            for (int r = 0; r < n; r++)
            {
                string row = rows[r] ?? string.Empty;
                buffer.SetText(timelineRect.X, y + r, row.AsSpan(0, Math.Min(row.Length, timelineRect.Width)), CellStyle.Plain);
            }

            y += h;
            painted += h;
        }

        return painted;
    }
}
