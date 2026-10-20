using Harbor.E2E.Framework;

namespace Harbor.E2E.App.Avalonia;

/// <summary>
///     The two-process split of this project's test classes (#1790).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this project cannot be parallelised IN-PROCESS.</b>
///         All fifteen classes here drive the SAME headless Avalonia
///         application: <c>HeadlessAvaloniaDriver</c> keeps
///         <c>Application.Current</c>, the desktop lifetime and the DI host in
///         STATIC fields, and Avalonia permits exactly one
///         <c>Application</c> per AppDomain — there is no second app to
///         build, so a second class cannot have a window of its own. The
///         classes also share two more process-wide things: the driver sets
///         <c>$HOME</c> (an environment variable is not thread-local), and
///         <c>CommonConfig.OverrideHarborHomeForTests</c> pins the Harbor home
///         for the whole process. Two classes running at the same time would
///         therefore drive one window and disagree about the home.
///     </para>
///     <para>
///         That is why every class in this project holds the SAME
///         <c>[NotInParallel("e2e-framework")]</c> key: the key names the
///         competitors, and the competitors are all of them. Splitting the
///         key across the classes would not make the suite faster, it would
///         make it racy — a keyed attribute is a mutex over its NAMED PEERS
///         only, so two differently-keyed classes would overlap and share one
///         <c>MainWindow</c>. See docs/TEST_PATTERNS.md §"Parallelism".
///     </para>
///     <para>
///         <b>What IS available is a second PROCESS.</b> A second process gets
///         its own Avalonia application, its own <c>$HOME</c> and its own
///         Harbor home, so the two shards below can run side by side. The
///         project's wall time is currently the SUM of its class durations
///         (51.67s of test time against a 53.80s wall on CI run 39192015051 —
///         a parallelism factor of 0.96, i.e. fully serialised); two balanced
///         shards turn that into the MAX of the two.
///     </para>
///     <para>
///         The shard split is expressed as two <c>--treenode-filter</c>
///         expressions because that is the only lever TUnit exposes from the
///         command line, and because it needs no change to the classes: the
///         filter selects which classes a process DISCOVERS, so each shard is
///         a complete, self-contained run of the same fixture. CI runs one
///         <c>dotnet exec</c> per test project per shard — the two shards are
///         two <c>dotnet exec</c> invocations and therefore two processes.
///     </para>
///     <para>
///         <b>Balance.</b> The split is by MEASURED class duration (CI run
///         39192015051, per-class sums of the TRX durations), not by class
///         count: the two heaviest classes alone are half the project's time,
///         so an even split by class would be a 4:1 split by cost. Re-balance
///         this table when a class is added or a class's cost moves — and
///         <see cref="E2eShardsTests" /> fails the build if a class is left
///         out of both shards.
///     </para>
/// </remarks>
public static class E2eShards
{
    /// <summary>
    ///     The constraint key every class in this project holds. The one
    ///     Avalonia session, the one <c>$HOME</c> and the one Harbor home are
    ///     the shared resource it names.
    /// </summary>
    public const string ConstraintKey = "e2e-framework";

    /// <summary>
    ///     Shard 0 — the classes whose measured cost is the larger half of
    ///     the project. Run as
    ///     <c>dotnet exec Harbor.E2E.App.Avalonia.dll --treenode-filter "…"</c>.
    ///     <para>
    ///         The filter, verbatim:
    ///         <c>/*/*/(AvaloniaUiTests)|(SessionListTests)|(StandaloneControlsTests)/*</c>
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> Shard0 { get; } =
    [
        nameof(AvaloniaUiTests),
        nameof(ComponentTests.SessionListTests),
        nameof(ComponentTests.StandaloneControlsTests),
    ];

    /// <summary>
    ///     Shard 1 — the rest of the project. Same invocation as
    ///     <see cref="Shard0" />, with this shard's filter.
    ///     <para>
    ///         The filter, verbatim:
    ///         <c>/*/*/(ToastTests)|(ChatViewTests)|(CommandPaletteTests)|(ComponentGoldenFramesTests)|(FocusSessionTests)|(GoldenFrameTests)|(MicroControlsTests)|(OnboardingTests)|(OverlaySurfacesTests)|(SettingsTests)|(ShellChromeTests)|(StatusBarTests)|(StateTestRunnerTests)/*</c>
    ///     </para>
    ///     <para>
    ///         <c>StateTestRunnerTests</c> lives in the REFERENCED
    ///         <c>Harbor.E2E.Framework</c> project, not in this one — but TUnit
    ///         discovers and runs a referenced test project's classes in every
    ///         project that references it, so those 26 tests are part of this
    ///         process today and a filter that did not name them would DROP
    ///         them from CI. They cost 0.2s in total, so they ride along here.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> Shard1 { get; } =
    [
        nameof(ComponentTests.ToastTests),
        nameof(ComponentTests.ChatViewTests),
        nameof(ComponentTests.CommandPaletteTests),
        nameof(ComponentTests.ComponentGoldenFramesTests),
        nameof(ComponentTests.FocusSessionTests),
        nameof(ComponentTests.GoldenFrameTests),
        nameof(ComponentTests.MicroControlsTests),
        nameof(ComponentTests.OnboardingTests),
        nameof(ComponentTests.OverlaySurfacesTests),
        nameof(ComponentTests.SettingsTests),
        nameof(ComponentTests.ShellChromeTests),
        nameof(ComponentTests.StatusBarTests),
        nameof(StateTestRunnerTests),
    ];

    /// <summary>Both shards, in order.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> All { get; } = [Shard0, Shard1];

    /// <summary>
    ///     The <c>--treenode-filter</c> expression that selects exactly the
    ///     given classes.
    /// </summary>
    /// <param name="classNames">The simple names of the classes to select.</param>
    /// <returns>
    ///     A filter of the shape <c>/*/*/(ClassA)|(ClassB)/*</c>: the assembly
    ///     and namespace segments are wildcards (the classes live in three
    ///     namespaces — this one, <c>ComponentTests</c>, and the referenced
    ///     <c>Harbor.E2E.Framework</c>), the class segment is the OR of the
    ///     requested names, and the test segment is a wildcard.
    /// </returns>
    public static string FilterFor(IReadOnlyList<string> classNames)
    {
        if (classNames.Count == 0)
        {
            throw new ArgumentException("A shard must name at least one class.", nameof(classNames));
        }

        return "/*/*/(" + string.Join(")|(", classNames) + ")/*";
    }
}
