using Harbor.Abstractions.Models;
using Harbor.Application.Agents;
using CSharpFunctionalExtensions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     #1557: the cap check answers with a <c>Maybe&lt;RunLimitKind&gt;</c>.
/// </summary>
/// <remarks>
///     <para>
///         The ledger has three ceilings and no budget for a fourth answer, so
///         "no ceiling tripped" is a state of the ledger — not a missing value
///         the caller has to pattern-match away. The type says it: the answer
///         is <c>Some(kind)</c> when a ceiling is exceeded and <c>None</c>
///         while the run is within caps.
///     </para>
///     <para>
///         Every branch of the chain gets a case here: no ceiling set, a
///         ceiling set and not reached, a ceiling reached exactly (the
///         boundary — reaching a cap is not exceeding it), a ceiling exceeded,
///         the one refusal the ledger cannot judge (spend without reported
///         usage: the tariff is unknown, so the ceiling is not decided), and
///         the priority between two ceilings that are both exceeded.
///     </para>
/// </remarks>
public class RunBudgetCapTests
{
    /// <summary>2 USD per million input tokens, 10 USD per million output tokens.</summary>
    private static readonly Pricing Table = new(2m, 10m);

    [Test]
    public async Task NoCaps_ReportsNoLimit()
    {
        var tracker = new RunBudgetTracker(new RunBudgetCaps());
        tracker.CompleteRequest(new Usage(1000, 1000), Table, 0);

        Maybe<RunLimitKind> hit = tracker.CheckCap();

        await Assert.That(hit.HasNoValue).IsTrue();
    }

    [Test]
    public async Task CapsSetAndNotReached_ReportsNoLimit()
    {
        var tracker = new RunBudgetTracker(new RunBudgetCaps(MaxTokens: 1000, MaxCostUsd: 1m, MaxOutputBytes: 1000));
        tracker.CompleteRequest(new Usage(100, 100), Table, 0);
        tracker.RecordOutputBytes(10);

        Maybe<RunLimitKind> hit = tracker.CheckCap();

        await Assert.That(hit.HasNoValue).IsTrue();
    }

    [Test]
    public async Task TokenCapReachedExactly_ReportsNoLimit()
    {
        // The comparison is strict: a run that exactly reaches the ceiling is
        // within it, and only the next token trips the cap.
        var tracker = new RunBudgetTracker(new RunBudgetCaps(MaxTokens: 1500));
        tracker.CompleteRequest(new Usage(1000, 500), Table, 0);

        Maybe<RunLimitKind> hit = tracker.CheckCap();

        await Assert.That(hit.HasNoValue).IsTrue();
    }

    [Test]
    public async Task TokenCapExceeded_ReportsTokenLimit()
    {
        var tracker = new RunBudgetTracker(new RunBudgetCaps(MaxTokens: 1500));
        tracker.CompleteRequest(new Usage(1000, 500), Table, 0);
        tracker.CompleteRequest(new Usage(1, 0), Table, 0);

        Maybe<RunLimitKind> hit = tracker.CheckCap();

        await Assert.That(hit.HasValue).IsTrue();
        await Assert.That(hit.Value).IsEqualTo(RunLimitKind.MaxTokens);
    }

    [Test]
    public async Task SpendCapExceeded_ReportsCostLimit()
    {
        // 1000 input + 500 output tokens at 2/10 USD per million = 0.007 USD.
        var tracker = new RunBudgetTracker(new RunBudgetCaps(MaxCostUsd: 0.005m));
        tracker.CompleteRequest(new Usage(1000, 500), Table, 0);

        Maybe<RunLimitKind> hit = tracker.CheckCap();

        await Assert.That(hit.HasValue).IsTrue();
        await Assert.That(hit.Value).IsEqualTo(RunLimitKind.MaxCost);
    }

    [Test]
    public async Task SpendCapWithoutReportedUsage_ReportsNoLimit()
    {
        // The refusal branch: the tariff is unknown until the provider reports
        // usage, so a spend ceiling cannot be decided from an estimate. The
        // ledger says "no limit", never a guess.
        var tracker = new RunBudgetTracker(new RunBudgetCaps(MaxCostUsd: 0.000001m));
        tracker.CompleteRequest(null, Table, 400);

        Maybe<RunLimitKind> hit = tracker.CheckCap();

        await Assert.That(hit.HasNoValue).IsTrue();
    }

    [Test]
    public async Task OutputCapExceeded_ReportsOutputLimit()
    {
        var tracker = new RunBudgetTracker(new RunBudgetCaps(MaxOutputBytes: 100));
        tracker.RecordOutputBytes(101);

        Maybe<RunLimitKind> hit = tracker.CheckCap();

        await Assert.That(hit.HasValue).IsTrue();
        await Assert.That(hit.Value).IsEqualTo(RunLimitKind.MaxOutputBytes);
    }

    [Test]
    public async Task OutputCapReachedExactly_ReportsNoLimit()
    {
        var tracker = new RunBudgetTracker(new RunBudgetCaps(MaxOutputBytes: 100));
        tracker.RecordOutputBytes(100);

        Maybe<RunLimitKind> hit = tracker.CheckCap();

        await Assert.That(hit.HasNoValue).IsTrue();
    }

    [Test]
    public async Task TokenCapOutranksSpendCap()
    {
        // Both ceilings are exceeded: the token ceiling is checked first, so
        // the answer names it.
        var tracker = new RunBudgetTracker(new RunBudgetCaps(MaxTokens: 100, MaxCostUsd: 0.001m));
        tracker.CompleteRequest(new Usage(1000, 500), Table, 0);

        Maybe<RunLimitKind> hit = tracker.CheckCap();

        await Assert.That(hit.HasValue).IsTrue();
        await Assert.That(hit.Value).IsEqualTo(RunLimitKind.MaxTokens);
    }

    [Test]
    public async Task SpendCapOutranksOutputCap()
    {
        // Both ceilings are exceeded: spend is checked before output size.
        var tracker = new RunBudgetTracker(new RunBudgetCaps(MaxCostUsd: 0.001m, MaxOutputBytes: 10));
        tracker.CompleteRequest(new Usage(1000, 500), Table, 0);
        tracker.RecordOutputBytes(20);

        Maybe<RunLimitKind> hit = tracker.CheckCap();

        await Assert.That(hit.HasValue).IsTrue();
        await Assert.That(hit.Value).IsEqualTo(RunLimitKind.MaxCost);
    }

    [Test]
    public async Task TheAnswerIsStable_OnceTheCapIsExceeded()
    {
        // The ledger keeps naming the same ceiling after the trip: the answer
        // is a state of the ledger, not a one-shot event.
        var tracker = new RunBudgetTracker(new RunBudgetCaps(MaxTokens: 100));
        tracker.CompleteRequest(new Usage(1000, 0), Table, 0);
        tracker.CompleteRequest(new Usage(1000, 0), Table, 0);

        Maybe<RunLimitKind> first = tracker.CheckCap();
        tracker.CompleteRequest(new Usage(1000, 0), Table, 0);
        Maybe<RunLimitKind> second = tracker.CheckCap();

        await Assert.That(first.HasValue).IsTrue();
        await Assert.That(first.Value).IsEqualTo(RunLimitKind.MaxTokens);
        await Assert.That(second.HasValue).IsTrue();
        await Assert.That(second.Value).IsEqualTo(RunLimitKind.MaxTokens);
    }
}
