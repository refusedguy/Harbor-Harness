namespace Harbor.Tui.CellForge.Widgets;

/// <summary>Mascot mood — the host derives it from agent state (SpinnerStrip rhythm, approval gate, …).</summary>
public enum MascotMood : byte
{
    /// <summary>Calm blink — agent idle.</summary>
    Idle = 0,

    /// <summary>Bounce — agent working.</summary>
    Working = 1,

    /// <summary>Wide eyes — awaiting approval.</summary>
    Awaiting = 2,

    /// <summary>Sleep — session untouched for a long while.</summary>
    Sleeping = 3,

    /// <summary>Brow sway — the LLM is streaming text (mascot-brand T1).</summary>
    Thinking = 4,

    /// <summary>Tail flick — a tool is executing.</summary>
    ToolCall = 5,

    /// <summary>Flat X stare — the last run failed.</summary>
    Error = 6,

    /// <summary>Purr whisker wiggle — the last run finished clean.</summary>
    Success = 7,
}

/// <summary>
/// Ambient mascot (Petdex-style, killer features §P6.1): a tick-driven one-cell-row
/// cat that lives in the prompt footer and mirrors agent state. Frames are
/// static single-width ASCII — zero allocations, deterministic like
/// <see cref="SpinnerStrip" />; the host passes the mood, we only render.
/// </summary>
public static class AmbientMascot
{
    /// <summary>Idle blink cycle — eyes open, closed, open.</summary>
    public static readonly string[] IdleFrames = ["( ^..^ )", "( -..- )", "( ^..^ )", "( -..- )"];

    /// <summary>Working bounce — tail flicks, ears perk.</summary>
    public static readonly string[] WorkingFrames = ["( ^..^)/", "( >..^ )", "( ^..^')", "( ^>.. )"];

    /// <summary>Awaiting approval — wide eyes, paws up.</summary>
    public static readonly string[] AwaitingFrames = ["( O..O )", "( o..o )"];

    /// <summary>Sleeping — slow breath, drifting zzz (constant 8-cell width).</summary>
    public static readonly string[] SleepingFrames = ["( -.-  )", "( -.- z)", "( -.-  )", "( z-.- )"];

    /// <summary>Thinking — brows sway while the LLM streams (constant 8-cell width).</summary>
    public static readonly string[] ThinkingFrames = ["( ^..^ )", "( ~..^ )", "( ^..^ )", "( ^..~ )"];

    /// <summary>Tool call — tail flicks left/right while the paws are busy (constant 8-cell width).</summary>
    public static readonly string[] ToolCallFrames = ["( ^..^)|", "( ^..^ )", "(|^..^ )", "( ^..^ )"];

    /// <summary>Error — flat X stare, then a squeeze (constant 8-cell width).</summary>
    public static readonly string[] ErrorFrames = ["( X..X )", "( >..< )"];

    /// <summary>Success — purr whisker wiggle (constant 8-cell width).</summary>
    public static readonly string[] SuccessFrames = ["(=^w^= )", "( =^w^=)"];

    /// <summary>Sleeping advances once per this many ticks (slow breath).</summary>
    public const int SleepPeriod = 8;

    /// <summary>Frame bank for a mood — static arrays, never copied.</summary>
    public static string[] FramesOf(MascotMood mood) => mood switch
    {
        MascotMood.Working => WorkingFrames,
        MascotMood.Awaiting => AwaitingFrames,
        MascotMood.Sleeping => SleepingFrames,
        MascotMood.Thinking => ThinkingFrames,
        MascotMood.ToolCall => ToolCallFrames,
        MascotMood.Error => ErrorFrames,
        MascotMood.Success => SuccessFrames,
        _ => IdleFrames,
    };

    /// <summary>
    /// Index into the mood's frame bank for the given monotonic tick — the
    /// panel-mode renderer uses it to keep ear/paw rows in lockstep with the
    /// face row. Deterministic; negative ticks wrap.
    /// </summary>
    public static int FrameIndex(long monotonicTick, MascotMood mood)
    {
        string[] frames = FramesOf(mood);
        long period = mood == MascotMood.Sleeping ? SleepPeriod : 1;
        int i = (int)((monotonicTick / period) % frames.Length);
        return i >= 0 ? i : i + frames.Length;
    }

    /// <summary>Frame for the given monotonic tick and mood. Deterministic.</summary>
    public static string Frame(long monotonicTick, MascotMood mood = MascotMood.Idle) =>
        FramesOf(mood)[FrameIndex(monotonicTick, mood)];

    /// <summary>Display width of the frame (all frames are single-width runes).</summary>
    public static int Width(string frame) => frame.Length;
}
