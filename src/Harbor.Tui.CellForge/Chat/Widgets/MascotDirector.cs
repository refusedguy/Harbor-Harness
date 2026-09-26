using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Tui.CellForge.Widgets;

public sealed class MascotDirector
{
    public const int MoodLatchFrames = 150;

    private const byte NoMood = 0xFF;

    private long _lastActiveMs = Environment.TickCount64;
    private byte _mood = NoMood;
    private long _moodFlipTick = long.MinValue;
    private byte _latched = NoMood;
    private long _latchEndTick;
    private byte _lastPhase;
    private readonly SpringFx _crossfadeSpring = new(1.0);

    public MascotMood Advance(StatusViewModel vm, long tick)
    {
        if (MascotModeEnv.Value == MascotMode.Off)
        {
            return MascotMood.Idle;
        }

        byte phase = (byte)vm.Phase;
        bool eventPhase = phase is (byte)AgentPhase.Errored or (byte)AgentPhase.Succeeded;
        if (eventPhase && _lastPhase != phase)
        {
            _latched = phase == (byte)AgentPhase.Errored ? (byte)MascotMood.Error : (byte)MascotMood.Success;
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
            _lastActiveMs = Environment.TickCount64;
        }

        return mood;
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
        _ => Environment.TickCount64 - _lastActiveMs > StatusPanel.MascotSleepAfterMs
            ? MascotMood.Sleeping
            : MascotMood.Idle,
    };
}
