// ScanUniverseRule.cs — a MEASUREMENT, not a fix: what is in the reflection
// scan universe of Harbor.Architecture.Tests, and — the part that matters more —
// what is NOT in it.
//
// THE DEFECT
// ----------
// `ArchitectureTestHelpers.LoadHarborAssemblies()` (GlobalUsings.cs) builds its
// dictionary three ways, and not one of them is a product filter:
//
//   1. seed from `AppDomain.CurrentDomain.GetAssemblies()`, keep anything whose
//      name StartsWith("Harbor") — and this project is named
//      `Harbor.Architecture.Tests`, so the rule's own instrument is in the
//      result, always;
//   2. `Assembly.Load` every `Harbor*` reference of this test assembly;
//   3. sweep `Harbor*.dll` out of `AppContext.BaseDirectory`.
//
// Seventeen files consume it. The universe is "whatever this test bin directory
// happens to hold", which is not a declared perimeter.
//
// #872 (CellForgeGraphRules) and #949 (TuiReadLineContractRules) both hit the
// same wall and each paid for it by hand: the first by hardcoding an assembly
// name, the second by reference identity. Three more files carry a header saying
// their exclusion is load-bearing BECAUSE the perimeter bit them. Five distinct
// exclusion predicates are in the tree today, for one boundary.
//
// WHY MEASURE, AND WHY BASELINE RATHER THAN FIX
// ----------------------------------------------
// The obvious move is to make the inventory the product graph. Measured, that is
// NOT what this file does, and the reason is worth recording because it inverts
// the expectation:
//
//   * THE PRODUCT SIDE IS ALREADY COMPLETE. Every assembly a `src/**/*.csproj`
//     produces is reachable from this test project, and `LayerDependencyTests.
//     AllExpectedHarborAssembliesAreLoaded` pins that it is LOADED. There is no
//     product-side incompleteness to buy, so "convert the 17 consumers" would
//     change no verdict — it would relocate a perimeter that already holds.
//   * THE INCOMPLETENESS IS ON BOTH OTHER SIDES, AND BOTH ARE DECLARABLE. The
//     inventory carries a measured number of NON-PRODUCT members, and misses a
//     measured number of NON-PRODUCT PROJECTS that genuinely reference product
//     code. Neither number is knowable by reading the helper — only by walking.
//
// So this file pins both sides as BASELINES. That is the point: a rule that
// decides on data whose incompleteness is undeclared is the #877 defect class
// one level up, and declaring the boundary is what stops it. It also costs one
// file instead of changing the form of seventeen.
//
// WHAT IS DELIBERATELY NOT DONE HERE
// -----------------------------------
//   * `RepoPaths.FindProjectDir` is a path COMBINER, not a probe: with a checkout
//     it never returns null. Four "no such src project" checks in
//     `EnforcerIntegrityTests` therefore cannot fire. Making it existence-aware
//     is the obvious fix and is NOT attempted — measured, it paints six
//     legitimate `Harbor.Hosting -> contrib/tui/*` rows red
//     (`DeclaredButUnboundProjectReferences`). #949 reached the same conclusion
//     and stopped there for the same reason. Reported, not done.
//   * The five distinct exclusion predicates in the six perimeter-bearing files
//     are not unified. That is a new shared axis for a feature-frozen tree
//     (#555), it changes six files' shape, and this measurement is what tells
//     the owner whether it is worth the price.
//   * `Harbor.Samples.slnx` (11 of 40 rows name deleted projects) and
//     `contrib/Contrib.slnx` (26 of 26) are NOT load-bearing for any tool: no
//     rule reads a `.slnx` row as a project list. `RepoPaths` uses `Harbor.slnx`
//     only as a ROOT MARKER (`File.Exists`) and globs `src/**/*.csproj` off disk
//     for everything else. That is why the product side is complete here while
//     the manifests are not, and it is why this file walks the DISK rather than a
//     manifest. Not this issue's business.
//
// RELATION TO #877 AND #947
// --------------------------
//   * #877 is `SourceScan` — the SOURCE-scan side, whose universe
//     `SourceScan.IsBuildOutput` cuts at `/tests/` and `/contrib/`. Its
//     measurement already merged as `ScanVisibilityRule`. This file is the
//     REFLECTION-scan side, a different helper in a different file, and it does
//     not read `SourceScan` at all. No overlap; the two are twins that answer
//     different questions.
//   * #947 is stale `.cs:NNN` line-number citations in test prose. This file
//     therefore cites SYMBOLS and never line numbers — a `:NNN` in a header here
//     would be born into the class #947 measures.
//
// NON-VACUITY
// -----------
//   1. TheProductSideIsPresent — the loaded inventory came back and every
//      `FullLayerMatrixTests.AllSrcAssemblies` member is in it. Without this, an
//      EMPTY inventory would satisfy both baselines below for the wrong reason.
//   2. ThePartitionIsNotTrivial — the product set is non-empty and the blind
//      side is non-empty, so "one non-product member" and "nothing is blind"
//      cannot both be vacuous.
//   3. ThePartitionPredicateAnswersTheDeclaredQuestion — a positive control over
//      the predicate itself on a fixed table, so the baselines are anchored to a
//      STATED contract rather than to whatever the walk happens to do on the day
//      it is pinned.
//
// This file first shipped with EMPTY baselines, on purpose: the red run is the
// measurement, and there is no local dotnet in the authoring environment.

namespace Harbor.Architecture.Tests;

using System.Xml.Linq;

/// <summary>
///     Walks the repository the way <see cref="ArchitectureTestHelpers.LoadHarborAssemblies" />
///     does NOT: from the disk, with no reliance on a manifest.
/// </summary>
internal static class ScanUniverseProbe
{
    /// <summary>
    ///     Directory names this walk never descends into. Build output, VCS metadata and
    ///     sibling checkouts are not the repository; a measurement that counted them would
    ///     report a number about a tree that is not its subject.
    /// </summary>
    internal static readonly string[] NeverWalked = [".git", ".worktrees", "bin", "obj", "node_modules"];

    /// <summary>Every <c>*.csproj</c> in the repository, as repository-relative paths.</summary>
    internal static IReadOnlyList<string> CsprojPaths()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string current = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(current, "*.csproj");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            found.AddRange(files);

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string directory in directories)
            {
                if (NeverWalked.Contains(Path.GetFileName(directory), StringComparer.Ordinal))
                {
                    continue;
                }

                pending.Push(directory);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    ///     The assembly a csproj produces: its <c>&lt;AssemblyName&gt;</c> if it declares
    ///     one, otherwise the project directory name.
    /// </summary>
    /// <remarks>
    ///     Same default as the compiler's, and deliberately the same shape as
    ///     <c>RepoPaths.ReadAssemblyName</c>. It is re-derived here rather than reached for
    ///     because this walk must not reuse anything the partition is meant to measure.
    /// </remarks>
    internal static string AssemblyNameOf(string csprojPath)
    {
        try
        {
            foreach (XElement element in XDocument.Load(csprojPath, LoadOptions.None).Descendants())
            {
                if (element.Name.LocalName == "AssemblyName" && element.Value.Length > 0)
                {
                    return element.Value.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            // Fall through to the directory-name default.
        }

        return Path.GetFileName(Path.GetDirectoryName(csprojPath)!) is { } dir ? dir : "<unknown>";
    }

    /// <summary>The <c>ProjectReference</c> targets a csproj declares, as project directory names.</summary>
    internal static IReadOnlyList<string> ReferencedProjectDirs(string csprojPath)
    {
        try
        {
            string projectDir = Path.GetDirectoryName(csprojPath)!;
            var result = new List<string>();

            foreach (XElement element in XDocument.Load(csprojPath, LoadOptions.None).Descendants())
            {
                if (element.Name.LocalName != "ProjectReference")
                {
                    continue;
                }

                string? include = element.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                string resolved = Path.GetFullPath(
                    Path.Combine(projectDir, include.Replace('\\', Path.DirectorySeparatorChar)));
                result.Add(Path.GetFileName(resolved));
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return [];
        }
    }

    /// <summary>
    ///     The partition predicate, stated once: an assembly name is PRODUCT iff some
    ///     <c>src/**/*.csproj</c> produces it.
    /// </summary>
    /// <remarks>
    ///     This is the whole boundary, and it is deliberately a definition over the DISK
    ///     rather than over a manifest or over <c>FullLayerMatrixTests.AllSrcAssemblies</c>.
    ///     A hand-maintained name list would be a second thing to forget, and a manifest
    ///     would be a thing already known to be unreconciled (<c>Harbor.Samples.slnx</c>).
    /// </remarks>
    internal static HashSet<string> ProductAssemblyNames(IReadOnlyList<string> csprojs)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string csproj in csprojs)
        {
            if (TreeOf(csproj) == "src")
            {
                result.Add(AssemblyNameOf(csproj));
            }
        }

        return result;
    }

    /// <summary>The top-level tree a repository-relative path lives under.</summary>
    internal static string TreeOf(string relativePath)
    {
        int slash = relativePath.IndexOf('/');
        return slash < 0 ? relativePath : relativePath[..slash];
    }
}

/// <summary>
///     Pins what the reflection scan universe of this project CONTAINS and what it CANNOT
///     SEE. Both baselines ship empty on purpose — the red run is the measurement.
///     See the file header for why this is a measurement rather than a fix.
/// </summary>
public sealed class ScanUniverseRule
{
    private static readonly Lazy<UniverseReport> Report = new(Measure);

    /// <summary>Assembly names in the inventory that no <c>src/</c> project produces.</summary>
    /// <remarks>
    ///     Pinned as NAMES, never as a count — the same argument as
    ///     <c>ScanVisibilityRule.MeasuredInvisibleTrees</c>: a count is satisfiable by one
    ///     member leaving while another arrives, and the diff that did it reads as a wash.
    /// </remarks>
    private static readonly string[] MeasuredNonProductMembers = [];

    /// <summary>
    ///     Non-product projects that declare a <c>&lt;ProjectReference&gt;</c> into
    ///     <c>src/</c> and are therefore invisible to the inventory, counted per top-level
    ///     tree. The format is <c>tree=count</c>, ordinal-sorted.
    /// </summary>
    /// <remarks>
    ///     This is the half that has never been written down anywhere. The inventory is not
    ///     merely polluted with its own instrument; it also cannot see whole projects that
    ///     really do reference product code, which is why a rule built on it once
    ///     accused the instrument and, in the same breath, could not see five other real
    ///     referrers of the assembly it was grading.
    /// </remarks>
    private static readonly string[] MeasuredBlindTrees = [];

    /// <summary>What the two walks found, once.</summary>
    private sealed record UniverseReport(
        int ProductAssemblyCount,
        int LoadedCount,
        IReadOnlyList<string> NonProductMembers,
        IReadOnlyList<string> BlindTrees,
        IReadOnlyList<string> BlindProjects)
    {
        /// <summary>The inventory came back and holds what the product set declares.</summary>
        internal bool ProductSideIsPresent { get; init; }
    }

    private static UniverseReport Measure()
    {
        if (RepoPaths.RepoRoot is null)
        {
            return new UniverseReport(0, 0, [], [], []) { ProductSideIsPresent = false };
        }

        IReadOnlyList<string> csprojs = ScanUniverseProbe.CsprojPaths();
        HashSet<string> product = ScanUniverseProbe.ProductAssemblyNames(csprojs);

        // Directory name -> the assembly it produces, for resolving ProjectReference targets.
        var assemblyOfDir = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string csproj in csprojs)
        {
            string dir = Path.GetFileName(Path.GetDirectoryName(csproj)!);
            assemblyOfDir[dir] = ScanUniverseProbe.AssemblyNameOf(csproj);
        }

        var loaded = ArchitectureTestHelpers.LoadHarborAssemblies();
        var nonProduct = loaded.Keys.Where(name => !product.Contains(name)).Order(StringComparer.Ordinal).ToArray();

        // The blind side: a non-product project that declares a ProjectReference into a
        // directory some src/ project occupies, and whose own assembly the inventory does
        // NOT hold. The instrument is excluded from the walk: it is in the inventory by
        // construction and is measured above, not here.
        var blind = new List<string>();
        foreach (string csproj in csprojs)
        {
            string dir = Path.GetFileName(Path.GetDirectoryName(csproj)!);
            string tree = ScanUniverseProbe.TreeOf(Path.GetRelativePath(RepoPaths.RepoRoot, csproj));
            if (tree == "src" || dir == "Harbor.Architecture.Tests")
            {
                continue;
            }

            bool reachesProduct = ScanUniverseProbe.ReferencedProjectDirs(csproj)
                .Any(target => assemblyOfDir.TryGetValue(target, out string? a) && product.Contains(a));

            if (reachesProduct && !loaded.ContainsKey(assemblyOfDir[dir]))
            {
                blind.Add(tree + "/" + dir);
            }
        }

        blind.Sort(StringComparer.Ordinal);

        var byTree = blind
            .GroupBy(m => m[..m.IndexOf('/')], StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key + "=" + g.Count())
            .ToArray();

        bool complete = FullLayerMatrixTests.AllSrcAssemblies.All(name => loaded.ContainsKey(name));

        return new UniverseReport(product.Count, loaded.Count, nonProduct, byTree, blind)
        {
            ProductSideIsPresent = loaded.Count > 0 && complete,
        };
    }

    // =====================================================================
    // 1. The measurement.
    // =====================================================================

    /// <summary>
    ///     The non-product members of <c>LoadHarborAssemblies()</c> must equal
    ///     <see cref="MeasuredNonProductMembers" />.
    /// </summary>
    /// <remarks>
    ///     This is the inventory's excess, and it is what #872 and #949 each had to work
    ///     around by hand. When this fails the message IS the measurement, which is why the
    ///     baseline shipped empty: no amount of reading the helper by eye puts a name here.
    /// </remarks>
    [Test]
    public async Task NonProductMembers_MatchTheMeasuredBaseline()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("this rule measures the inventory of a checkout; without one the inventory "
                   + "would answer for the wrong reason rather than report a defect");

        var report = Report.Value;
        var live = report.NonProductMembers;

        await Assert.That(string.Join(" | ", live))
            .IsEqualTo(string.Join(" | ", MeasuredNonProductMembers))
            .Because(
                "LoadHarborAssemblies() builds its dictionary from AppDomain.GetAssemblies() plus a "
                + "bin-directory sweep of Harbor*.dll, and neither is a product filter. Whatever is in "
                + "the inventory that no src/ project produces is what every consumer of this helper "
                + "grades unless it carries its own perimeter — and there are five different ones in "
                + "the tree, each added by a rule that was bitten. A NEW non-product member means a "
                + "reference or a copy-local arrived that no rule has agreed how to treat; an EMPTY "
                + "member list means the filter grew a product filter and that is a reviewed change, "
                + "not a cleanup. Live (" + live.Length + "): "
                + (live.Length == 0 ? "(none)" : string.Join(" | ", live))
                + " | baseline held " + MeasuredNonProductMembers.Length + ": "
                + (MeasuredNonProductMembers.Length == 0 ? "(empty)" : string.Join(" | ", MeasuredNonProductMembers)));
    }

    /// <summary>
    ///     The non-product projects the inventory CANNOT see must equal
    ///     <see cref="MeasuredBlindTrees" />.
    /// </summary>
    /// <remarks>
    ///     The complement of the test above, and the half with no declaration anywhere in the
    ///     tree. It is not a defect in the helper — an assembly that was never built cannot be
    ///     loaded — it is a fact about what these rules may conclude, and an undeclared fact of
    ///     that kind is the defect. Pinned per top-level tree so a change is a name somebody
    ///     has to edit on purpose rather than a number that drifts.
    /// </remarks>
    [Test]
    public async Task BlindProjects_MatchTheMeasuredBaseline()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("without a checkout there are no projects to be blind to");

        var report = Report.Value;
        var live = report.BlindTrees;

        await Assert.That(string.Join(" | ", live))
            .IsEqualTo(string.Join(" | ", MeasuredBlindTrees))
            .Because(
                "these are the non-product projects that declare a ProjectReference into src/ and are "
                + "not in the inventory, so no rule built on LoadHarborAssemblies() can see them. A rule "
                + "that says \"only these assemblies may reference X\" is making a statement about the "
                + "product graph and is silently making it about the bin directory instead: CellForgeGraphRules "
                + "graded Harbor.Architecture.Tests as a leak while five other referrers of the same assembly "
                + "were outside its reach. A tree APPEARING here is a new project whose edges no reflection rule "
                + "covers; a tree DISAPPEARING is either a deletion or a project that became loadable, and both "
                + "are worth reading before the baseline is edited. Live (" + live.Count + " tree(s)): "
                + (live.Count == 0 ? "(none)" : string.Join(" | ", live))
                + " | baseline held " + MeasuredBlindTrees.Length + ": "
                + (MeasuredBlindTrees.Length == 0 ? "(empty)" : string.Join(" | ", MeasuredBlindTrees))
                + " | projects: " + Describe(report.BlindProjects));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The inventory came back, and holds every assembly the product set declares.
    /// </summary>
    /// <remarks>
    ///     Load-bearing for both baselines: an empty inventory satisfies an empty
    ///     non-product list and an empty blind list simultaneously, so without this the file
    ///     would pass on a helper that loads nothing at all.
    /// </remarks>
    [Test]
    public async Task TheProductSideIsPresent()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the product side is half the measurement; without a checkout there is nothing to load");

        var report = Report.Value;
        await Assert.That(report.LoadedCount).IsGreaterThan(0)
            .Because(
                "if the inventory is empty then the non-product baseline is holding against a helper that "
                + "loads nothing, and 'the universe contains nothing foreign' and 'the universe is empty' "
                + "become the same report. Product assemblies on disk: " + report.ProductAssemblyCount
                + ", loaded: " + report.LoadedCount);

        await Assert.That(report.ProductSideIsPresent).IsTrue()
            .Because(
                "every name in FullLayerMatrixTests.AllSrcAssemblies must be loadable, because that is what "
                + "makes the PRODUCT half of this rule's partition trustworthy — the excess measured above is "
                + "only meaningful if the rest is really there. A name missing here means a ProjectReference "
                + "was dropped from this test project's csproj and every reflection rule over that assembly "
                + "has quietly stopped judging it.");
    }

    /// <summary>
    ///     The partition is not trivial: the product set is non-empty and the blind side is
    ///     non-empty, so neither baseline can be holding for the boring reason.
    /// </summary>
    [Test]
    public async Task ThePartitionIsNotTrivial()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("a trivial partition in a non-checkout measures nothing and must not look like a pass");

        var report = Report.Value;
        await Assert.That(report.ProductAssemblyCount).IsGreaterThan(0)
            .Because("no src/**/*.csproj was found, so 'is product?' has no referent and both partitions "
                   + "are vacuous");

        await Assert.That(report.BlindProjects.Count).IsGreaterThan(0)
            .Because(
                "if nothing is blind, the universe has become the product graph and this file has stopped "
                + "measuring the thing it was written for. Either every non-product project that references "
                + "product code is now loadable — a real change worth reading — or the walk that finds them "
                + "has broken. A non-empty product set with an empty blind set is also the shape a filter "
                + "regression takes, so this is the assertion that would notice one. Blind: "
                + Describe(report.BlindProjects));
    }

    // =====================================================================
    // 3. The positive control.
    // =====================================================================

    /// <summary>
    ///     The partition predicate answers the question it declares, on a fixed table.
    /// </summary>
    /// <remarks>
    ///     The baselines above are pinned against whatever the walk happens to do on the day
    ///     it is pinned. That is only meaningful while the predicate still answers the
    ///     STATED question: "an assembly name is product iff some src/**/*.csproj produces
    ///     it". If it later regressed to, say, matching on the `Harbor` prefix — which is
    ///     what `LoadHarborAssemblies` itself does — then this control fails first, and the
    ///     failure arrives as a reviewed change rather than as two baselines quietly meaning
    ///     something else.
    /// </remarks>
    [Test]
    public async Task ThePartitionPredicateAnswersTheDeclaredQuestion()
    {
        var wrong = DeclaredPartitionContract
            .Where(pair => ScanUniverseProbe.ProductAssemblyNames(pair.Csprojs)
                               .Contains(pair.Assembly, StringComparer.Ordinal) != pair.IsProduct)
            .Select(pair => pair.Assembly + " (expected "
                               + (pair.IsProduct ? "product" : "non-product") + ", got "
                               + (ScanUniverseProbe.ProductAssemblyNames(pair.Csprojs)
                                          .Contains(pair.Assembly, StringComparer.Ordinal)
                                      ? "product"
                                      : "non-product") + ")")
            .ToArray();

        await Assert.That(string.Join(" | ", wrong))
            .IsEqualTo(string.Empty)
            .Because(
                "the excess measured by NonProductMembers_MatchTheMeasuredBaseline and the blindness measured "
                + "by BlindProjects_MatchTheMeasuredBaseline are both stated as 'what src/ does not produce'. If "
                + "that definition stops holding, both baselines keep passing while measuring a different "
                + "question. The rows below cover each side of the definition: a src/ project IS product, a "
                + "non-src/ project with the SAME assembly name is NOT (the src/-by-location half, which is the "
                + "one a prefix match would get wrong), and a name no project produces is not. Mismatches: "
                + (wrong.Length == 0 ? "(none)" : string.Join(" | ", wrong)));
    }

    /// <summary>
    ///     The declared partition, as a table. Paths are synthetic: this is a control over
    ///     the PREDICATE, and it must not depend on the repository it runs in.
    /// </summary>
    private static readonly (string Assembly, bool IsProduct, string[] Csprojs)[] DeclaredPartitionContract =
    [
        // A src/ project IS product.
        ("Harbor.Synthetic", true, ["src/Harbor.Synthetic/Harbor.Synthetic.csproj"]),

        // The SAME assembly name one directory over, NOT under src/, is not. This is the
        // row a prefix or name match gets wrong, and it is the whole difference between
        // this predicate and the `name.StartsWith("Harbor")` test LoadHarborAssemblies uses.
        ("Harbor.Synthetic", false, ["tests/Harbor.Synthetic/Harbor.Synthetic.csproj"]),
        ("Harbor.Synthetic", false, ["contrib/tui/Harbor.Synthetic/Harbor.Synthetic.csproj"]),
        ("Harbor.Synthetic", false, ["samples/plugins/Harbor.Synthetic/Harbor.Synthetic.csproj"]),

        // A name no project in the set produces is not product either — so "not in the
        // product set" never quietly becomes "is a product" for an unknown name.
        ("Harbor.Synthetic", false, []),
    ];

    private static string Describe(IReadOnlyList<string> projects) =>
        projects.Count == 0
            ? "(none)"
            : string.Join(" | ", projects.Take(12)) + (projects.Count > 12 ? $" | ... and {projects.Count - 12} more" : string.Empty);
}