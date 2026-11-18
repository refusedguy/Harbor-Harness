namespace Harbor.TestKit;

/// <summary>
///     The exception a fuzz invariant violation becomes, carrying everything
///     needed to replay the failing case without re-running the walk.
/// </summary>
/// <remarks>
///     A fuzz failure that only says "assertion failed" is not a finding: it
///     cannot be handed to another engineer or pasted into a bug report. This
///     type puts the loop name, the seed, the case index and the exact input
///     in the message, because those four things are the whole reproduction.
/// </remarks>
public sealed class FuzzViolationException : Exception
{
    public FuzzViolationException(
        string name,
        int seed,
        int index,
        int count,
        byte[] input,
        string reason,
        Exception? inner = null)
        : base(
            $"fuzz '{name}' failed at seed {seed}, case {index}/{count}: {reason}"
            + $"{Environment.NewLine}input (base64): {Convert.ToBase64String(input)}",
            inner)
    {
        Name = name;
        Seed = seed;
        Index = index;
        Count = count;
        Input = input;
    }

    public string Name { get; }

    public int Seed { get; }

    public int Index { get; }

    public int Count { get; }

    public byte[] Input { get; }
}

/// <summary>
///     Reusable fuzz driver: hand it a seed, an input factory and an invariant,
///     and it walks the inputs in a reproducible order.
/// </summary>
/// <typeparam name="TInput">The generated input type.</typeparam>
/// <remarks>
///     <para>
///         The seed is owned by <see cref="TestSeeds" /> at the call site, not
///         hidden here. Every stochastic test reads its seeds from that one
///         source, so <c>HARBOR_TEST_SEED</c> narrows a whole suite to one
///         replayable walk and <c>HARBOR_TEST_ITERATIONS</c> widens it.
///     </para>
///     <para>
///         <b>Why the helper exists.</b> Three fuzz suites had the same for-loop
///         and three different ideas of what a failure message should contain.
///         The loop is not the interesting part; the replay data is. Routing
///         every violation through <see cref="FuzzViolationException" /> makes
///         the seed and the exact bytes a permanent part of the contract.
///     </para>
///     <para>
///         <b>What is asserted.</b> The helper does not decide what correct
///         looks like; the caller's invariant does. It only guarantees that
///         the walk is deterministic for a given seed and that a violation is
///         reported with its input, even when the invariant throws instead of
///         returning a diagnostic.
///     </para>
/// </remarks>
public static class FuzzLoop<TInput>
{
    /// <summary>
    ///     Walks <paramref name="count" /> generated inputs and checks each one.
    /// </summary>
    /// <param name="name">Loop name, echoed into a failure message.</param>
    /// <param name="seed">RNG seed; the same seed yields the same walk.</param>
    /// <param name="count">How many inputs to generate.</param>
    /// <param name="makeInput">Builds the next input from the seeded RNG.</param>
    /// <param name="invariant">Throws when an input violates the contract.</param>
    public static async Task RunAsync(
        string name,
        int seed,
        int count,
        Func<Random, TInput> makeInput,
        Func<TInput, int, Task> invariant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(makeInput);
        ArgumentNullException.ThrowIfNull(invariant);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var rng = new Random(seed);

        for (int index = 0; index < count; index++)
        {
            TInput input = makeInput(rng);

            try
            {
                await invariant(input, index).ConfigureAwait(false);
            }
            catch (FuzzViolationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Convert the raw bytes back into a replay recipe. TInput is
                // deliberately not constrained: callers that fuzz a text
                // parser still hand the byte[] they parsed to this helper,
                // which is why the diagnostic type is byte[]-shaped.
                if (input is byte[] bytes)
                {
                    throw new FuzzViolationException(name, seed, index, count, bytes, ex.Message, ex);
                }

                string text = input is null ? "<null>" : input.ToString() ?? "<null>";
                throw new FuzzViolationException(
                    name, seed, index, count,
                    System.Text.Encoding.UTF8.GetBytes(text),
                    ex.Message, ex);
            }
        }
    }
}
