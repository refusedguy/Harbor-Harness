using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Panel-mode mascot (sprint mascot-brand T2): a 3-row cat — ears / face /
/// paws — painted beside the composer via <c>LayoutTree.Split</c>. The face
/// row is the footer <see cref="AmbientMascot.Frame" />, so the moods stay in
/// lockstep across modes; mood derivation, event-mood latching and the
/// accent-ramp crossfade come from the shared <see cref="MascotDirector" />.
/// Zero allocations: static row banks, span writes, tick-driven like
/// <see cref="StatusPanel" />.
/// </summary>
public sealed class MascotPanel : Panel
{
    public const string DefaultId = "chat.mascot";

    private readonly MascotDirector _director = new();

    public MascotPanel(string id, StatusViewModel status, int minWidth = AmbientMascot.PanelMinWidth, int minHeight = AmbientMascot.PanelRows, int priority = 4)
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
        if (Rect.Width <= 0 || Rect.Height <= 0)
        {
            return;
        }

        MascotMood mood = _director.Advance(Vm, Tick);
        int idx = AmbientMascot.FrameIndex(Tick, mood);
        string face = AmbientMascot.Frame(Tick, mood);
        string ears = AmbientMascot.PanelEars(mood)[idx];
        string paws = AmbientMascot.PanelPaws(mood)[idx];

        buffer.SetText(Rect.X, Rect.Y, ears, ChatPalette.Dim);
        buffer.SetText(Rect.X, Rect.Y + 1, face, ChatPalette.Dim);
        if (Rect.Height > 2)
        {
            buffer.SetText(Rect.X, Rect.Y + 2, paws, ChatPalette.Dim);
        }

        _ = _director.BlendMoodCrossfade(buffer, Rect, Tick);
    }
}
