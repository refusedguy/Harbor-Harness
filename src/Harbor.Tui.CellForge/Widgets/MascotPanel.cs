using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Tui.CellForge.Widgets;

public sealed class MascotPanel : Panel
{
    public const string DefaultId = "chat.mascot";

    private readonly MascotDirector _director = new();
    private readonly SpringFx _entranceSpring = new(0.0);
    private readonly PostFxPipeline _postFx = new();
    private readonly GlowEffect _glow = new();
    private bool _entranceArmed;

    public MascotPanel(string id, StatusViewModel status, int minWidth = AmbientMascot.PanelMinWidth, int minHeight = AmbientMascot.PanelRows, int priority = 4)
        : base(id, new Size(minWidth, minHeight), priority)
    {
        Vm = status;
    }

    public StatusViewModel Vm { get; }

    public long Tick { get; private set; }

    public override void Paint(ScreenBuffer buffer)
    {
        Tick++;

        if (!_entranceArmed)
        {
            _entranceArmed = true;
            _entranceSpring.SnapTo(0.0);
            _entranceSpring.Retarget(1.0);
        }

        if (Rect.Width <= 0 || Rect.Height <= 0)
        {
            return;
        }

        double entrance = _entranceSpring.Step();
        if (entrance < 1.0)
        {
            PanelFx.BlendRegion(buffer, Rect, Math.Clamp(entrance, 0.0, 1.0));
        }

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

        if (!_director.BlendReaction(buffer, Rect, Tick))
        {
            _ = _director.BlendMoodCrossfade(buffer, Rect, Tick);
        }

        if (entrance >= 1.0)
        {
            ApplyPostFx(buffer);
        }
    }

    private void ApplyPostFx(ScreenBuffer buffer)
    {
        _postFx.Clear();
        if (_director.TryReactionFrame(Tick, out MascotReaction active, out _))
        {
            var accent = active switch
            {
                MascotReaction.ErrorBlink => ChatPalette.Error,
                MascotReaction.SuccessBounce => ChatPalette.Success,
                _ => ChatPalette.Warning,
            };
            _glow.Update(new GlowRegion(Rect, accent, GlowEffect.PeakStrength));
            _postFx.Set(0, _glow);
        }

        if (_postFx.Count == 0)
        {
            return;
        }

        for (int y = Rect.Y; y < Rect.Bottom; y++)
        {
            for (int x = Rect.X; x < Rect.Right; x++)
            {
                var cell = buffer.Get(x, y);
                if (cell.Width == Cell.WSkip)
                {
                    continue;
                }

                var transformed = _postFx.Transform(x, y, in cell);
                if (transformed.Fg != cell.Fg || transformed.Bg != cell.Bg)
                {
                    buffer.SetStyleAt(x, y, transformed.Style);
                }
            }
        }
    }
}
