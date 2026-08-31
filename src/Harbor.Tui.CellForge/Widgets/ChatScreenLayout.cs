using Harbor.Tui.CellForge.Rendering;

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

        var snapshot = Composer.Buffer.SnapshotText();
        var logical = snapshot.Split('\n');
        int caret = Composer.Buffer.Cursor;

        // Locate the caret row/col inside the logical lines.
        int caretRow = 0, caretCol = 0, seen = 0;
        for (int i = 0; i < logical.Length; i++)
        {
            int len = logical[i].Length;
            if (caret <= seen + len)
            {
                caretRow = i;
                caretCol = caret - seen;
                break;
            }

            seen += len + 1; // '\n'
            caretRow = Math.Min(i + 1, Rect.Height - 1);
            caretCol = 0;
        }

        // Erase the previous frame's composer content first: the back buffer
        // persists across frames and SetText("") is a no-op, so any shrink
        // (Ctrl+C clear, Ctrl+U/K kill, backspace, shorter history recall)
        // would otherwise leave ghost characters on the emulated grid.
        buffer.Fill(Rect, Cell.Blank);

        for (int row = 0; row < Rect.Height; row++)
        {
            var text = row < logical.Length ? logical[row] : string.Empty;
            buffer.SetText(Rect.X, Rect.Y + row, text, CellStyle.Plain);

            if (text.Length == 0 && logical.Length == 1 && !string.IsNullOrEmpty(Placeholder))
            {
                buffer.SetText(Rect.X, Rect.Y, Placeholder, PlaceholderStyle);
            }
        }

        if (caretRow < Rect.Height && caretCol <= Rect.Width)
        {
            buffer.SetStyleAt(Math.Min(Rect.X + caretCol, Rect.Right - 1), Rect.Y + caretRow, CaretStyle);
        }
    }
}

/// <summary>
/// Status footer leaf: spinner glyph (mode-driven rhythm) + fitted
/// <see cref="StatusViewModel"/> segments on one row, with an ambient
/// <see cref="AmbientMascot"/> framed at the trailing edge on wide terminals
/// (sprint UI-V2 P6.1 — the mascot is ambient, so it never competes with
/// status segments on narrow rows).
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
    /// read once, never per-frame.
    /// </summary>
    private static readonly bool MascotEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("HARBOR_MASCOT"), "off", StringComparison.OrdinalIgnoreCase);

    private readonly StatusSeg[] _compose = new StatusSeg[12];
    private byte _lastMode;
    private bool _modeSeen;
    private long _modeFlipTick = long.MinValue;
    private long _lastActiveMs = Environment.TickCount64;

    public StatusPanel(string id, StatusViewModel status, int minWidth, int minHeight, int priority = int.MaxValue)
        : base(id, new Size(minWidth, minHeight), priority)
    {
        Vm = status;
    }

    public StatusViewModel Vm { get; }

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
        byte mode = (byte)Vm.Mode;
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

        if (Vm.Mode != StatusBarMode.Idle)
        {
            _lastActiveMs = Environment.TickCount64;
        }

        int n = Vm.BuildSegments(_compose.AsSpan(1));
        int total = n + 1;

        var rhythm = Vm.Mode switch
        {
            StatusBarMode.Running or StatusBarMode.Compacting => SpinnerRhythm.Working,
            StatusBarMode.AwaitingApproval => SpinnerRhythm.Awaiting,
            _ => (SpinnerRhythm?)null,
        };

        if (rhythm is null)
        {
            ShiftLeft(n); // drop the reserved slot
            total = n;
        }
        else
        {
            _compose[0] = new StatusSeg(SpinnerStrip.FrameString(Tick, rhythm.Value), StatusAccent.Accent, FixedPriority: true);
        }

        string? mascot = MascotEnabled && Rect.Width >= MascotMinWidth
            ? AmbientMascot.Frame(Tick, MascotFor(Vm.Mode))
            : null;

        Span<StatusSeg> span = _compose;
        int budget = Rect.Width - 1 - (mascot is null ? 0 : AmbientMascot.Width(mascot) + MascotGap);
        int kept = StatusBarLayout.Fit(span[..total], budget);
        StatusBarWidget.Paint(buffer, new Rect(Rect.X, Rect.Y, Rect.Width, 1), span[..kept]);

        if (mascot is not null)
        {
            buffer.SetText(Rect.Right - mascot.Length, Rect.Y, mascot, ChatPalette.Dim);
        }

        long flip = _modeFlipTick;
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

    /// <summary>Mascot mood mirrors the status mode; long idle dozes off to sleep.</summary>
    private MascotMood MascotFor(StatusBarMode mode) => mode switch
    {
        StatusBarMode.Running or StatusBarMode.Compacting => MascotMood.Working,
        StatusBarMode.AwaitingApproval => MascotMood.Awaiting,
        _ => Environment.TickCount64 - _lastActiveMs > MascotSleepAfterMs
            ? MascotMood.Sleeping
            : MascotMood.Idle,
    };
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
public sealed record ChatScreen(LayoutTree Tree, ChatTimelinePanel Timeline, ComposerPanel Composer, StatusPanel Status, SideBarPanel? Sidebar = null)
{
    public const string TimelineId = "chat.timeline";
    public const string ComposerId = "chat.composer";
    public const string StatusId = "chat.status";
    public const string SidebarId = SideBarPanel.DefaultId;

    public static ChatScreen Build(
        Rendering.ComposerController composer,
        StatusViewModel status,
        float timelineRatio = 0.82f,
        int minComposerRows = 3,
        bool includeSidebar = true)
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

        return new ChatScreen(tree, timeline, composerPanel, statusRow, sidebar);
    }
}
