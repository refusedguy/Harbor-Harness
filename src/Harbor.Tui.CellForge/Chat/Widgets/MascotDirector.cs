using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Tui.CellForge.Widgets;

public sealed class MascotDirector
{
    /// <summary>
    /// Legacy tick alias: 150 frames at the 80 ms heartbeat ≈ 12 s.
    /// Kept for compatibility (tests, warm-up counts); the latch itself
    /// expires by wall-clock (<see cref="MoodLatchMs"/>), not by ticks (#170).
    /// </summary>
    public const int MoodLatchFrames = 150;

    /// <summary>
    /// Wall-clock mood latch: 150 frames × 80 ms heartbeat ≈ 12 s. A tick
    /// counter froze forever in Idle (no repaints → no ticks); wall-clock
    /// expiry revives the mascot once frames flow again (#170).
    /// </summary>
    public const int MoodLatchMs = 12_000;

    private const byte NoMood = 0xFF;

    private readonly int _moodLatchMs;
    private readonly TimeProvider _time;
    private readonly double _msPerTimestampTick;
    private long _lastActiveMs;
    private byte _mood = NoMood;
    private long _moodFlipTick = long.MinValue;
    private byte _latched = NoMood;
    private long _latchEndMs;
    private long _latchEndTick;
    private byte _lastPhase;
    private readonly SpringFx _crossfadeSpring = new(1.0);

    /// <summary>Creates a director with an optional latch-lifetime override.</summary>
    /// <param name="moodLatchMs">Latch lifetime override (tests inject milliseconds).</param>
    /// <param name="timeProvider">
    /// Clock override (#1007). Production reads <see cref="TimeProvider.System"/>;
    /// tests inject a manual clock, so a latch assertion is a function of the
    /// elapsed time the test declares and not of how fast the runner is. The
    /// shape <c>RetryPolicy</c> already uses for its backoff (#54) — the latch
    /// is wall-clock by design (#170), so the clock has to be a seam, not a
    /// constant the test shrinks until the machine can hit it.
    /// </param>
    public MascotDirector(int moodLatchMs = MoodLatchMs, TimeProvider? timeProvider = null)
    {
        _moodLatchMs = moodLatchMs;
        _time = timeProvider ?? TimeProvider.System;

        // Scale through double, never `GetTimestamp() * 1000`: a
        // nanosecond-resolution TimeProvider (Stopwatch.Frequency is 1e9 on
        // Linux) overflows long after ~106 days of uptime. Dividing first
        // keeps the headroom at ~292 years, and the sub-millisecond precision
        // that gets dropped falls below the double's 53-bit mantissa rather
        // than below the 12 s latch.
        _msPerTimestampTick = 1_000.0 / _time.TimestampFrequency;
        _lastActiveMs = NowMs();
    }

    /// <summary>
    /// Milliseconds on the injected clock, measured from this director's own
    /// construction so the value stays small whatever the clock's epoch is.
    /// The only clock read in this class — every ms field below is derived
    /// from it (#1007).
    /// </summary>
    private long NowMs() => (long)(_time.GetTimestamp() * _msPerTimestampTick);

    public MascotMood Advance(StatusViewModel vm, long tick)
    {
        if (MascotModeEnv.Value == MascotMode.Off)
        {
            return MascotMood.Idle;
        }

        long now = NowMs();
        byte phase = (byte)vm.Phase;
        bool eventPhase = phase is (byte)AgentPhase.Errored or (byte)AgentPhase.Succeeded;
        if (eventPhase && _lastPhase != phase)
        {
            _latched = phase == (byte)AgentPhase.Errored ? (byte)MascotMood.Error : (byte)MascotMood.Success;
            _latchEndMs = now + _moodLatchMs;
            _latchEndTick = tick + MoodLatchFrames;
        }
        else if (eventPhase && _latched != NoMood && tick >= _latchEndTick)
        {
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
            _crossfadeSpring.SnapTo(0.0);
            _crossfadeSpring.Retarget(1.0);
        }

        if (vm.Mode != StatusBarMode.Idle)
        {
            // The same read as `now` above: two reads inside one Advance could
            // straddle a latch boundary and disagree about this frame (#1007).
            _lastActiveMs = now;
        }

        return mood;
    }

    /// <summary>
    /// True while the mascot still owes the user motion: a one-shot reaction
    /// is armed or the mood latch is live by wall-clock (#170). The frame loop
    /// keeps its 80 ms heartbeat while this holds so reactions play out and
    /// the latch can expire in Idle; it goes quiet by itself afterwards.
    /// False when the mascot is off. Allocation-free.
    /// </summary>
    public bool HasActiveAnimation
    {
        get
        {
            if (MascotModeEnv.Value == MascotMode.Off)
            {
                return false;
            }

            if (_reaction != 0)
            {
                return true;
            }

            return _latched != NoMood && NowMs() < _latchEndMs;
        }
    }

    public bool BlendMoodCrossfade(ScreenBuffer buffer, Rect region, long tick)
    {
        long flip = _moodFlipTick;
        if (flip == long.MinValue || tick <= flip)
        {
            return false;
        }

        double ramp = _crossfadeSpring.Step();
        if (ramp >= 1.0)
        {
            _moodFlipTick = long.MinValue;
            return false;
        }

        PanelFx.BlendRegion(buffer, region, Math.Clamp(ramp, 0.0, 1.0));
        return true;
    }

    public const int ReactionFrameTicks = 3;
    public const int ReactionFrames = 3;

    private int _reaction;
    private long _reactionStartTick;

    public void Notify(MascotReaction reaction, long tick)
    {
        if (reaction == MascotReaction.None)
        {
            return;
        }

        _reaction = (int)reaction;
        _reactionStartTick = tick;
    }

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
        _ => NowMs() - _lastActiveMs > StatusPanel.MascotSleepAfterMs
            ? MascotMood.Sleeping
            : MascotMood.Idle,
    };
}
