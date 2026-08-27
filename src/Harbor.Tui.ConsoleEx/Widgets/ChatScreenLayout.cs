using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Widgets;

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
/// <see cref="StatusViewModel"/> segments on one row.
/// </summary>
public sealed class StatusPanel : Panel
{
    private readonly StatusSeg[] _compose = new StatusSeg[12];
    private byte _lastMode;
    private bool _modeSeen;
    private long _modeFlipTick = long.MinValue;

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

        Span<StatusSeg> span = _compose;
        int kept = StatusBarLayout.Fit(span[..total], Rect.Width - 1);
        StatusBarWidget.Paint(buffer, new Rect(Rect.X, Rect.Y, Rect.Width, 1), span[..kept]);

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
}

/// <summary>Assembled chat screen: timeline above, composer below, status footer.</summary>
public sealed record ChatScreen(LayoutTree Tree, ChatTimelinePanel Timeline, ComposerPanel Composer, StatusPanel Status)
{
    public const string TimelineId = "chat.timeline";
    public const string ComposerId = "chat.composer";
    public const string StatusId = "chat.status";

    public static ChatScreen Build(
        Rendering.ComposerController composer,
        StatusViewModel status,
        float timelineRatio = 0.82f,
        int minComposerRows = 3)
    {
        var tree = new LayoutTree();
        var timeline = new ChatTimelinePanel(TimelineId, minWidth: 20, minHeight: 4, priority: 10);
        var composerPanel = new ComposerPanel(ComposerId, composer, minWidth: 10, minHeight: minComposerRows, priority: 50);
        var statusRow = new StatusPanel(StatusId, status, minWidth: 10, minHeight: 1, priority: int.MaxValue);
        timeline.Timeline.EnableEntranceFx();

        tree.AddRoot(timeline);
        tree.Split(TimelineId, SplitDir.Vertical, timelineRatio, composerPanel, gap: 0);
        tree.Split(ComposerId, SplitDir.Vertical, ratio: 1f - (1f / Math.Max(2, minComposerRows)), statusRow, gap: 0);

        return new ChatScreen(tree, timeline, composerPanel, statusRow);
    }
}
