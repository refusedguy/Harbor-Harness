using System.Diagnostics.CodeAnalysis;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>Animation rhythm: fast wave while working, slow pulse while awaiting the user.</summary>
public enum SpinnerRhythm : byte
{
    /// <summary>Advances every tick — busy wave (braille dots).</summary>
    Working = 0,

    /// <summary>Advances every <see cref="PulsePeriod"/> ticks — calm pulse for «ждёт тебя».</summary>
    Awaiting = 1,
}

/// <summary>
/// Tick-driven spinner (widgets §3.8): frames come from the render pipeline's
/// monotonic tick, never a timer. Two frame sets + two rates distinguish
/// «работает» from «ждёт твоего решения» (grok is_pending_user_input pulse).
/// Pure function of (tick, rhythm) → frame slice; zero allocations.
/// </summary>
public static class SpinnerStrip
{
    /// <summary>Braille dot wave — the working animation.</summary>
    public static readonly string[] WorkingFrames =
    [
        "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏",
    ];

    /// <summary>Pulsing ring — the awaiting-user animation.</summary>
    public static readonly string[] AwaitingFrames =
    [
        "◐", "◓", "◑", "◒",
    ];

    /// <summary>Awaiting rhythm advances once per this many ticks.</summary>
    public const int PulsePeriod = 4;

    /// <summary>ASCII fallback for terminals without braille/geometric glyphs.</summary>
    public static readonly string[] AsciiWorkingFrames = ["|", "/", "-", "\\"];

    /// <summary>Frame for the given monotonic tick and rhythm. Deterministic.</summary>
    [SuppressMessage("Performance", "MA0011", Justification = "frame arrays are tiny constants; indexer cost is nil")]
    public static ReadOnlySpan<char> Frame(long monotonicTick, SpinnerRhythm rhythm = SpinnerRhythm.Working)
    {
        return rhythm switch
        {
            SpinnerRhythm.Awaiting => AwaitingFrames[(int)((monotonicTick / PulsePeriod) % AwaitingFrames.Length)],
            _ => WorkingFrames[(int)(monotonicTick % WorkingFrames.Length)],
        };
    }

    /// <summary>Same frame as <see cref="Frame"/> but as the backing static string — zero allocations.</summary>
    public static string FrameString(long monotonicTick, SpinnerRhythm rhythm = SpinnerRhythm.Working) => rhythm switch
    {
        SpinnerRhythm.Awaiting => AwaitingFrames[(int)((monotonicTick / PulsePeriod) % AwaitingFrames.Length)],
        _ => WorkingFrames[(int)(monotonicTick % WorkingFrames.Length)],
    };

    /// <summary>Narrow-terminal fallback set selection.</summary>
    public static ReadOnlySpan<char> AsciiFrame(long monotonicTick) =>
        AsciiWorkingFrames[(int)(monotonicTick % AsciiWorkingFrames.Length)];
}
