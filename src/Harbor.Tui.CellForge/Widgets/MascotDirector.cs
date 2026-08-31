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
    /// mascot cells dip toward the panel surface and ease back over the HDS
    /// micro fade (<see cref="PanelFx.AccentRamp" />). The detection frame
    /// itself paints the new mood settled (<c>Progress</c> resolves to 1 for
    /// <c>nowTick ≤ startTick</c>); blending runs on the frames after it.
    /// Returns false when settled (no blend work this frame).
    /// </summary>
    public bool BlendMoodCrossfade(ScreenBuffer buffer, Rect region, long tick)
    {
        long flip = _moodFlipTick;
        if (flip == long.MinValue || tick <= flip)
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

    // ── Event reactions (mascot-brand T3) ──────────────────────────────────

    /// <summary>Ticks each reaction frame stays on screen — 3 frames × 3 ticks ≈ 150 ms.</summary>
    public const int ReactionFrameTicks = 3;

    /// <summary>Frames in every reaction sequence (blink / bounce / wiggle).</summary>
    public const int ReactionFrames = 3;

    private int _reaction;
    private long _reactionStartTick;

    /// <summary>Arms a per-event overlay — the reaction frames override the
    /// mood frames for the sequence duration. Latest notification wins.</summary>
    public void Notify(MascotReaction reaction, long tick)
    {
        if (reaction == MascotReaction.None)
        {
            return;
        }

        _reaction = (int)reaction;
        _reactionStartTick = tick;
    }

    /// <summary>
    /// True while an event overlay is playing; <paramref name="frameIndex" />
    /// walks [0..<see cref="ReactionFrames" />), each frame held for
    /// <see cref="ReactionFrameTicks" /> ticks. Expiry clears the overlay —
    /// the mood resumes without a fresh notification.
    /// </summary>
    public bool TryReactionFrame(long tick, out MascotReaction reaction, out int frameIndex)
    {
        int armed = _reaction;
        if (armed == 0)
        {
            reaction = MascotReaction.None;
            frameIndex = 0;
            return false;
        }

        int idx = (int)((tick - _reactionStartTick) / ReactionFrameTicks);
        if (idx < 0 || idx >= ReactionFrames)
        {
            _reaction = 0;
            reaction = MascotReaction.None;
            frameIndex = 0;
            return false;
        }

        reaction = (MascotReaction)armed;
        frameIndex = idx;
        return true;
    }

    /// <summary>
    /// Blends the reaction region with the accent ramp — same detection-frame
    /// semantics as the mood crossfade: the notify frame paints settled, the
    /// frames after it dip and ease back. Returns true while the overlay owns
    /// the region (mood crossfades stay suppressed meanwhile).
    /// </summary>
    public bool BlendReaction(ScreenBuffer buffer, Rect region, long tick)
    {
        int armed = _reaction;
        if (armed == 0)
        {
            return false;
        }

        long start = _reactionStartTick;
        if (tick > start)
        {
            double ramp = PanelFx.AccentRamp(start, tick);
            if (ramp < 1.0)
            {
                PanelFx.BlendRegion(buffer, region, ramp);
            }
        }

        return true;
    }

    /// <summary>Event tint: the blink burns red, the bounce glows green, the wiggle warns.</summary>
    public static CellStyle ReactionStyle(MascotReaction reaction) => reaction switch
    {
        MascotReaction.ErrorBlink => ChatPalette.ToolError,
        MascotReaction.SuccessBounce => ChatPalette.ToolOk,
        _ => ChatPalette.ToolRunning,
    };

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
