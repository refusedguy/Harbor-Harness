using Harbor.Abstractions.Models;

namespace Harbor.Application.Agents;

/// <summary>
///     Per-run budget ledger (epic #41, slice B2.3): accumulates the four
///     separated sources of <see cref="RunBudget" /> and enforces
///     <see cref="RunBudgetCaps" /> by threshold compare.
/// </summary>
/// <remarks>
///     <para>
///         One instance per run, owned by <c>AgentLoop</c>. The loop never
///         shares it across runs: the counters are run totals, and a shared
///         instance would attribute one run's spend to the next.
///     </para>
///     <para>
///         Thread-safe (single lock): parallel request fan-out may bracket
///         with <see cref="BeginRequest" />/<see cref="CompleteRequest" />.
///         The sequential loop never begins a request — its trips therefore
///         read <see cref="BudgetOvershootReason.LateUsage" /> by
///         construction, which is the honest shape for usage that arrives
///         after the request that spent it.
///     </para>
///     <para>
///         Cost is priced here, in the core, via
///         <see cref="Pricing.CalculateCost" /> — the same single home
///         <c>SessionMetadata.AddUsage</c> folds through (#653). No
///         presentation code may re-derive it.
///     </para>
/// </remarks>
public sealed class RunBudgetTracker
{
    private readonly RunBudgetCaps _caps;
    private readonly object _sync = new();
    private int _inFlight;
    private bool _hasReportedUsage;
    private int _reportedInputTokens;
    private int _reportedOutputTokens;
    private decimal _tariffCostUsd;
    private Pricing _lastPricing = Pricing.Unknown;
    private int _estimatedTokens;
    private long _outputBytes;
    private bool _tripped;

    /// <summary>
    ///     Create a ledger enforcing <paramref name="caps" />.
    /// </summary>
    /// <param name="caps">The ceilings to enforce.</param>
    public RunBudgetTracker(RunBudgetCaps caps)
    {
        ArgumentNullException.ThrowIfNull(caps);
        _caps = caps;
    }

    /// <summary>
    ///     Why the recorded spend exceeds the cap, when it does.
    ///     <see cref="BudgetOvershootReason.None" /> while within caps.
    /// </summary>
    public BudgetOvershootReason Overshoot { get; private set; } = BudgetOvershootReason.None;

    /// <summary>
    ///     Mark one request in flight. Only parallel fan-out calls this; the
    ///     sequential loop does not (see the class remarks).
    /// </summary>
    public void BeginRequest()
    {
        lock (_sync)
        {
            _inFlight++;
        }
    }

    /// <summary>
    ///     Fold one finished request into the ledger.
    /// </summary>
    /// <param name="usage">Usage the provider reported; null when it reported none.</param>
    /// <param name="pricing">Rate table of the model that served the request.</param>
    /// <param name="estimatedTokens">
    ///     Heuristic estimate for the request. Read only when
    ///     <paramref name="usage" /> is null — a metered turn pays no
    ///     estimate cost, so the non-streaming path stays a threshold
    ///     compare rather than a second estimation pass.
    /// </param>
    public void CompleteRequest(Usage? usage, Pricing pricing, int estimatedTokens)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        lock (_sync)
        {
            if (_inFlight > 0)
                _inFlight--;
            if (usage is not null)
            {
                _hasReportedUsage = true;
                _reportedInputTokens += usage.InputTokens;
                _reportedOutputTokens += usage.OutputTokens;
                _tariffCostUsd += pricing.CalculateCost(usage);
                _lastPricing = pricing;
            }
            else
            {
                _estimatedTokens += estimatedTokens;
            }

            NoteTripLocked();
        }
    }

    /// <summary>
    ///     Count streamed output bytes (called on the delta path, per delta).
    /// </summary>
    /// <param name="byteCount">UTF-8 bytes of one delta. Must not be negative.</param>
    public void RecordOutputBytes(long byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(byteCount, 0);
        lock (_sync)
        {
            _outputBytes += byteCount;
            NoteTripLocked();
        }
    }

    /// <summary>
    ///     The first exceeded ceiling in fixed order (tokens, spend,
    ///     output size); null while within caps. Pure threshold compares —
    ///     no lookup, no allocation.
    /// </summary>
    /// <returns>The tripped limit kind, or null.</returns>
    public RunLimitKind? CheckCap()
    {
        lock (_sync)
        {
            return CheckCapLocked();
        }
    }

    /// <summary>
    ///     Snapshot the run's budget read-model. Reported usage prices into
    ///     the tariff; a missing usage prices into nothing — the tariff
    ///     reads null (<see cref="BudgetSource.Unknown" />), never zero.
    /// </summary>
    /// <returns>The four-source read-model for the run so far.</returns>
    public RunBudget Snapshot()
    {
        lock (_sync)
        {
            Usage? reported = _hasReportedUsage
                ? new Usage(_reportedInputTokens, _reportedOutputTokens)
                : null;
            decimal? tariff = _hasReportedUsage ? _tariffCostUsd : null;
            // Estimate-side money, priced at the output rate of the last
            // call's table: the basis is documented, not billed, and null
            // while no table was ever seen.
            decimal? estimated = _estimatedTokens > 0 && !_lastPricing.IsUnknown
                ? _lastPricing.CalculateCost(new Usage(0, _estimatedTokens))
                : null;
            return new RunBudget(reported, _estimatedTokens, tariff, estimated, _outputBytes);
        }
    }

    private RunLimitKind? CheckCapLocked()
    {
        if (_caps.MaxTokens is { } maxTokens && TokenTotalLocked() > maxTokens)
            return RunLimitKind.MaxTokens;
        if (_caps.MaxCostUsd is { } maxCost && _hasReportedUsage && _tariffCostUsd > maxCost)
            return RunLimitKind.MaxCost;
        if (_caps.MaxOutputBytes is { } maxBytes && _outputBytes > maxBytes)
            return RunLimitKind.MaxOutputBytes;
        return null;
    }

    /// <summary>
    ///     The token figure the cap binds: reported totals while any usage
    ///     was reported, the heuristic estimate otherwise. Enforcement
    ///     pragmatism, not a blend — the read-model still reports which
    ///     source bound it via <see cref="RunBudget.TokenSource" />.
    /// </summary>
    private int TokenTotalLocked() =>
        _hasReportedUsage ? _reportedInputTokens + _reportedOutputTokens : _estimatedTokens;

    private void NoteTripLocked()
    {
        if (CheckCapLocked() is null)
            return;
        if (!_tripped)
        {
            _tripped = true;
            Overshoot = _inFlight > 0
                ? BudgetOvershootReason.InFlightRequests
                : BudgetOvershootReason.LateUsage;
        }
        else if (Overshoot == BudgetOvershootReason.LateUsage)
        {
            // Spend recorded after the trip: the stop decision lagged behind
            // the meter. First cause is kept for an in-flight trip — a lag
            // on top of in-flight requests adds no new information.
            Overshoot = BudgetOvershootReason.CancelLag;
        }
    }
}
