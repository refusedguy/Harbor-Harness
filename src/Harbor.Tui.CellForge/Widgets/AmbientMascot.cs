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

    // ── Panel-mode rows (mascot-brand T2) ──────────────────────────────────
    // The panel cat is 3 rows: ears / face / paws. The face row IS the footer
    // frame — one source of face art — and ear/paw rows are indexed in
    // lockstep via FrameIndex. Every row is exactly 8 cells wide.

    /// <summary>Row count of the panel-mode mascot.</summary>
    public const int PanelRows = 3;

    /// <summary>Minimum panel width — the 8-cell art plus one pad column.</summary>
    public const int PanelMinWidth = 9;

    private static readonly string[] EarsUp = [" /\\_/\\  ", " /\\_/\\  ", " /\\_/\\  ", " /\\_/\\  "];
    private static readonly string[] EarsFlat = [" \\___/  ", " \\___/  ", " \\___/  ", " \\___/  "];
    private static readonly string[] PawsLoaf = [" (____) ", " (____) ", " (____) ", " (____) "];
    private static readonly string[] PawsKnead = [" d  b   ", "  d  b  ", " d  b   ", "  d  b  "];
    private static readonly string[] PawsReach = [" \\    / ", " (____) ", " \\    / ", " (____) "];
    private static readonly string[] PawsWag = ["   /|   ", "  \\|    ", "   /|   ", "  \\|    "];

    /// <summary>Ear row for the mood — flat when the cat sulks.</summary>
    public static string[] PanelEars(MascotMood mood) => mood == MascotMood.Error ? EarsFlat : EarsUp;

    /// <summary>Paw row for the mood — loaf / knead / reach / tail-wag.</summary>
    public static string[] PanelPaws(MascotMood mood) => mood switch
    {
        MascotMood.Working or MascotMood.ToolCall => PawsKnead,
        MascotMood.Awaiting => PawsReach,
        MascotMood.Success => PawsWag,
        _ => PawsLoaf,
    };

    // ── Event reactions (mascot-brand T3) ──────────────────────────────────
    // Short overlay sequences: the reaction overrides the mood frames for a
    // few ticks, then the mood resumes. Faces are 8 cells; panel rows match.

    /// <summary>Error blink — X-eyes flash, close, flash (3 frames).</summary>
    public static readonly string[] ErrorBlinkFrames = ["( X..X )", "( -..- )", "( X..X )"];

    /// <summary>Success bounce — happy squint pop (3 frames).</summary>
    public static readonly string[] SuccessBounceFrames = ["( ^..^ )", "( ^w^  )", "( ^..^ )"];

    /// <summary>Approval wiggle — wide eyes asking (3 frames).</summary>
    public static readonly string[] ApprovalWiggleFrames = ["( O..O )", "( o..o )", "( O..O )"];

    /// <summary>Face bank for a reaction.</summary>
    public static string[] ReactionFramesOf(MascotReaction reaction) => reaction switch
    {
        MascotReaction.SuccessBounce => SuccessBounceFrames,
        MascotReaction.ApprovalWiggle => ApprovalWiggleFrames,
        _ => ErrorBlinkFrames,
    };

    private static readonly string[] ReactionEarsFlat = [" \\___/  ", " /\\_/\\  ", " \\___/  "];
    private static readonly string[] ReactionEarsUp = [" /\\_/\\  ", " /\\_/\\  ", " /\\_/\\  "];
    private static readonly string[] ReactionPawsLoaf = [" (____) ", " (____) ", " (____) "];
    private static readonly string[] ReactionPawsHop = [" (____) ", "  (__)  ", " (____) "];
    private static readonly string[] ReactionPawsWave = [" \\    / ", " /    \\ ", " \\    / "];

    /// <summary>Panel ear row for a reaction — ears flatten on the blink.</summary>
    public static string[] ReactionEars(MascotReaction reaction) => reaction == MascotReaction.ErrorBlink
        ? ReactionEarsFlat
        : ReactionEarsUp;

    /// <summary>Panel paw row for a reaction — hop on the bounce, wave on the wiggle.</summary>
    public static string[] ReactionPaws(MascotReaction reaction) => reaction switch
    {
        MascotReaction.SuccessBounce => ReactionPawsHop,
        MascotReaction.ApprovalWiggle => ReactionPawsWave,
        _ => ReactionPawsLoaf,
    };
}
