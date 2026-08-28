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

    /// <summary>Sleeping advances once per this many ticks (slow breath).</summary>
    public const int SleepPeriod = 8;

    /// <summary>Frame for the given monotonic tick and mood. Deterministic.</summary>
    public static string Frame(long monotonicTick, MascotMood mood = MascotMood.Idle) => mood switch
    {
        MascotMood.Working => WorkingFrames[IndexOf(WorkingFrames, monotonicTick)],
        MascotMood.Awaiting => AwaitingFrames[IndexOf(AwaitingFrames, monotonicTick)],
        MascotMood.Sleeping => SleepingFrames[IndexOf(SleepingFrames, monotonicTick / SleepPeriod)],
        _ => IdleFrames[IndexOf(IdleFrames, monotonicTick)],
    };

    /// <summary>Display width of the frame (all frames are single-width runes).</summary>
    public static int Width(string frame) => frame.Length;

    private static int IndexOf(string[] frames, long tick) =>
        (int)(tick % frames.Length) is int i && i >= 0 ? i : i + frames.Length;
}
