using TUnit.Core.Interfaces;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     The concurrency budget for the CellForge PTY scenarios: four scenarios
///     at a time, in this process.
///     <para />
///     TUnit keys the limiter's semaphore by the LIMIT TYPE, so this one type
///     is one gate for the whole assembly — the scenarios do not each get
///     their own four permits (which would be 40 × 4 = 160 concurrent child
///     processes on a four-core runner).
///     <para />
///     Four is the runner's core count, and one scenario per core is the
///     honest budget: a scenario is not a unit of CPU work, it is a spawned
///     <c>dotnet exec Harbor.App.Cli.dll</c> inside a pseudo-terminal, and the
///     box has four cores to run those children on. The scenarios are mostly
///     waiting (the mock provider streams at 4 characters per 50 ms), so the
///     budget is about the STARTUP burst, not about the streaming.
///     <para />
///     This replaced the project-wide <c>[NotInParallel("pty")]</c> key
///     (#1789), which bounded the same resource the wrong way: it allowed ONE
///     scenario at a time (parallelism factor 1.00, wall time = the sum of the
///     durations) instead of one per core.
/// </summary>
public sealed record PtyScenarioLimit : IParallelLimit
{
    /// <summary>Permits: one scenario per runner core.</summary>
    public int Limit => 4;
}
