// E2eAvaloniaShardCoverageRules.cs — the arch-rule half of the headless
// Avalonia E2E process split.
//
// THE DEFECT THIS GUARDS
// ----------------------
// `tests/Harbor.E2E.App.Avalonia` runs as SEVERAL PROCESSES. Each shard is one
// `dotnet exec` of the same assembly with its own `--treenode-filter`, and the
// shard membership lives in `tests/Harbor.E2E.App.Avalonia/e2e-shards.json`.
//
// A class that is in NO shard is a class CI STOPS RUNNING. Nothing else
// notices: the shard that would have carried it still exits 0, the TRX it
// writes has one class fewer, and the suite is green with fewer tests than it
// had. That is the shape this file exists to close.
//
// WHY IT IS AN ARCH RULE AND NOT A TEST IN THE E2E ASSEMBLY
// ----------------------------------------------------------
// The first version of the shard table shipped with a guard test INSIDE the
// E2E assembly, and it was removed (PR #1818). A new test class in that
// project is an UNKEYED test, so it lands in TUnit's parallel bucket and its
// presence MOVES the order of the classes around it — and that suite is
// order-fragile by documentation (the `SettingsTests` skip named "orphaned
// dispatcher continuations from previously-initialized hosts" and said the
// deterministic fix "requires process-per-class isolation"). The guard moved
// `Chat_ShowToolCallCard` from the sixth slot of its class to the second on
// two consecutive CI runs, and the test failed there both times. A guard that
// is worth that much fragility is not worth having.
//
// The check belongs HERE, where it can read the tree without adding a test to
// it. The file the removed guard recommended is the arch-rule slot (#1724).
//
// THE UNIVERSE, AND WHY IT IS ONE PROJECT
// ---------------------------------------
// The classes this assembly DISCOVERS are the classes of ONE project:
// `tests/Harbor.E2E.App.Avalonia`, its own fifteen classes.
//
// It used to be two. `tests/Harbor.E2E.Framework` is a REFERENCED test
// project, and TUnit discovers a referenced test project's classes inside
// every project that references it: a `[Test]` added there would be part of
// THIS assembly's run too, and a filter naming only this project's classes
// would drop it from CI SILENTLY. The shard would still exit 0 and the suite
// would still be green. The universe therefore spanned both.
//
// The second half is now unreachable BY CONSTRUCTION, not merely unused.
// `TestProjectReferenceRules.ARereferencedTestProjectIsDiscoveredInExactlyOneModule`
// forbids a referenced test project to declare test methods at all, and it is
// a gate: a `[Test]` in `Harbor.E2E.Framework` turns the architecture suite
// red, it does not add a silent extra shard. The framework's own tests live
// in `tests/Harbor.E2E.Framework.Tests`, a project nothing references (#1943
// moved them there), so they run exactly once.
//
// Grading the second half anyway would teach a reader that a trap is live
// when it is not, and that trap was the reason the universe was derived
// rather than written down. It is still derived, from ONE project, and the
// generator (`harbor-gates gen-e2e-shards`) derives the same universe from
// the same source.
//
// HOW THE UNIVERSE IS DERIVED
// ---------------------------
// A type is a test class when ITS OWN extent declares a `[Test]`: the extent
// runs from the type's declaration to the next top-level declaration, so a
// helper class in the same file does not inherit its neighbour's tests, and a
// file with two suites yields two classes. Nested types are ignored — TUnit
// schedules top-level types.
//
// NON-VACUITY
// -----------
// A rule over a list that matched nothing is indistinguishable from a broken
// rule, and a broken rule is worse than none because it is believed. Five
// mechanisms, each going RED rather than passing quietly:
//
//   1. `TheClassUniverseIsNotEmpty` requires the scan to find a non-trivial
//      set of classes. A scan that stopped matching the tree would otherwise
//      make every other assertion below true of nothing — the #901 failure,
//      where two checks pass on zero subjects.
//   2. `ThePlanFileExistsAndParses` requires the plan to be there AND to
//      parse. A missing plan is not "no classes to run"; it is a broken CI.
//   3. `TheRuleReportsAPlantedClassMissingFromThePlan` drives the SAME
//      matcher with a synthetic class and requires it to be reported by name.
//   4. `EveryShardNamesOnlyClassesThatExist` closes the other end: a plan that
//      names a class that no longer exists fails too, so a renamed class
//      cannot leave a stale entry behind.
//   5. `ThePlanRunsTheSuiteInMoreThanOneProcess` fails a plan that collapses
//      back to one process, which is the serialisation this change removed.
//
// `RequireRepoRoot` THROWS rather than returning quietly: "could not find the
// file this guard polices" must never render as "every class is in a shard".

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Every test class the headless Avalonia E2E assembly discovers must be
///     named by exactly one shard of the process plan, and no shard may name a
///     class that does not exist.
/// </summary>
public sealed class E2eAvaloniaShardCoverageRules
{
    /// <summary>Repo-relative path of the shard plan this rule grades.</summary>
    private const string PlanRelativePath = "tests/Harbor.E2E.App.Avalonia/e2e-shards.json";

    /// <summary>
    ///     The projects whose test classes the E2E assembly discovers: only its
    ///     own. A REFERENCED test project cannot contribute one, because the
    ///     arch rule <c>TestProjectReferenceRules</c> forbids a referenced test
    ///     project to declare test methods at all.
    /// </summary>
    private static readonly string[] AssembledProjects =
    [
        "Harbor.E2E.App.Avalonia",
    ];

    /// <summary>
    ///     A top-level type declaration. Column 0 only — a nested type is not
    ///     a suite and TUnit does not schedule one.
    /// </summary>
    private static readonly Regex TypeDeclaration = new(
        @"^(?:\[[^\]]*\]\s*|public\s+|internal\s+|private\s+|protected\s+|sealed\s+|abstract\s+"
        + @"|static\s+|partial\s+|file\s+|unsafe\s+)*(?:class|record|struct)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    /// <summary>
    ///     A <c>[Test]</c> attribute at the start of a line, ignoring what
    ///     follows the name so <c>[Test]</c>, <c>[Test(...)]</c> and
    ///     <c>[Test, NotInParallel("k")]</c> all match while
    ///     <c>[TestKitSomething]</c> does not.
    /// </summary>
    private static readonly Regex TestAttribute = new(@"^\s*\[Test(\]|\(|\s|,)", RegexOptions.Compiled);

    /// <summary>
    ///     The plan parses, and it is the shape this rule grades. A missing or
    ///     malformed plan is a broken CI, not an empty one.
    /// </summary>
    [Test]
    public async Task ThePlanFileExistsAndParses()
    {
        string text = File.ReadAllText(PlanPath());

        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;

        await Assert.That(root.TryGetProperty("schemaVersion", out _)).IsTrue()
            .Because("the plan must declare a schemaVersion so a future shape change is "
                + "visible rather than silently mis-read. The plan is at " + PlanRelativePath + ".");

        bool hasShards = root.TryGetProperty("shards", out JsonElement shards);
        await Assert.That(hasShards).IsTrue()
            .Because("the plan must carry a `shards` array — it IS the list of processes CI "
                + "runs. The plan is at " + PlanRelativePath + ".");

        await Assert.That(shards.GetArrayLength()).IsGreaterThan(0)
            .Because("an empty `shards` array means CI runs no process at all, and every "
                + "class in the assembly silently stops running.");
    }

    /// <summary>
    ///     The scan must find the classes. Without this, a scan that stopped
    ///     matching the tree would make every other assertion in this file
    ///     true of nothing while reading as a clean repository.
    /// </summary>
    [Test]
    public async Task TheClassUniverseIsNotEmpty()
    {
        IReadOnlyList<string> universe = DiscoverTestClasses();

        await Assert.That(universe.Count).IsGreaterThanOrEqualTo(15)
            .Because("the E2E assembly discovers at least fifteen classes of its own. Found "
                + universe.Count + ": "
                + string.Join(", ", universe) + ". A near-empty set means the scan stopped "
                + "matching the tree — a renamed folder, a moved file — and every rule below "
                + "would then be satisfied by nothing.");
    }

    /// <summary>
    ///     The rule proper: a class in no shard is a class CI stops running.
    /// </summary>
    [Test]
    public async Task EveryTestClassTheAssemblyDiscoversIsInExactlyOneShard()
    {
        IReadOnlyList<string> universe = DiscoverTestClasses();
        IReadOnlyDictionary<string, string> plan = ReadPlanClasses();

        List<string> missing = MissingFromPlan(universe, plan.Keys);

        await Assert.That(missing).IsEmpty()
            .Because("a class that is in NO shard is a class CI STOPS RUNNING, and nothing "
                + "else notices: the shard that would have carried it still exits 0, the TRX it "
                + "writes has one class fewer, and the suite is green with fewer tests than it "
                + "had. Missing from " + PlanRelativePath + ": "
                + (missing.Count == 0 ? "(none)" : string.Join(", ", missing))
                + ". Regenerate the plan with harbor-gates gen-e2e-shards. NOTE: the universe "
                + "is the classes of `Harbor.E2E.App.Avalonia` ALONE. A referenced test project "
                + "cannot contribute one, because `TestProjectReferenceRules` forbids a "
                + "referenced test project to declare test methods at all.");
    }

    /// <summary>
    ///     The other end: a shard may not name a class that does not exist.
    /// </summary>
    /// <remarks>
    ///     A renamed or deleted class leaves a stale entry behind, and a stale
    ///     entry is a filter that matches nothing — which is the same silent
    ///     loss as a missing class, from the other direction.
    /// </remarks>
    [Test]
    public async Task EveryShardNamesOnlyClassesThatExist()
    {
        IReadOnlyList<string> universe = DiscoverTestClasses();
        IReadOnlyDictionary<string, string> plan = ReadPlanClasses();

        List<string> stale = [.. plan.Keys.Where(name => !universe.Contains(name))];

        await Assert.That(stale).IsEmpty()
            .Because("a shard naming a class that does not exist is a filter that matches "
                + "nothing: the tests it claims to run are not run, and the shard is one class "
                + "short without saying so. Stale entries in " + PlanRelativePath + ": "
                + (stale.Count == 0 ? "(none)" : string.Join(", ", stale))
                + ". Regenerate the plan with harbor-gates gen-e2e-shards.");
    }

    /// <summary>
    ///     The rule must be able to report a bad class, proved on planted input.
    /// </summary>
    /// <remarks>
    ///     Today's plan is clean, so <see cref="EveryTestClassTheAssemblyDiscoversIsInExactlyOneShard" />
    ///     returns an empty list — which is what a correct plan looks like, and
    ///     also what a blind rule looks like. This drives the SAME matcher with
    ///     a synthetic class and requires it to be reported by name, so a green
    ///     run of the real rule means "the matcher fired and found nothing".
    /// </remarks>
    [Test]
    public async Task TheRuleReportsAPlantedClassMissingFromThePlan()
    {
        const string Planted = "PlantedSyntheticShardTests";

        IReadOnlyList<string> universe = DiscoverTestClasses();
        IReadOnlyDictionary<string, string> plan = ReadPlanClasses();

        List<string> planted = [.. universe, Planted];

        List<string> missing = MissingFromPlan(planted, plan.Keys);

        await Assert.That(missing.ToArray()).IsEquivalentTo(new[] { Planted })
            .Because("the planted class is in no shard, so it must be reported by name — and "
                + "ONLY it, because the real classes are all in the plan. Reported: "
                + (missing.Count == 0 ? "(nothing)" : string.Join(", ", missing))
                + ". A rule that reports nothing here is blind in the real plan too, and the "
                + "green assertion above was not evidence of anything.");
    }

    /// <summary>
    ///     The plan must run the suite in more than one process.
    /// </summary>
    /// <remarks>
    ///     One process means the classes share one Avalonia
    ///     <c>Application</c>, one <c>$HOME</c> and one pinned Harbor home, so
    ///     the <c>[NotInParallel("e2e-framework")]</c> key serialises them and
    ///     the project's wall is the SUM of its class durations — measured at
    ///     60.56 s of wall for 58.59 s of test time (parallelism 0.97, i.e.
    ///     fully serialised) on CI run 39404823168. Three processes bring the
    ///     critical path down to 28.36 s of test time. A plan that collapses
    ///     back to one shard would put that back, and nothing else in CI would
    ///     notice: the suite stays green.
    /// </remarks>
    [Test]
    public async Task ThePlanRunsTheSuiteInMoreThanOneProcess()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(PlanPath()));
        int shards = document.RootElement.GetProperty("shards").GetArrayLength();

        await Assert.That(shards).IsGreaterThan(1)
            .Because("the whole point of the plan is that the classes CANNOT share a process: "
                + "Avalonia permits one Application per AppDomain, the driver sets $HOME (not "
                + "thread-local), and CommonConfig.OverrideHarborHomeForTests pins the Harbor "
                + "home for the whole process. One process means one window, one home, and a "
                + "suite whose wall is the SUM of its class durations. The plan declares "
                + shards + " shard(s).");
    }

    /// <summary>
    ///     The classes of the project this assembly discovers, as the
    ///     generator derives them.
    /// </summary>
    private static IReadOnlyList<string> DiscoverTestClasses()
    {
        var found = new List<string>();

        foreach (string project in AssembledProjects)
        {
            string dir = Path.Combine(RequireRepoRoot(), "tests", project);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory
                         .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                         .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                found.AddRange(ParseTestClasses(file));
            }
        }

        return [.. found.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal)];
    }

    /// <summary>
    ///     The test classes declared in one file.
    /// </summary>
    /// <remarks>
    ///     A type counts when ITS OWN extent declares a <c>[Test]</c>: the
    ///     extent runs from the type's declaration to the next top-level
    ///     declaration. Without that, a helper class in the same file would
    ///     inherit its neighbour's tests, and a file with two suites would
    ///     yield one class instead of two.
    /// </remarks>
    private static IEnumerable<string> ParseTestClasses(string file)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (IOException)
        {
            yield break;
        }

        string[] code = SourceScan.StripComments(string.Join('\n', lines)).Split('\n');
        if (code.Length != lines.Length)
        {
            yield break;
        }

        var declarations = new List<(int Index, string Name)>();
        for (int i = 0; i < code.Length; i++)
        {
            Match match = TypeDeclaration.Match(code[i]);
            if (match.Success)
            {
                declarations.Add((i, match.Groups["name"].Value));
            }
        }

        for (int t = 0; t < declarations.Count; t++)
        {
            int index = declarations[t].Index;
            int end = t + 1 < declarations.Count ? declarations[t + 1].Index : code.Length;

            if (Enumerable.Range(index, Math.Max(0, end - index)).Any(i => TestAttribute.IsMatch(code[i])))
            {
                yield return declarations[t].Name;
            }
        }
    }

    /// <summary>
    ///     The classes the plan names, mapped to the shard that names them.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadPlanClasses()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(PlanPath()));
        var named = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (JsonElement shard in document.RootElement.GetProperty("shards").EnumerateArray())
        {
            string id = shard.GetProperty("id").GetString() ?? "(no id)";
            foreach (JsonElement name in shard.GetProperty("classes").EnumerateArray())
            {
                string? value = name.GetString();
                if (value is not null)
                {
                    named[value] = id;
                }
            }
        }

        return named;
    }

    /// <summary>
    ///     The classes of <paramref name="universe" /> that no shard names.
    /// </summary>
    private static List<string> MissingFromPlan(
        IEnumerable<string> universe,
        IEnumerable<string> planClasses)
    {
        var inPlan = new HashSet<string>(planClasses, StringComparer.Ordinal);
        return [.. universe.Where(name => !inPlan.Contains(name)).OrderBy(p => p, StringComparer.Ordinal)];
    }

    private static string PlanPath() => Path.Combine(RequireRepoRoot(), PlanRelativePath);

    private static string RequireRepoRoot()
        => RepoPaths.RepoRoot ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory + " — this guard reads "
            + PlanRelativePath + " and cannot run from a published test host. Returning quietly "
            + "here would report 'every class is in a shard' without having read the plan at all.");
}
