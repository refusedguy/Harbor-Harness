namespace Harbor.Abstractions.Models;

/// <summary>
///     Usage of billable non-model services for one session or turn (issue #2671, D07).
/// </summary>
/// <remarks>
///     <para>
///         Model tokens already have their own counter. Web tools spend a
///         different budget: a search, a fetch or an OCR pass is billed per call
///         and may also carry tokens of its own. Folding those figures into the
///         model counter loses which service spent them, so each service gets a
///         separate call counter and a separate token counter here.
///     </para>
///     <para>
///         The value is immutable and additive: two snapshots combine with
///         <see cref="Combine" />, so a caller can accumulate per-turn usage into
///         a session total without shared mutable state. This is the event-payload
///         shape; wiring it into a stats event is a separate slice.
///     </para>
/// </remarks>
public sealed record ServiceUsage
{
    /// <summary>
    ///     Create a snapshot. Every counter defaults to zero, so an empty usage
    ///     is cheap to construct. Negative counters are rejected: a counter that
    ///     goes backwards is a bug, not a refund.
    /// </summary>
    /// <param name="searchCalls">Number of search calls.</param>
    /// <param name="fetchCalls">Number of fetch calls.</param>
    /// <param name="ocrCalls">Number of OCR calls.</param>
    /// <param name="searchTokens">Tokens attributed to search.</param>
    /// <param name="fetchTokens">Tokens attributed to fetch.</param>
    /// <param name="ocrTokens">Tokens attributed to OCR.</param>
    public ServiceUsage(
        int searchCalls = 0,
        int fetchCalls = 0,
        int ocrCalls = 0,
        long searchTokens = 0,
        long fetchTokens = 0,
        long ocrTokens = 0)
    {
        SearchCalls = RequireNonNegative(searchCalls, nameof(searchCalls));
        FetchCalls = RequireNonNegative(fetchCalls, nameof(fetchCalls));
        OcrCalls = RequireNonNegative(ocrCalls, nameof(ocrCalls));
        SearchTokens = RequireNonNegative(searchTokens, nameof(searchTokens));
        FetchTokens = RequireNonNegative(fetchTokens, nameof(fetchTokens));
        OcrTokens = RequireNonNegative(ocrTokens, nameof(ocrTokens));
    }

    /// <summary>Number of search calls.</summary>
    public int SearchCalls { get; }

    /// <summary>Number of fetch calls.</summary>
    public int FetchCalls { get; }

    /// <summary>Number of OCR calls.</summary>
    public int OcrCalls { get; }

    /// <summary>Tokens attributed to search.</summary>
    public long SearchTokens { get; }

    /// <summary>Tokens attributed to fetch.</summary>
    public long FetchTokens { get; }

    /// <summary>Tokens attributed to OCR.</summary>
    public long OcrTokens { get; }

    /// <summary>Calls across all services.</summary>
    public int TotalCalls => SearchCalls + FetchCalls + OcrCalls;

    /// <summary>Tokens across all services.</summary>
    public long TotalTokens => SearchTokens + FetchTokens + OcrTokens;

    /// <summary>True when neither calls nor tokens were recorded.</summary>
    public bool IsEmpty => TotalCalls == 0 && TotalTokens == 0;

    /// <summary>
    ///     Add another snapshot without mutating either operand.
    /// </summary>
    /// <param name="other">The snapshot to add.</param>
    /// <returns>The summed usage.</returns>
    public ServiceUsage Combine(ServiceUsage other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new ServiceUsage(
            SearchCalls + other.SearchCalls,
            FetchCalls + other.FetchCalls,
            OcrCalls + other.OcrCalls,
            SearchTokens + other.SearchTokens,
            FetchTokens + other.FetchTokens,
            OcrTokens + other.OcrTokens);
    }

    private static int RequireNonNegative(int value, string name) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(name);

    private static long RequireNonNegative(long value, string name) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(name);
}
