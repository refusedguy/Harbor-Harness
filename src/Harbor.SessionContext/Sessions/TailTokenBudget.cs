// TailTokenBudget.cs - #2680 F054. Pure policy for the number of recent tokens
// a compaction preserves verbatim. BCL-only leaf: no state, no clock, no Harbor
// imports and no product wiring. It follows the F115/F125 slice shape: the
// parse/policy half of the item in isolation, with no config or service change.
//
// The upstream rule this ports (docs `context-condensing.md`, defaults table;
// `compaction-chunks.ts:152 budget()`) reserves 25% of the usable context for
// the tail, then clamps that figure into [2,000 .. 8,000] tokens. Keeping the
// arithmetic here as a pure function means the same bound can be applied by a
// config default, a validator or a compaction caller without any of them
// owning the numbers, and without the constant drifting between them.

namespace Harbor.Application.Sessions;

/// <summary>
///     Bounds the preserved recent-tail budget of a compaction (#2680 F054).
///     The derived share of the usable context is clamped into
///     [<see cref="MinTokens" />, <see cref="MaxTokens" />]. Pure and stateless:
///     identical inputs always produce the same result, with no culture- or
///     platform-dependent behaviour.
/// </summary>
public static class TailTokenBudget
{
    /// <summary>Smallest preserved tail, in tokens.</summary>
    public const int MinTokens = 2_000;

    /// <summary>Largest preserved tail, in tokens.</summary>
    public const int MaxTokens = 8_000;

    /// <summary>Share of the usable context reserved for the tail, in percent.</summary>
    public const int UsablePercent = 25;

    /// <summary>
    ///     Derives the preserved-tail budget from a usable-token count: 25% of
    ///     <paramref name="usableTokens" />, clamped into
    ///     [<see cref="MinTokens" />, <see cref="MaxTokens" />]. A non-positive
    ///     input yields <see cref="MinTokens" />. The percentage is computed in
    ///     <see cref="long" /> arithmetic so even <see cref="int.MaxValue" />
    ///     cannot overflow before the upper clamp.
    /// </summary>
    /// <param name="usableTokens">Tokens available for the compacted view.</param>
    /// <returns>The clamped tail budget, in tokens.</returns>
    public static int FromUsableTokens(int usableTokens)
    {
        if (usableTokens <= 0)
        {
            return MinTokens;
        }

        long share = (long)usableTokens * UsablePercent / 100;
        if (share < MinTokens)
        {
            return MinTokens;
        }

        if (share > MaxTokens)
        {
            return MaxTokens;
        }

        return (int)share;
    }

    /// <summary>
    ///     Clamps an explicit budget request into
    ///     [<see cref="MinTokens" />, <see cref="MaxTokens" />].
    /// </summary>
    /// <param name="requested">The requested tail budget, in tokens.</param>
    /// <returns><paramref name="requested" /> narrowed to the allowed range.</returns>
    public static int Clamp(int requested)
    {
        if (requested < MinTokens)
        {
            return MinTokens;
        }

        if (requested > MaxTokens)
        {
            return MaxTokens;
        }

        return requested;
    }
}
