// TestProjectReferenceRules.cs — the edges BETWEEN test projects, and the two
// costs a wrong edge carries.
//
// THE TWO COSTS
// -------------
// A test project that references another test project pays for that edge
// twice, and neither payment shows up in a build log.
//
// 1. THE CLOSURE. The referenced project drags its own references in with it.
//    Harbor.Ipc.Tests referenced Harbor.E2E.Framework and used NONE of its
//    types; the edge still put Harbor.Application and the whole
//    Harbor.Ui.Framework family into the IPC test project's closure — 23
//    projects where 9 were needed.
//
// 2. THE DISCOVERY. TUnit discovers the test classes of a REFERENCED TEST
//    PROJECT inside every project that references it. So a [Test] method
//    declared in a shared test-support library is not "run once by the
//    library" — it is run once per referencing project.
//
//    Measured on the tree this file landed on: tests/Harbor.E2E.Framework
//    held 26 [Test] methods, and six test projects reference it
//    (Harbor.E2E.App.Avalonia, Harbor.E2E.Cli, Harbor.LoadTests,
//    Harbor.Tui.E2E.Tests, Harbor.Tui.CellForge.PtyTests, Harbor.Ipc.Tests).
//    That is 156 executions of 26 tests per CI run — and on the OS matrix,
//    where test-os.yml also runs Harbor.Ipc.Tests on windows-latest and
//    macos-latest, two more copies each.
//
//    The number "156" is the whole argument for this file. A test that runs
//    six times is not six times as safe; it is six times as slow, six times
//    as flaky, and — worst — six INDEPENDENT verdicts on the same behaviour,
//    so a reader of the CI summary cannot tell a real regression from a
//    flake in one of five copies.
//
// THE TWO RULES
// -------------
// (1) A test project must not reference another test project unless it uses
//     its types. A reference nobody reads is pure closure cost.
//
// (2) A test project that another test project references must not declare
//     test methods at all. This is the discovery half, stated in the shape
//     the defect has: a helper library may be shared, a helper library that
//     also carries tests gets its tests run in every consumer.
//
//     The two rules together say what the E2E framework had to become: the
//     framework holds helpers, and its own tests live in
//     tests/Harbor.E2E.Framework.Tests, a project nothing references.
//
// WHAT IS A "TEST PROJECT" HERE
// -----------------------------
// A project directory under tests/ whose csproj does not say
// <IsTestProject>false</IsTestProject>. That last one is the repository's own
// declaration, and it is honoured: tests/Harbor.TestKit.Generators sets it,
// because it is a Roslyn source generator consumed as an ANALYZER, not a test
// project. That exemption is not a convenience — it is the difference between
// "a test project that references a test project" and "a test project that
// references a build-time component", and the two have different costs.
//
// tests/fixtures and tests/Plugins are DATA, not projects: they have no
// csproj and are skipped by construction.
//
// ANALYZER REFERENCES ARE OUT OF SCOPE
// ------------------------------------
// A ProjectReference with OutputItemType=Analyzer (or
// ReferenceOutputAssembly=false) runs inside the compiler. There is no type
// to name, so rule (1) cannot be applied to it, and there is no assembly in
// the consumer's reference list, so there is nothing for TUnit to discover.
// Harbor.TestKit -> Harbor.TestKit.Generators is exactly this shape, and it
// is load-bearing: the generator emits the test doubles the library's
// consumers use.
//
// HOW "USES ITS TYPES" IS DECIDED
// -------------------------------
// A source-level question, because the arch gate reads the tree and the
// consumer's assembly is not necessarily in this project's bin directory (this
// project does not reference Harbor.E2E.Framework, so its dll is not here to
// be asked about). The answer is YES when any of these holds:
//
//   a) the consumer can see one of the provider's namespaces — explicitly, by
//      a using directive, or implicitly, because the consumer's own namespace
//      IS that namespace or a DESCENDANT of it;
//   b) the consumer writes one of the provider's types fully qualified;
//   c) the consumer writes one of the provider's type names bare, and does
//      not declare a type of that name itself.
//
// Clause (a)'s second half is the C# name-lookup rule, and it is not
// decoration: inside `namespace A.B.C`, the compiler searches A.B.C, then
// A.B, then A, then the global namespace, so a type in an ANCESTOR
// namespace needs no using directive at all. Two of the tree's own
// references depend on it — tests/Harbor.TestKit.Tests sits in namespace
// Harbor.TestKit.Tests and reaches Harbor.TestKit's types without importing
// anything, and tests/Harbor.TestKit and tests/Harbor.TestKit.Support share
// the namespace Harbor.TestKit outright — and a reader that only looked at
// using directives would call both of them dead.
//
// Clause (c)'s last half is not decoration either.
// Harbor.Ui.Framework.Tests declares its OWN TestScalingGate in namespace
// Harbor.Ui.Framework.Tests while Harbor.TestKit declares a TestScalingGate
// in namespace Harbor.TestKit; without the exclusion, the bare name would
// count as a use of the provider's type and the shadowing would hide a dead
// edge.
//
// The rule is deliberately PERMISSIVE in the direction that matters: a
// reported edge is a red build, so every reading that could plausibly mean
// "used" counts as used. A missed edge is a missed cleanup; a false edge is a
// broken build. See KNOWN LIMITATIONS for what "plausibly" excludes.
//
// KNOWN LIMITATIONS — stated, not hidden
// --------------------------------------
// * The scan is line-level over comment-stripped source, not a C# parser.
//   A type used only through a `using static`, a type alias, or a name that
//   never appears in source (an inferred `var`) is not seen by clauses (b)
//   and (c). Clause (a) covers the common case, because a consumer that
//   writes `using Provider;` and then only ever says `var` still imports
//   the namespace.
// * A provider type that is GENERATED rather than written in the provider's
//   own sources is not in the provider's type list, so clause (c) cannot
//   name it. Clause (a) covers it, because the generated namespace is inside
//   the provider's namespace.
// * The discovery claim rests on TUnit's behaviour, which is observed rather
//   than derived: the E2E framework's 26 tests appeared in the TRX of six
//   modules that reference it. This file does not re-derive that; it forbids
//   the SHAPE that produces it, so the claim does not have to be re-measured
//   every time the tree moves.
//
// NON-VACUITY
// -----------
// A rule that matches nothing is indistinguishable from a rule that is
// broken, and a broken rule is worse than none because it is believed. Six
// controls below:
//
//   1. TheScanFindsTheTestToTestEdges — the real tree must contain a
//      non-trivial number of test -> test edges (measured 36), and at least
//      two distinct providers (measured 4). A scanner that stopped matching
//      would otherwise pass vacuously.
//   2. TheUnusedEdgeRuleFiresOnAPlantedUnusedEdge — a synthetic consumer
//      that references a provider and names none of its types MUST be
//      reported. This is the "a synthetic unused reference must be
//      reported" proof.
//   3. TheUnusedEdgeRuleAcceptsAPlantedUsedEdge — a synthetic consumer that
//      DOES name one of the provider's types must NOT be reported. A rule
//      that condemns every edge is not a guard.
//   4. TheDiscoveryRuleFiresOnAPlantedTestBearingLibrary — a referenced
//      provider whose source declares a [Test] method MUST be reported, with
//      the number of modules it is discovered in.
//   5. TheDiscoveryRuleAcceptsAPlantedHelperOnlyLibrary — the same
//      provider without the [Test] method must NOT be reported.
//
// Controls 2-5 drive the SAME predicates the real rules drive, over planted
// inputs, so a matcher that stopped matching cannot pass them.

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Two rules about the edges between test projects: a test project may not
///     reference another test project it does not use, and a test project that
///     another test project references may not carry test methods of its own.
/// </summary>
public sealed class TestProjectReferenceRules
{
    /// <summary>
    ///     A <c>using</c> directive, with or without <c>global</c> and
    ///     <c>static</c>. A type ALIAS (<c>using X = Y;</c>) does not match,
    ///     because the <c>=</c> comes before the <c>;</c>.
    /// </summary>
    private static readonly Regex UsingDirective = new(
        @"^\s*(?:global\s+)?using\s+(?:static\s+)?(?<ns>[A-Za-z_][\w\.]*)\s*;",
        RegexOptions.Compiled);

    /// <summary>
    ///     A namespace declaration, file-scoped or block. The name is captured
    ///     whole so that <c>Harbor.E2E.Framework.Pty</c> is one namespace and
    ///     not two.
    /// </summary>
    private static readonly Regex NamespaceDeclaration = new(
        @"^\s*namespace\s+(?<ns>[A-Za-z_][\w\.]*)\s*[;{]",
        RegexOptions.Compiled);

    /// <summary>
    ///     A type declaration. The modifier run is spelled out rather than
    ///     matched loosely, so that a <c>class</c> token inside an expression
    ///     does not register as a declaration.
    /// </summary>
    private static readonly Regex TypeDeclaration = new(
        @"^\s*(?:(?:public|internal|private|protected|sealed|abstract|static|partial|file|readonly"
        + @"|ref|unsafe|new|extern)\s+)*(?:class|struct|interface|enum)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    /// <summary>
    ///     A record declaration, which may be a bare <c>record</c>, a
    ///     <c>record class</c> or a <c>record struct</c>. It is matched
    ///     separately from <see cref="TypeDeclaration" /> because
    ///     <c>record struct Foo</c> would otherwise capture "struct" as the
    ///     type name.
    /// </summary>
    private static readonly Regex RecordDeclaration = new(
        @"^\s*(?:(?:public|internal|private|protected|sealed|abstract|static|partial|file|readonly"
        + @"|ref|unsafe|new|extern)\s+)*record\s+(?:(?:class|struct)\s+)?(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    /// <summary>
    ///     A TUnit attribute that can only sit on a TEST METHOD. The list is
    ///     the method-level members of TUnit's attribute set: the marker
    ///     itself, the data sources that parameterise it, and the two that
    ///     generate tests.
    /// </summary>
    /// <remarks>
    ///     <c>ClassDataSource</c> is deliberately NOT here: it decorates a
    ///     class or a fixture property, not a test method, and the E2E
    ///     framework's <c>E2eTestBase</c> carries one. A library may hold
    ///     fixtures; what it may not hold is tests.
    /// </remarks>
    private static readonly Regex TestMethodAttribute = new(
        @"(?<=\[|,)\s*(?:Test|Arguments|MethodDataSource|MatrixDataSource|DataSource|Repeat"
        + @"|InheritsTests|DynamicTestBuilder|GenerateGenericTest)\b",
        RegexOptions.Compiled);

    /// <summary>Every identifier in a source file, for the bare-name reading.</summary>
    private static readonly Regex Identifier = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);

    /// <summary>
    ///     Lowest acceptable number of test -&gt; test edges in the real tree.
    ///     Measured 36 on the tree this landed on; the floor sits well below
    ///     that so an ordinary cleanup does not red the build, but a scanner
    ///     that stopped matching does.
    /// </summary>
    private const int MinimumMeasuredEdges = 25;

    /// <summary>
    ///     Lowest acceptable number of DISTINCT providers among those edges.
    ///     Measured 4 (Harbor.TestKit, Harbor.TestKit.Support,
    ///     Harbor.TestKit.Tui and Harbor.E2E.Framework); the floor is 2
    ///     because the point of the check is that the rule is read off more
    ///     than one pair, and a provider coming or going is not a broken
    ///     scanner.
    /// </summary>
    private const int MinimumMeasuredProviders = 2;

    // ---------------------------------------------------------------------
    // Rule 1
    // ---------------------------------------------------------------------

    /// <summary>
    ///     A test project must not reference another test project unless it
    ///     uses its types.
    /// </summary>
    [Test]
    public async Task NoTestProjectReferencesATestProjectItDoesNotUse()
    {
        IReadOnlyList<ProjectFacts> projects = LoadProjects();
        IReadOnlyList<Edge> edges = TestToTestEdges(projects);

        List<string> unused = ReportUnusedEdges(edges);

        await Assert.That(unused).IsEmpty()
            .Because("a test project that references another test project and uses none of its types "
                + "pays for the edge twice: the referenced project's own references arrive in this "
                + "project's closure for nothing, and — because a referenced TEST PROJECT's test "
                + "classes are discovered inside every project that references it — this project also "
                + "becomes a module in which the referenced project's tests run. Harbor.Ipc.Tests "
                + "referenced Harbor.E2E.Framework and used none of it: 23 projects in its closure "
                + "where 9 were needed, and a sixth copy of 26 unrelated tests. Unused edges: "
                + string.Join(" | ", unused));
    }

    // ---------------------------------------------------------------------
    // Rule 2 — the discovery half
    // ---------------------------------------------------------------------

    /// <summary>
    ///     A test project that another test project references must not declare
    ///     test methods: its tests would be discovered in every referring
    ///     module.
    /// </summary>
    [Test]
    public async Task AReferencedTestProjectIsDiscoveredInExactlyOneModule()
    {
        IReadOnlyList<ProjectFacts> projects = LoadProjects();
        IReadOnlyList<Edge> edges = TestToTestEdges(projects);

        List<string> duplicated = ReportDuplicatedProviders(edges);

        await Assert.That(duplicated).IsEmpty()
            .Because("TUnit discovers the test classes of a referenced TEST PROJECT inside every "
                + "project that references it. A shared test-support library that also carries tests "
                + "therefore has its tests run once per consumer: Harbor.E2E.Framework held 26 test "
                + "methods and six test projects referenced it, so CI ran 156 executions of 26 tests, "
                + "and the OS matrix ran two more copies on each of windows-latest and macos-latest. "
                + "The fix is structural — the library keeps its helpers and its tests move to a "
                + "project nothing references (tests/Harbor.E2E.Framework.Tests) — and this rule is "
                + "what keeps the shape. Duplicated: " + string.Join(" | ", duplicated));
    }

    // ---------------------------------------------------------------------
    // Non-vacuity
    // ---------------------------------------------------------------------

    /// <summary>
    ///     The real tree must contain a non-trivial number of test -&gt; test
    ///     edges, and more than one distinct provider.
    /// </summary>
    [Test]
    public async Task TheScanFindsTheTestToTestEdges()
    {
        IReadOnlyList<ProjectFacts> projects = LoadProjects();
        IReadOnlyList<Edge> edges = TestToTestEdges(projects);

        await Assert.That(projects.Count).IsGreaterThan(0)
            .Because("the scan must find the test projects at all; a tree where it finds none makes "
                + "every rule in this file vacuously green. Found: " + projects.Count);

        await Assert.That(edges.Count).IsGreaterThanOrEqualTo(MinimumMeasuredEdges)
            .Because("the tree carries at least " + MinimumMeasuredEdges + " test -> test edges today "
                + "and both rules above are graded over them. Fewer than that means the reader stopped "
                + "matching the project graph — and a rule over an empty subject is a rule that "
                + "reports nothing and is believed. Found: " + edges.Count
                + " (" + string.Join(", ", edges.Select(e => e.Consumer.Name + "->" + e.Provider.Name)) + ")");

        int providers = edges.Select(e => e.Provider.Name).Distinct(StringComparer.Ordinal).Count();

        await Assert.That(providers).IsGreaterThanOrEqualTo(MinimumMeasuredProviders)
            .Because("the edges must span more than one provider, or the rule is being read off a "
                + "single pair and proves nothing about the general shape. Distinct providers: "
                + providers);
    }

    /// <summary>
    ///     The unused-edge rule must fire on a synthetic unused reference.
    /// </summary>
    [Test]
    public async Task TheUnusedEdgeRuleFiresOnAPlantedUnusedEdge()
    {
        ProjectFacts provider = PlantedProvider();
        ProjectFacts consumer = PlantedConsumer("Planted.Consumer", "using System;\n", new[] { "System" }, new[] { "UnrelatedType" });

        await Assert.That(UsesProviderTypes(consumer, provider)).IsFalse()
            .Because("the planted consumer references the provider and names none of its types, so "
                + "the rule must report the edge. A matcher that reported nothing here would also "
                + "report nothing on the real tree, and the green assertion above would not be "
                + "evidence of anything.");

        List<string> reported = ReportUnusedEdges([new Edge(consumer, provider)]);

        await Assert.That(reported.Count).IsEqualTo(1)
            .Because("the planted edge must be reported exactly once, by name. Reported: "
                + string.Join(" | ", reported));
    }

    /// <summary>
    ///     The unused-edge rule must NOT fire on a synthetic edge that does
    ///     use a type of the provider.
    /// </summary>
    [Test]
    public async Task TheUnusedEdgeRuleAcceptsAPlantedUsedEdge()
    {
        ProjectFacts provider = PlantedProvider();
        ProjectFacts consumer = PlantedConsumer(
            "Planted.Consumer",
            "using Harbor.Planted.Provider;\nvar x = new ProvidedType();\n",
            new[] { "Harbor.Planted.Provider" },
            new[] { "LocalType" });

        await Assert.That(UsesProviderTypes(consumer, provider)).IsTrue()
            .Because("the planted consumer imports the provider's namespace, so the rule must accept "
                + "the edge. A rule that condemns every edge is a wall, and a wall gets disabled.");

        List<string> reported = ReportUnusedEdges([new Edge(consumer, provider)]);

        await Assert.That(reported).IsEmpty()
            .Because("a used edge must not be reported. Reported: " + string.Join(" | ", reported));
    }

    /// <summary>
    ///     The discovery rule must fire on a referenced provider that declares
    ///     a test method, and must say how many modules it is discovered in.
    /// </summary>
    [Test]
    public async Task TheDiscoveryRuleFiresOnAPlantedTestBearingLibrary()
    {
        ProjectFacts provider = PlantedProvider(testBearing: true);
        ProjectFacts consumerA = PlantedConsumer("Planted.Consumer.A", "using System;\n", new[] { "System" }, new[] { "LocalType" });
        ProjectFacts consumerB = PlantedConsumer("Planted.Consumer.B", "using System;\n", new[] { "System" }, new[] { "LocalType" });

        List<string> reported = ReportDuplicatedProviders(
            [new Edge(consumerA, provider), new Edge(consumerB, provider)]);

        await Assert.That(reported.Count).IsEqualTo(1)
            .Because("the planted provider declares a test method and two test projects reference it, "
                + "so the rule must report it once. Reported: " + string.Join(" | ", reported));

        await Assert.That(reported[0]).Contains("3 modules")
            .Because("the provider plus its two consumers is three modules, and the message is the "
                + "only place the number is stated. Reported: " + reported[0]);
    }

    /// <summary>
    ///     The discovery rule must NOT fire on a referenced provider that has
    ///     no test methods — the shape the E2E framework has now.
    /// </summary>
    [Test]
    public async Task TheDiscoveryRuleAcceptsAPlantedHelperOnlyLibrary()
    {
        ProjectFacts provider = PlantedProvider(testBearing: false);
        ProjectFacts consumer = PlantedConsumer("Planted.Consumer", "using System;\n", new[] { "System" }, new[] { "LocalType" });

        List<string> reported = ReportDuplicatedProviders([new Edge(consumer, provider)]);

        await Assert.That(reported).IsEmpty()
            .Because("a referenced library with no test methods has no tests to duplicate. Reported: "
                + string.Join(" | ", reported));
    }

    /// <summary>
    ///     A provider in an ANCESTOR namespace of the consumer's is reachable
    ///     without a using directive, so the edge is not a dead one.
    /// </summary>
    /// <remarks>
    ///     This is the C# name-lookup rule, and two of the tree's own
    ///     references depend on it: tests/Harbor.TestKit.Tests sits in
    ///     namespace Harbor.TestKit.Tests and reaches Harbor.TestKit's types
    ///     without importing anything, and tests/Harbor.TestKit and
    ///     tests/Harbor.TestKit.Support share the namespace Harbor.TestKit
    ///     outright. A reader that looked at using directives only would call
    ///     both of them dead.
    /// </remarks>
    [Test]
    public async Task TheAncestorNamespaceCountsAsAVisibleNamespace()
    {
        ProjectFacts provider = PlantedProvider(namespaceName: "Harbor.Planted");
        ProjectFacts consumer = PlantedConsumer(
            "Planted.Consumer",
            "namespace Harbor.Planted.Consumer;\n",
            [],
            new[] { "LocalType" });

        await Assert.That(UsesProviderTypes(consumer, provider)).IsTrue()
            .Because("the consumer sits in namespace Harbor.Planted.Consumer, so the compiler "
                + "searches Harbor.Planted.Consumer, then Harbor.Planted, then Harbor: the "
                + "provider's namespace is reachable with no using directive at all, and the edge "
                + "is not dead. A reader that only looked at using directives would report it and "
                + "break the build on a live reference.");

        List<string> reported = ReportUnusedEdges([new Edge(consumer, provider)]);

        await Assert.That(reported).IsEmpty()
            .Because("the edge must not be reported. Reported: " + string.Join(" | ", reported));
    }

    // ---------------------------------------------------------------------
    // The predicates — shared by the rules and by the controls
    // ---------------------------------------------------------------------

    /// <summary>
    ///     Whether <paramref name="consumer" /> uses any type declared by
    ///     <paramref name="provider" />.
    /// </summary>
    private static bool UsesProviderTypes(ProjectFacts consumer, ProjectFacts provider)
    {
        // (a) an imported namespace of the provider, or one inside it
        foreach (string imported in consumer.ImportedNamespaces)
        {
            foreach (string ns in provider.Namespaces)
            {
                if (imported.Equals(ns, StringComparison.Ordinal)
                    || imported.StartsWith(ns + ".", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        // (b) a fully qualified mention of one of the provider's types
        foreach (string ns in provider.Namespaces)
        {
            foreach (string type in provider.TypeNames)
            {
                if (consumer.Source.Contains(ns + "." + type, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        // (c) a bare mention of one of the provider's type names, in a name the
        //     consumer does not declare itself
        foreach (string type in provider.TypeNames)
        {
            if (!consumer.TypeNames.Contains(type) && consumer.Identifiers.Contains(type))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The sites in a project that declare a test method, for the failure
    ///     message.
    /// </summary>
    private static IReadOnlyList<string> DeclaredTestMethods(ProjectFacts project)
    {
        var sites = new List<string>();
        for (int i = 0; i < project.SourceLines.Count; i++)
        {
            if (TestMethodAttribute.IsMatch(project.SourceLines[i].Text))
            {
                sites.Add(project.SourceLines[i].File + ":" + (project.SourceLines[i].LineNumber + 1));
            }
        }

        return sites;
    }

    /// <summary>The unused-edge findings for a set of edges.</summary>
    private static List<string> ReportUnusedEdges(IReadOnlyList<Edge> edges)
    {
        var unused = new List<string>();
        foreach (Edge edge in edges)
        {
            if (!UsesProviderTypes(edge.Consumer, edge.Provider))
            {
                unused.Add(
                    $"{edge.Consumer.Name} -> {edge.Provider.Name}: the reference names no type of "
                    + $"{edge.Provider.Name}, so it buys nothing but closure");
            }
        }

        return unused;
    }

    /// <summary>The duplicated-discovery findings for a set of edges.</summary>
    private static List<string> ReportDuplicatedProviders(IReadOnlyList<Edge> edges)
    {
        var duplicated = new List<string>();
        foreach (IGrouping<string, Edge> byProvider in edges.GroupBy(e => e.Provider.Name, StringComparer.Ordinal))
        {
            ProjectFacts provider = byProvider.First().Provider;
            IReadOnlyList<string> testSites = DeclaredTestMethods(provider);
            if (testSites.Count == 0)
            {
                continue;
            }

            List<string> modules =
            [
                provider.Name,
                .. byProvider.Select(e => e.Consumer.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)
            ];

            duplicated.Add(
                $"{provider.Name} declares {testSites.Count} test method(s) ({string.Join(", ", testSites)}) "
                + $"and is referenced by {modules.Count - 1} other test project(s), so those tests are "
                + $"discovered in {modules.Count} modules: {string.Join(", ", modules)}");
        }

        return duplicated;
    }

    // ---------------------------------------------------------------------
    // The tree
    // ---------------------------------------------------------------------

    /// <summary>
    ///     Every test project in the tree, with everything the two rules need
    ///     to know about it.
    /// </summary>
    private static IReadOnlyList<ProjectFacts> LoadProjects()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string testsRoot = Path.Combine(root, "tests");
        if (!Directory.Exists(testsRoot))
        {
            return [];
        }

        var projects = new List<ProjectFacts>();
        foreach (string directory in Directory.GetDirectories(testsRoot))
        {
            string[] csprojs = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojs.Length == 0)
            {
                // tests/fixtures and tests/Plugins are data, not projects.
                continue;
            }

            Array.Sort(csprojs, StringComparer.Ordinal);
            projects.Add(ReadProject(root, directory, csprojs[0]));
        }

        return [.. projects.OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    /// <summary>Reads one test project off the disk.</summary>
    private static ProjectFacts ReadProject(string root, string directory, string csprojPath)
    {
        string csproj = File.ReadAllText(csprojPath);

        bool isTestProject = !Regex.IsMatch(
            csproj,
            @"<IsTestProject\s*>\s*false\s*</IsTestProject>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var references = new List<string>();
        foreach (XElement element in XDocument.Load(csprojPath).Descendants()
                     .Where(e => e.Name.LocalName == "ProjectReference"))
        {
            string? include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            bool isAnalyzer = string.Equals(
                                  element.Elements().FirstOrDefault(e => e.Name.LocalName == "OutputItemType")?.Value,
                                  "Analyzer",
                                  StringComparison.OrdinalIgnoreCase)
                              || string.Equals(
                                  element.Attribute("ReferenceOutputAssembly")?.Value,
                                  "false",
                                  StringComparison.OrdinalIgnoreCase);

            if (isAnalyzer)
            {
                // A build-time component, not a project dependency: out of scope.
                continue;
            }

            string resolved = Path.GetFullPath(
                Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar)));
            string resolvedDirectory = Path.GetDirectoryName(resolved) ?? resolved;
            references.Add(Path.GetRelativePath(root, resolvedDirectory).Replace('\\', '/'));
        }

        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        var imported = new HashSet<string>(StringComparer.Ordinal);
        var sourceLines = new List<SourceLine>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);

        foreach (string file in Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
                     .Where(p => !IsBuildOutput(p))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            string[] lines = SourceCommentStripper.StripAll(File.ReadAllLines(file));
            string fileName = Path.GetFileName(file);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                sourceLines.Add(new SourceLine(fileName, i, line));

                Match ns = NamespaceDeclaration.Match(line);
                if (ns.Success)
                {
                    namespaces.Add(ns.Groups["ns"].Value);

                    // The consumer's OWN namespace, and every ancestor of it,
                    // are visible to it without a using directive: inside
                    // `namespace A.B.C` the compiler searches A.B.C, then A.B,
                    // then A, then the global namespace. A provider whose
                    // namespace is one of those is reachable from the consumer
                    // whether or not the consumer imports it by hand, and
                    // treating that as "not used" would call a live reference
                    // dead.
                    foreach (string visible in NamespaceAndAncestors(ns.Groups["ns"].Value))
                    {
                        imported.Add(visible);
                    }
                }

                Match type = TypeDeclaration.Match(line);
                if (type.Success)
                {
                    typeNames.Add(type.Groups["name"].Value);
                }

                Match record = RecordDeclaration.Match(line);
                if (record.Success)
                {
                    typeNames.Add(record.Groups["name"].Value);
                }

                Match used = UsingDirective.Match(line);
                if (used.Success)
                {
                    imported.Add(used.Groups["ns"].Value);
                }

                foreach (Match identifier in Identifier.Matches(line))
                {
                    identifiers.Add(identifier.Value);
                }
            }
        }

        string source = string.Join('\n', sourceLines.Select(l => l.Text));

        string name = Path.GetFileName(directory);
        return new ProjectFacts(
            name,
            isTestProject,
            references,
            namespaces,
            typeNames,
            imported,
            identifiers,
            sourceLines,
            source);
    }

    /// <summary>Build output is not source.</summary>
    private static bool IsBuildOutput(string path)
    {
        string normalised = path.Replace('\\', '/');
        return normalised.Contains("/obj/", StringComparison.Ordinal)
               || normalised.Contains("/bin/", StringComparison.Ordinal);
    }

    /// <summary>
    ///     A namespace and every ancestor of it, from the longest to the
    ///     shortest: <c>Harbor.E2E.Framework.Pty</c> yields
    ///     <c>Harbor.E2E.Framework.Pty</c>, <c>Harbor.E2E.Framework</c>,
    ///     <c>Harbor.E2E</c>, <c>Harbor</c>.
    /// </summary>
    /// <remarks>
    ///     This is the C# name-lookup rule written out: inside
    ///     <c>namespace A.B.C</c>, an unqualified name is searched for in
    ///     A.B.C, then A.B, then A, then the global namespace. A type in an
    ///     ancestor namespace therefore binds with no using directive, which is
    ///     why "the consumer does not import the provider's namespace" is not
    ///     the same question as "the consumer cannot see the provider's
    ///     types".
    /// </remarks>
    private static IEnumerable<string> NamespaceAndAncestors(string ns)
    {
        string current = ns;
        while (!string.IsNullOrEmpty(current))
        {
            yield return current;

            int lastDot = current.LastIndexOf('.');
            current = lastDot < 0 ? string.Empty : current[..lastDot];
        }
    }

    /// <summary>
    ///     The test -&gt; test edges of the tree: an edge is a reference from
    ///     one test project to another.
    /// </summary>
    private static IReadOnlyList<Edge> TestToTestEdges(IReadOnlyList<ProjectFacts> projects)
    {
        var byDirectory = projects.ToDictionary(
            p => "tests/" + p.Name,
            p => p,
            StringComparer.Ordinal);

        var edges = new List<Edge>();
        foreach (ProjectFacts consumer in projects)
        {
            if (!consumer.IsTestProject)
            {
                continue;
            }

            foreach (string reference in consumer.References)
            {
                if (!byDirectory.TryGetValue(reference, out ProjectFacts? provider))
                {
                    continue;
                }

                if (!provider.IsTestProject)
                {
                    continue;
                }

                edges.Add(new Edge(consumer, provider));
            }
        }

        return edges;
    }

    // ---------------------------------------------------------------------
    // The planted fixtures
    // ---------------------------------------------------------------------

    /// <summary>
    ///     A provider with one namespace and one type, optionally carrying a
    ///     test method.
    /// </summary>
    private static ProjectFacts PlantedProvider(bool testBearing = false, string namespaceName = "Harbor.Planted.Provider")
    {
        string[] lines;
        if (testBearing)
        {
            lines =
            [
                $"namespace {namespaceName};",
                "public sealed class ProvidedType { }",
                "public class ProvidedTests",
                "{",
                "    [Test]",
                "    public async Task Something() { }",
                "}"
            ];
        }
        else
        {
            lines =
            [
                $"namespace {namespaceName};",
                "public sealed class ProvidedType { }"
            ];
        }

        var sourceLines = new List<SourceLine>();
        for (int i = 0; i < lines.Length; i++)
        {
            sourceLines.Add(new SourceLine("planted.cs", i, lines[i]));
        }

        return new ProjectFacts(
            "Planted.Provider",
            true,
            [],
            new HashSet<string>(new[] { namespaceName }, StringComparer.Ordinal),
            new HashSet<string>(new[] { "ProvidedType" }, StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            sourceLines,
            string.Join('\n', lines));
    }

    /// <summary>
    ///     A consumer that references the planted provider, with the given
    ///     name, source, imported namespaces and declared type names.
    /// </summary>
    private static ProjectFacts PlantedConsumer(
        string name,
        string source,
        IReadOnlyCollection<string> importedNamespaces,
        IReadOnlyCollection<string> declaredTypeNames,
        string namespaceName = "Harbor.Planted.Consumer")
    {
        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        var sourceLines = new List<SourceLine>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
        {
            sourceLines.Add(new SourceLine("planted.cs", i, lines[i]));
            foreach (Match identifier in Identifier.Matches(lines[i]))
            {
                identifiers.Add(identifier.Value);
            }
        }

        // The consumer's own namespace and its ancestors are visible to it
        // without a using directive, exactly as in ReadProject.
        var visible = new HashSet<string>(importedNamespaces, StringComparer.Ordinal);
        foreach (string ancestor in NamespaceAndAncestors(namespaceName))
        {
            visible.Add(ancestor);
        }

        return new ProjectFacts(
            name,
            true,
            [],
            new HashSet<string>(new[] { namespaceName }, StringComparer.Ordinal),
            new HashSet<string>(declaredTypeNames, StringComparer.Ordinal),
            visible,
            identifiers,
            sourceLines,
            source);
    }

    // ---------------------------------------------------------------------
    // The model
    // ---------------------------------------------------------------------

    /// <summary>One test -&gt; test edge.</summary>
    private sealed record Edge(ProjectFacts Consumer, ProjectFacts Provider);

    /// <summary>One line of a project's source, with the file it came from.</summary>
    private sealed record SourceLine(string File, int LineNumber, string Text);

    /// <summary>Everything the two rules need to know about one project.</summary>
    private sealed record ProjectFacts(
        string Name,
        bool IsTestProject,
        IReadOnlyList<string> References,
        IReadOnlyCollection<string> Namespaces,
        IReadOnlyCollection<string> TypeNames,
        IReadOnlyCollection<string> ImportedNamespaces,
        IReadOnlySet<string> Identifiers,
        IReadOnlyList<SourceLine> SourceLines,
        string Source);
}
