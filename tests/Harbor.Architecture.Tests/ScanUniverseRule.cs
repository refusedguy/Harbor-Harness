// ScanUniverseRule.cs — a MEASUREMENT, not a fix: what is in the reflection
// scan universe of Harbor.Architecture.Tests, and — the part that matters more —
// what is NOT in it.
//
// THE DEFECT
// ----------
// `ArchitectureTestHelpers.LoadHarborAssemblies()` (GlobalUsings.cs) builds its
// dictionary three ways, and not one of them is a product filter. It SEEDS from
// AppDomain.CurrentDomain.GetAssemblies() and keeps anything whose name starts
// with "Harbor" — and this project is named Harbor.Architecture.Tests, so the
// rule's own instrument is in the result, always. It FORCE-LOADS every Harbor*
// reference of this test assembly. And it SWEEPS Harbor*.dll out of
// AppContext.BaseDirectory.
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
//
//     Measured before writing this file: 51 src/ assemblies reachable from the
//     test project, 50 = AllSrcAssemblies exactly, 0 of 52 src csprojs
//     unreachable (Plugins.Host is OutputType=Exe and declared out-of-scope).
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
// measurement, and there is no local dotnet in the authoring environment. The
// first CI run also caught a compile error and a logic error in this file's own
// probe, both fixed in the follow-up commit and recorded here rather than
// quietly amended: the compile error was `.Length` on an `IReadOnlyList`, and
// the logic error was `TreeOf` being asked about an ABSOLUTE path, whose first
// segment is empty — which made every project non-product and the whole
// measurement vacuous. `TreeOf` now takes the root and normalises.
//
// WHAT THE RED RUN MEASURED (run 36823428154, job 110243821618)
//
//     NonProductMembers  Live (1): Harbor.Architecture.Tests
//     BlindProjects      Live (5 trees): apps=2 | contrib=14 | samples=5
//                                      | tests=31 | tools=1        (53 total)
//
// Both baselines above are transcribed verbatim from that log rather than
// recomputed. The three non-vacuity assertions and the positive control passed
// on the same run, which is the only reason the numbers can be read at all:
// without `TheProductSideIsPresent` an empty inventory would have satisfied an
// empty non-product baseline, and without `ThePartitionIsNotTrivial` an empty
// blind set would have satisfied an empty blind baseline.
//
// The 53 is the number that was never written down. Of the 31 under `tests/`,
// every one is invisible to every reflection rule in this project, and the four
// other referrers of `Harbor.Tui.CellForge.Engine` that `CellForgeGraphRules`
// has never seen are among them.

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

    /// <summary>Every <c>*.csproj</c> in the repository, as ABSOLUTE paths.</summary>
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

    /// <summary>
    ///     The ABSOLUTE paths of the <c>&lt;ProjectReference&gt;</c> targets a csproj
    ///     declares, resolved against that csproj's own directory.
    /// </summary>
    /// <remarks>
    ///     Resolved to paths, not to directory names, so the caller can ask which TREE each
    ///     target sits in. A name-keyed map would silently collapse the two
    ///     <c>Harbor.Tui.Spectre.Fullscreen</c>-style names that exist in more than one tree,
    ///     and the wrong one would decide whether an edge counts as a product edge.
    /// </remarks>
    internal static IReadOnlyList<string> ReferencedProjectPaths(string csprojPath)
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

                result.Add(Path.GetFullPath(
                    Path.Combine(projectDir, include.Replace('\\', Path.DirectorySeparatorChar))));
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return [];
        }
    }

    /// <summary>
    ///     The partition predicate, stated once and stated PURELY: an assembly name is
    ///     PRODUCT iff some project under the <c>src</c> tree produces it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the whole boundary. It is deliberately a predicate over a (tree,
    ///         assembly) PAIR and not over a path, not over a manifest, and not over
    ///         <c>FullLayerMatrixTests.AllSrcAssemblies</c>: the tree half is what makes it
    ///         correct, and it is the half a manifest or a hand-maintained name list cannot
    ///         supply. <c>Harbor.Samples.slnx</c> names projects that no longer exist;
    ///         <c>AllSrcAssemblies</c> is a second list somebody has to remember.
    ///     </para>
    ///     <para>
    ///         Taking a pair rather than a path is what lets the positive control exercise
    ///         the predicate with no filesystem at all. The measurement resolves paths to
    ///         pairs before calling this; the control never touches the disk.
    ///     </para>
    /// </remarks>
    internal static HashSet<string> ProductAssemblyNames(IEnumerable<(string Tree, string Assembly)> projects)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string tree, string assembly) in projects)
        {
            if (IsProductTree(tree))
            {
                result.Add(assembly);
            }
        }

        return result;
    }

    /// <summary>The one question this file's partition asks: is this the product tree?</summary>
    internal static bool IsProductTree(string tree) => string.Equals(tree, "src", StringComparison.Ordinal);

    /// <summary>
    ///     The top-level tree an ABSOLUTE path lives under, relative to
    ///     <paramref name="root" />, with forward slashes so the answer does not depend on
    ///     the platform's directory separator.
    /// </summary>
    /// <remarks>
    ///     Normalising matters and was a real bug in the first draft of this file: an
    ///     absolute path such as <c>/home/runner/…/src/X.csproj</c> has an empty first
    ///     segment, so a walk that asked this question of the raw path concluded that no
    ///     project was product and the whole measurement became vacuous — passing on a
    ///     partition that had found nothing.
    /// </remarks>
    internal static string TreeOf(string root, string absolutePath)
    {
        string relative = Path.GetRelativePath(root, absolutePath).Replace('\\', '/');
        int slash = relative.IndexOf('/');
        return slash < 0 ? relative : relative[..slash];
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
    ///     <para>
    ///         Pinned as NAMES, never as a count — the same argument as
    ///         <c>ScanVisibilityRule.MeasuredInvisibleTrees</c>: a count is satisfiable by one
    ///         member leaving while another arrives, and the diff that did it reads as a wash.
    ///     </para>
    ///     <para>
    ///         MEASURED, and the number is 1 — not the several the issue expected. The six
    ///         <c>Harbor.Hosting -> contrib/tui/*</c> references do NOT put contrib assemblies
    ///         in this bin directory, because they sit behind
    ///         <c>Condition="'$(HarborWithSpectreTui)' == 'true'"</c> and no global default
    ///         turns it on (only <c>apps/Harbor.App.Cli</c> sets it, for its own build). So the
    ///         inventory's excess is exactly the instrument and nothing else.
    ///     </para>
    ///     <para>
    ///         That is a much cleaner result than "several foreign members", and it sharpens
    ///         the finding rather than softening it. There is nothing to filter out except
    ///         this assembly, so a shared product-graph accessor would replace exactly one
    ///         `ReferenceEquals` — and the six consumers that already carry a perimeter are
    ///         filtering a single assembly each, in five different ways, for one reason: it is
    ///         the rule's own instrument. The baseline is small; the DECLARATION is the point.
    ///     </para>
    /// </remarks>
    private static readonly string[] MeasuredNonProductMembers = ["Harbor.Architecture.Tests"];

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
    private static readonly string[] MeasuredBlindTrees =
        ["apps=2", "contrib=14", "samples=5", "tests=31", "tools=1"];

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
        if (RepoPaths.RepoRoot is not { } root)
        {
            return new UniverseReport(0, 0, [], [], []) { ProductSideIsPresent = false };
        }

        IReadOnlyList<string> csprojs = ScanUniverseProbe.CsprojPaths();

        // The (tree, assembly) pairs the partition is defined over, and the set of absolute
        // paths that ARE the product graph — the second is what decides whether a declared
        // edge is a product edge, and it is keyed on PATH so two projects that share a
        // directory name in different trees cannot be confused for one another.
        var pairs = new List<(string Tree, string Assembly)>();
        var productPaths = new HashSet<string>(StringComparer.Ordinal);
        var assemblyOfPath = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string csproj in csprojs)
        {
            string tree = ScanUniverseProbe.TreeOf(root, csproj);
            string assembly = ScanUniverseProbe.AssemblyNameOf(csproj);
            pairs.Add((tree, assembly));
            assemblyOfPath[csproj] = assembly;
            if (ScanUniverseProbe.IsProductTree(tree))
            {
                productPaths.Add(csproj);
            }
        }

        HashSet<string> product = ScanUniverseProbe.ProductAssemblyNames(pairs);

        var loaded = ArchitectureTestHelpers.LoadHarborAssemblies();
        var nonProduct = loaded.Keys.Where(name => !product.Contains(name)).Order(StringComparer.Ordinal).ToArray();

        // The blind side: a non-product project that declares a ProjectReference whose
        // TARGET is a src/ project, and whose own assembly the inventory does NOT hold. The
        // instrument is excluded here: it is in the inventory by construction and is
        // measured above, not here.
        var blind = new List<string>();
        foreach (string csproj in csprojs)
        {
            string tree = ScanUniverseProbe.TreeOf(root, csproj);
            if (ScanUniverseProbe.IsProductTree(tree))
            {
                continue;
            }

            string ownAssembly = assemblyOfPath[csproj];
            string dir = Path.GetFileName(Path.GetDirectoryName(csproj)!);

            // The instrument is measured above; counting it here would double-count the
            // one non-product member this project already knows it has.
            if (dir == "Harbor.Architecture.Tests")
            {
                continue;
            }

            bool reachesProduct = ScanUniverseProbe.ReferencedProjectPaths(csproj)
                .Any(target => productPaths.Contains(target));

            if (reachesProduct && !loaded.ContainsKey(ownAssembly))
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
                + "not a cleanup. Live (" + live.Count + "): "
                + (live.Count == 0 ? "(none)" : string.Join(" | ", live))
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
        string[] wrong =
        [
            .. DeclaredPartitionContract
                .Where(row => ScanUniverseProbe.IsProductTree(row.Tree) != row.IsProduct)
                .Select(row => row.Tree + "/" + row.Assembly + " (expected "
                               + (row.IsProduct ? "product" : "non-product") + ", got "
                               + (ScanUniverseProbe.IsProductTree(row.Tree) ? "product" : "non-product") + ")")
        ];

        await Assert.That(string.Join(" | ", wrong))
            .IsEqualTo(string.Empty)
            .Because(
                "the excess measured by NonProductMembers_MatchTheMeasuredBaseline and the blindness measured "
                + "by BlindProjects_MatchTheMeasuredBaseline are both stated as 'what src/ does not produce'. If "
                + "that definition stops holding, both baselines keep passing while measuring a different "
                + "question. The rows below cover both halves of the definition: a src/ project IS product, and the "
                + "SAME assembly name one directory over is NOT — which is the row a `StartsWith(\"Harbor\")` match "
                + "gets wrong, and the whole difference between this predicate and the one LoadHarborAssemblies "
                + "uses. Mismatches: " + (wrong.Length == 0 ? "(none)" : string.Join(" | ", wrong)));
    }

    /// <summary>
    ///     The declared partition, as a table of (tree, assembly) PAIRS. Deliberately
    ///     filesystem-free: this is a control over the PREDICATE, and it must keep
    ///     answering the same way whatever the repository looks like on the day it runs.
    /// </summary>
    private static readonly (string Tree, string Assembly, bool IsProduct)[] DeclaredPartitionContract =
    [
        // A src/ project IS product.
        ("src", "Harbor.Synthetic", true),

        // The SAME assembly name outside src/ is not. This is the row that separates this
        // predicate from a name-prefix match, and the one that matters: the instrument
        // itself, Harbor.Architecture.Tests, is exactly this row.
        ("tests", "Harbor.Architecture.Tests", false),
        ("tests", "Harbor.Synthetic", false),
        ("contrib", "Harbor.Tui.Spectre", false),
        ("samples", "Harbor.Plugin.WebSearch", false),
        ("tools", "Harbor.Evals", false),

        // Every other top-level tree in this repository, so a tree that appears later is a
        // row somebody has to think about rather than one that falls through silently.
        ("apps", "Harbor.App.Cli", false),
        ("analyzers", "Harbor.Synthetic.Analyzer", false),
        ("build", "Harbor.Synthetic.Build", false),
        ("external", "SharpConsoleUI", false),

        // A src/-nested tree is still the product tree: this predicate asks the FIRST
        // segment only, so a project at src/Harbor.X/sub/Y.csproj stays product.
        ("src", "harbor-renamed-by-AssemblyName", true),
    ];

    private static string Describe(IReadOnlyList<string> projects) =>
        projects.Count == 0
            ? "(none)"
            : string.Join(" | ", projects.Take(12)) + (projects.Count > 12 ? $" | ... and {projects.Count - 12} more" : string.Empty);
}