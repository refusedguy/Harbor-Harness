using TUnit.Core.Interfaces;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     The concurrency budget for the CellForge PTY scenarios: two scenarios
///     at a time, in this process.
///     <para />
///     TUnit keys the limiter's semaphore by the LIMIT TYPE, so this one type
///     is one gate for the whole assembly. The scenarios do not each get
///     their own two permits (which would be 40 × 2 = 80 concurrent child
///     processes on a four-core runner).
///     <para />
///     Two is a budget, not a preference. A scenario is not a unit of CPU
///     work, it is a spawned <c>dotnet exec Harbor.App.Cli.dll</c> inside a
///     pseudo-terminal, and the box has four cores to run those children on.
///     The suite's CPU demand is dominated by the child boot (48 spawns at
///     about 0.45 s, some 21 s of the run), so two concurrent children leave
///     the runner's cores alone, and the scenarios that measure the APP's own
///     clock (the 2 000 ms quit-gesture window) keep a timing margin. The
///     value is a constant to be raised with a measurement, not a ratio to be
///     tuned by feel: 3 and 4 are the next candidates, and each step needs
///     its own run's numbers before it is believed.
///     <para />
///     This replaced the project-wide <c>[NotInParallel("pty")]</c> key
///     (#1789), which bounded the same resource the wrong way: it allowed ONE
///     scenario at a time (parallelism factor 0.99, wall time = the sum of the
///     durations) instead of a couple at a time.
/// </summary>
public sealed record PtyScenarioLimit : IParallelLimit
{
    /// <summary>Permits: two scenarios at a time.</summary>
    public int Limit => 2;
}
