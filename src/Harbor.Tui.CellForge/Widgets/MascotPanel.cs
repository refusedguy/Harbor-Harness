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

        // Panel mode: the panel owns the one-shot event signal.
        MascotReaction signal = Vm.ConsumeMascotSignal();
        if (signal != MascotReaction.None)
        {
            _director.Notify(signal, Tick);
        }

        MascotMood mood = _director.Advance(Vm, Tick);
        string ears, face, paws;
        var style = ChatPalette.Dim;
        if (_director.TryReactionFrame(Tick, out MascotReaction active, out int ridx))
        {
            face = AmbientMascot.ReactionFramesOf(active)[ridx];
            ears = AmbientMascot.ReactionEars(active)[ridx];
            paws = AmbientMascot.ReactionPaws(active)[ridx];
            style = MascotDirector.ReactionStyle(active);
        }
        else
        {
            int idx = AmbientMascot.FrameIndex(Tick, mood);
            face = AmbientMascot.Frame(Tick, mood);
            ears = AmbientMascot.PanelEars(mood)[idx];
            paws = AmbientMascot.PanelPaws(mood)[idx];
        }

        buffer.SetText(Rect.X, Rect.Y, ears, style);
        buffer.SetText(Rect.X, Rect.Y + 1, face, style);
        if (Rect.Height > 2)
        {
            buffer.SetText(Rect.X, Rect.Y + 2, paws, style);
        }

        // Reaction overlay wins over the mood crossfade while it owns the cat.
        if (!_director.BlendReaction(buffer, Rect, Tick))
        {
            _ = _director.BlendMoodCrossfade(buffer, Rect, Tick);
        }
    }
}
