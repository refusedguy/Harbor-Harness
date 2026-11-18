using System.Globalization;

namespace Harbor.TestKit;

/// <summary>
///     One source of randomness for every stochastic test in the tree. A
///     failing fuzz run must be reproducible from a single value, so the same
///     environment variable drives the diff-engine fuzz, the IPC frame fuzz,
///     and any future property/data generator.
/// </summary>
/// <remarks>
///     <para>
///         <b>Contract.</b> <see cref="Values" /> returns the caller's defaults
///         untouched when no override is set. <c>HARBOR_TEST_SEED</c> replaces
///         the defaults with one seed (the caller prints it on failure), and
///         <c>HARBOR_TEST_ITERATIONS</c> expands a run to that many consecutive
///         seeds starting at the override, or at the first default when only
///         the count is given. When both are set the seed is the base and the
///         count is the number of seeds.
///     </para>
///     <para>
///         <b>Why consecutive seeds and not random ones.</b> The point is
///         replay, not coverage: <c>HARBOR_TEST_SEED=99</c> must always select
///         exactly seed 99. A larger iteration count walks forward from that
///         base so a bigger run still narrows to the same first failure.
///     </para>
///     <para>
///         Malformed values are ignored rather than thrown: a CI environment
///         that accidentally sets a non-numeric value must not turn every fuzz
///         test into a discovery error.
///     </para>
/// </remarks>
public static class TestSeeds
{
    /// <summary>Environment variable that pins the first seed of a run.</summary>
    public const string SeedVariable = "HARBOR_TEST_SEED";

    /// <summary>Environment variable that expands a run to N consecutive seeds.</summary>
    public const string IterationsVariable = "HARBOR_TEST_ITERATIONS";

    private const int MaxIterations = 10_000;

    /// <summary>
    ///     Resolves the seeds for one stochastic test, honouring the two
    ///     environment overrides. The returned list is never empty.
    /// </summary>
    /// <param name="defaults">The seeds the test uses in an ordinary run.</param>
    public static IReadOnlyList<int> Values(params int[] defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        int? seed = ReadInt(SeedVariable);
        int? iterations = ReadInt(IterationsVariable);
        if (iterations is <= 0)
        {
            iterations = null;
        }

        if (seed is null && iterations is null)
        {
            if (defaults.Length == 0)
            {
                return [0];
            }

            return defaults;
        }

        int first = seed ?? (defaults.Length == 0 ? 0 : defaults[0]);
        if (iterations is null)
        {
            return [first];
        }

        var values = new int[Math.Min(iterations.Value, MaxIterations)];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = unchecked(first + i);
        }

        return values;
    }

    /// <summary>
    ///     Reads a signed decimal integer, or <see langword="null" /> when the
    ///     variable is absent, blank, or not a number.
    /// </summary>
    private static int? ReadInt(string variable)
    {
        string? raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }
}
