using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Mood brain shared by the footer mascot and the panel-mode mascot
/// (sprint mascot-brand T1): derives the mood from the coarse
/// <see cref="StatusViewModel.Mode" /> plus the fine <see cref="AgentPhase" />,
/// latches event moods (error / success) for a bounded window, and crossfades
/// mood flips via <see cref="PanelFx.AccentRamp" />. Deterministic in tick,
/// zero allocations on the render path — the same contract as
/// <see cref="SpinnerStrip" />.
/// </summary>
public sealed class MascotDirector
{
    /// <summary>How long an event mood (error / success) stays latched before
    /// the derived mood resumes — 150 frames ≈ 2.5 s at the 60 fps cadence.</summary>
    public const int MoodLatchFrames = 150;

    private const byte NoMood = 0xFF;

    private long _lastActiveMs = Environment.TickCount64;
    private byte _mood = NoMood;
    private long _moodFlipTick = long.MinValue;
    private byte _latched = NoMood;
    private long _latchEndTick;
    private byte _lastPhase;

    /// <summary>
    /// Advances the mood for <paramref name="tick" /> and records flips for
    /// the crossfade. Call once per paint; the returned mood feeds the frame banks.
    ///
    /// Event moods (error / success) latch on the <em>edge</em> when
    /// <see cref="AgentPhase" /> enters Errored/Succeeded and hold for
    /// <see cref="MoodLatchFrames" /> even while the phase persists — the
    /// mascot sulks for a moment, then goes back to its derived mood.
    /// </summary>
    public MascotMood Advance(StatusViewModel vm, long tick)
    {
        byte phase = (byte)vm.Phase;
        bool eventPhase = phase is (byte)AgentPhase.Errored or (byte)AgentPhase.Succeeded;
        if (eventPhase && _lastPhase != phase)
        {
            // Edge: arm (or re-arm) the event-mood latch.
            _latched = phase == (byte)AgentPhase.Errored ? (byte)MascotMood.Error : (byte)MascotMood.Success;
            _latchEndTick = tick + MoodLatchFrames;
        }
        else if (eventPhase && _latched != NoMood && tick >= _latchEndTick)
        {
            // Latch window elapsed even though the phase persists.
            _latched = NoMood;
        }

        _lastPhase = phase;

        MascotMood mood = _latched != NoMood ? (MascotMood)_latched : Derive(vm);

        if (_mood == NoMood)
        {
            _mood = (byte)mood;
        }
        else if (_mood != (byte)mood)
        {
            _mood = (byte)mood;
            _moodFlipTick = tick;
        }

        if (vm.Mode != StatusBarMode.Idle)
        {
            _lastActiveMs = Environment.TickCount64;
        }

        return mood;
    }

    /// <summary>
    /// Applies the mood-flip crossfade to <paramref name="region" /> — the
    /// mascot cells ease in from the panel surface over the HDS micro fade.
    /// Returns false when settled (no blend work this frame).
    /// </summary>
    public bool BlendMoodCrossfade(ScreenBuffer buffer, Rect region, long tick)
    {
        long flip = _moodFlipTick;
        if (flip == long.MinValue)
        {
            return false;
        }

        double ramp = PanelFx.AccentRamp(flip, tick);
        if (ramp >= 1.0)
        {
            _moodFlipTick = long.MinValue;
            return false;
        }

        PanelFx.BlendRegion(buffer, region, ramp);
        return true;
    }

    private MascotMood Derive(StatusViewModel vm) => vm.Mode switch
    {
        StatusBarMode.Running => vm.Phase switch
        {
            AgentPhase.Thinking => MascotMood.Thinking,
            AgentPhase.ToolCall => MascotMood.ToolCall,
            _ => MascotMood.Working,
        },
        StatusBarMode.Compacting => MascotMood.Working,
        StatusBarMode.AwaitingApproval => MascotMood.Awaiting,
        _ => Environment.TickCount64 - _lastActiveMs > StatusPanel.MascotSleepAfterMs
            ? MascotMood.Sleeping
            : MascotMood.Idle,
    };
}
