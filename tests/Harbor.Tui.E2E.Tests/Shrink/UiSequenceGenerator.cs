namespace Harbor.Tui.E2E.Tests.Shrink;

/// <summary>
///     Builds the UI-action sequences the shrinker works on. The default path
///     returns the fixed cancel-approve-commit list; randomized sequences are
///     opt-in and fully determined by their seed.
/// </summary>
public static class UiSequenceGenerator
{
    /// <summary>
    ///     Version of the generator that produced a sequence. Recorded on every
    ///     failure artifact: the seed alone is insufficient to reproduce a run.
    /// </summary>
    public const string GeneratorVersion = "1.0.0";

    /// <summary>
    ///     The default path: the fixed cancel-approve-commit list.
    /// </summary>
    public static IReadOnlyList<UiSequenceAction> GenerateDefault() => UiSequence.DefaultFixedOrder;

    /// <summary>
    ///     Opt-in randomized sequences. The same seed always yields the same
    ///     sequence; different seeds may yield different ones.
    /// </summary>
    public static IReadOnlyList<UiSequenceAction> GenerateRandomized(int seed, int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        UiSequenceAction[] values = (UiSequenceAction[])Enum.GetValues(typeof(UiSequenceAction));
        var sequence = new UiSequenceAction[count];
        var rng = new Random(seed);
        for (int i = 0; i < sequence.Length; i++)
        {
            sequence[i] = values[rng.Next(values.Length)];
        }

        return sequence;
    }
}
