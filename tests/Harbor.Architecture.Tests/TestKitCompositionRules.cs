// TestKitCompositionRules.cs — GUARD for issue #1889.
//
// WHAT #1889 MEASURED
// --------------------
// `Harbor.TestKit` was ONE project holding ONE file that needs the UI stack
// (`UiTestDoubles.cs` — the dispatcher double and the counting view-model
// stubs) next to ten files that need none of it. The four
// `<ProjectReference>`s that file cost were therefore paid by EVERY consumer
// of TestKit: 11 of the 14 test projects that referenced it never used a
// single double, and each of them dragged eleven UI/Desktop assemblies into
// its build and its test bin.
//
// Measured on origin/dev 34944807 (transitive `<ProjectReference>` closure):
//
//   Harbor.TestKit   19 assemblies, 11 of them UI/Desktop
//   Harbor.Core.Tests 26 assemblies (13 of them dragged in by TestKit alone)
//
// #1889 splits the kit in two:
//
//   Harbor.TestKit      the core — agent fakes, scripted clients, assertions,
//                       message builders, the shared cost gates. Closure: 4.
//   Harbor.TestKit.Tui  the shared UI doubles, the render context and the
//                       panel provider. The ONE project allowed to reach the
//                       UI/Desktop stack on behalf of a test.
//
// The core's closure is FOUR, not three: the fourth is the kit's OWN support
// part, `Harbor.TestKit.Support` (#1772/#1773), which the core references so
// that a consumer of the core reaches the shared helpers transitively. It
// references `Harbor.Abstractions` only, so the core still adds NO product
// assembly to any consumer beyond `Harbor.Abstractions` and
// `Harbor.Abstractions.Contracts` — which is the property this rule exists to
// protect.
//
// WHAT IS RULED
// -------------
//   R1  The CORE's transitive `<ProjectReference>` closure contains no
//       `Harbor.Ui.Framework*`, no `Harbor.Desktop.*`, no
//       `Harbor.Terminal.Abstractions` and no `Harbor.Application`. The edge
//       that used to carry `Harbor.Application` was `Harbor.Desktop.Abstractions`,
//       and it left with the doubles.
//
//   R2  The PARTS are legal. `Harbor.TestKit.Tui` reaches the UI stack on
//       purpose; a rule that forbade the family by name would forbid the
//       arrangement #1889 exists to create. The rule therefore judges the
//       core by its EXACT project name and leaves every `Harbor.TestKit.*`
//       part alone. `The_Part_May_Reach_The_Ui_Stack` proves the exemption
//       is about the rule, not about a part that happens to be thin.
//
//   R3  The forbidden names must still name REAL projects. A rule that names
//       an assembly which no longer exists is green forever and enforces
//       nothing — the NetArchTest trap `EnforcerIntegrityTests` documents.
//
// NON-VACUITY
// -----------
// The closure walk is driven over the REAL csproj XML, so it cannot pass by
// reading nothing, and four controls pin it:
//
//   * `The_Forbidden_Names_Name_Real_Projects` — every forbidden prefix still
//     matches at least one project in the tree, so a rename cannot silently
//     disarm the rule.
//   * `The_Core_Closure_Contains_The_Expected_Projects` — the walk really ran
//     and really found the core's (four-project) closure.
//   * `The_Closure_Walk_Reports_A_Forbidden_Edge_On_A_Synthetic_Graph` — the
//     SAME predicate, handed a graph with one forbidden edge, must report it.
//     A guard that cannot fail is a comment with a test attribute.
//   * `The_Part_May_Reach_The_Ui_Stack` — the negative control: the part's
//     closure DOES contain the forbidden projects, and the rule does not
//     judge it.
//
// WHAT THIS FILE DELIBERATELY DOES NOT DO
// ---------------------------------------
// It does not forbid the UI stack from the test tree as a whole. Eleven test
// projects legitimately sit on the UI stack; what was wrong was the CORE of
// the shared kit dragging it onto everybody else. A rule about "test projects
// must not reference UI" would be green on a tree where the split had never
// happened, because the whole point of the split is that the UI parts of the
// kit still reach the UI stack.
//
// #1875: the SOURCE-GENERATED doubles moved into a part of their own.
//
//   Harbor.TestKit.Generated   the opt-in marker, the call-record type and the
//                              twelve emitted `Fake*` classes. The generator
//                              runs over THIS project, so the doubles are
//                              compiled here and reach a consumer as an
//                              ordinary assembly reference.
//
// The part is judged by the same shape as the core: its closure is exactly
// itself plus the two contract assemblies, and it reaches no UI, Desktop,
// Terminal-Abstractions or Application project. The core does NOT reference it
// — the core's closure stays FOUR, which is what the #1889 rule above pins.

namespace Harbor.Architecture.Tests;

/// <summary>
///     Pins the composition of the composite <c>Harbor.TestKit</c> (#1889): the
///     core stays free of the UI/Desktop stack, and the parts that do reach
///     it are legal.
/// </summary>
public sealed class TestKitCompositionRules
{
    /// <summary>
    ///     The core part of the kit: the project every test project references
    ///     for fakes, assertions and the shared cost gates.
    /// </summary>
    internal const string CorePart = "Harbor.TestKit";

    /// <summary>
    ///     The UI part: the shared dispatcher double, the counting view-model
    ///     stubs, the null render context and the panel provider. It is
    ///     ALLOWED to reach the UI/Desktop stack, and it is the only place in
    ///     the kit that does.
    /// </summary>
    internal const string UiPart = "Harbor.TestKit.Tui";

    /// <summary>
    ///     The generated part (#1875): the opt-in marker, the call-record type
    ///     and the twelve source-generated doubles. The generator runs over this
    ///     project, and nothing else does.
    /// </summary>
    internal const string GeneratedPart = "Harbor.TestKit.Generated";

    /// <summary>
    ///     The Roslyn component that emits the doubles. It is build-time only:
    ///     it reaches a project as an ANALYZER, and the repository's reference
    ///     reader reports an analyzer edge separately from a runtime one.
    /// </summary>
    internal const string GeneratorPart = "Harbor.TestKit.Generators";

    /// <summary>
    ///     The projects the core is judged against. The list is EXACT names,
    ///     not prefixes: a part is a different project with a different
    ///     composition, and judging it by the core's rule would forbid the
    ///     arrangement #1889 creates.
    /// </summary>
    internal static readonly string[] JudgedProjects = [CorePart];

    /// <summary>
    ///     Whether <paramref name="projectName" /> is one of the projects the
    ///     core must not reach — directly or through any number of hops.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The four families are the ones the UI doubles dragged in:
    ///         <c>Harbor.Ui.Framework*</c> (the framework itself and its
    ///         Abstractions/State/Services/ViewModels/Projection/Rendering/
    ///         Sessions siblings), <c>Harbor.Desktop.*</c> (the desktop view-model
    ///         bases, the animation library and the desktop shell),
    ///         <c>Harbor.Terminal.Abstractions</c> (the terminal contracts), and
    ///         <c>Harbor.Application</c> (which
    ///         <c>Harbor.Desktop.ViewModels</c> reached for
    ///         <c>ProviderPresets</c> until #1625 moved the catalogue to
    ///         <c>Harbor.PolicyConfig</c>; since #1899 the desktop view-models
    ///         live in <c>Harbor.Desktop.ViewModels</c> and not in
    ///         <c>Harbor.Desktop.Abstractions</c>, which is the config-schema
    ///         leaf).
    ///     </para>
    ///     <para>
    ///         The two prefix families are matched as PREFIXES on purpose: the
    ///         rule is about a FAMILY reaching the core, not about one
    ///         assembly. <c>Harbor.Ui.Framework.State</c> alone would have
    ///         passed while the sibling <c>Harbor.Ui.Framework.Rendering</c>
    ///         — which is what drags <c>Harbor.Desktop.Animations</c> in —
    ///         walked straight past.
    ///     </para>
    /// </remarks>
    internal static bool IsForbiddenForCore(string projectName) =>
        projectName.StartsWith("Harbor.Ui.Framework", StringComparison.Ordinal)
        || projectName.StartsWith("Harbor.Desktop.", StringComparison.Ordinal)
        || string.Equals(projectName, "Harbor.Terminal.Abstractions", StringComparison.Ordinal)
        || string.Equals(projectName, "Harbor.Application", StringComparison.Ordinal);

    // ---- R1: the core's composition ----------------------------------------

    /// <summary>
    ///     R1 — the core kit reaches no UI, Desktop, Terminal-Abstractions or
    ///     Application project, directly or transitively.
    /// </summary>
    [Test]
    public async Task The_Core_TestKit_Reaches_No_Ui_Desktop_Or_Application_Project()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph = LiveProjectGraph();

        var violations = new List<string>();
        foreach (string project in JudgedProjects)
        {
            foreach (string path in ForbiddenPathsFrom(project, graph))
            {
                violations.Add(path);
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "The core Harbor.TestKit must stay UI-free: every one of its 14 consumers pays for "
                + "what it reaches, and 11 of them never use a UI double. A forbidden project in "
                + "its transitive <ProjectReference> closure is what #1889 removed. Offending "
                + "paths: " + (violations.Count == 0 ? "(none)" : string.Join("; ", violations)));
    }

    // ---- R3 + non-vacuity: the forbidden names are real ----------------------

    /// <summary>
    ///     R3 — every forbidden family still names at least one real project.
    /// </summary>
    /// <remarks>
    ///     A rule that names an assembly which no longer exists is green
    ///     forever and enforces nothing. That is the NetArchTest trap
    ///     <c>EnforcerIntegrityTests</c> documents, and it is the failure mode
    ///     a rename would produce here: <c>Harbor.Ui.Framework</c> split into
    ///     siblings, and a rule that matched only the exact old name would
    ///     stop matching anything at all.
    /// </remarks>
    [Test]
    public async Task The_Forbidden_Names_Name_Real_Projects()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph = LiveProjectGraph();

        string[] families =
        [
            "Harbor.Ui.Framework",
            "Harbor.Desktop.",
            "Harbor.Terminal.Abstractions",
            "Harbor.Application",
        ];

        var empty = new List<string>();
        foreach (string family in families)
        {
            bool found = graph.Keys.Any(project =>
                family.EndsWith(".", StringComparison.Ordinal)
                    ? project.StartsWith(family, StringComparison.Ordinal)
                    : string.Equals(project, family, StringComparison.Ordinal));

            if (!found)
            {
                empty.Add(family);
            }
        }

        await Assert.That(empty).IsEmpty()
            .Because(
                "Every forbidden family must still name a real project, or the rule is a "
                + "constraint on a name that cannot exist and is green forever. Unmatched: "
                + string.Join(", ", empty));
    }

    // ---- non-vacuity: the walk really walked --------------------------------

    /// <summary>
    ///     The core's closure is the four projects #1889 says it is, and the
    ///     walk that found them is the same walk the rule uses.
    /// </summary>
    [Test]
    public async Task The_Core_Closure_Contains_The_Expected_Projects()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph = LiveProjectGraph();

        List<string> closure = [.. ClosureOf(CorePart, graph)];

        await Assert.That(closure).IsEquivalentTo(new[]
        {
            CorePart, "Harbor.TestKit.Support", "Harbor.Abstractions", "Harbor.Abstractions.Contracts",
        }).Because(
            "The core kit is supposed to be exactly four projects wide after #1889 — itself, its "
            + "own support part (#1772/#1773), and the two contract assemblies. A different set "
            + "means the split changed shape (or the walk stopped reading the csprojs), and every "
            + "verdict of R1 was computed over the wrong closure. Found: "
            + string.Join(", ", closure));
    }

    // ---- non-vacuity: the same predicate on a synthetic violation ------------

    /// <summary>
    ///     The predicate R1 uses, handed a graph with one forbidden edge, must
    ///     report it.
    /// </summary>
    /// <remarks>
    ///     This is the control that makes "the rule can fail" mean something.
    ///     Without it, a rule whose matcher had been emptied would pass over
    ///     a clean tree forever, and the reader of the failure message would
    ///     have no way to tell the two apart.
    /// </remarks>
    [Test]
    public async Task The_Closure_Walk_Reports_A_Forbidden_Edge_On_A_Synthetic_Graph()
    {
        var synthetic = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Harbor.TestKit"] = ["Harbor.Abstractions", "Harbor.Ui.Framework.State"],
            ["Harbor.Abstractions"] = [],
            ["Harbor.Ui.Framework.State"] = ["Harbor.Desktop.Animations"],
            ["Harbor.Desktop.Animations"] = [],
        };

        IReadOnlyList<string> reported = ForbiddenPathsFrom(CorePart, synthetic);

        await Assert.That(reported.Count()).IsEqualTo(2)
            .Because(
                "A synthetic core that reaches Harbor.Ui.Framework.State (and, through it, "
                + "Harbor.Desktop.Animations) must be reported on BOTH hops — a rule that only "
                + "looked at the direct edges would miss the second one. Reported: "
                + string.Join("; ", reported));
    }

    /// <summary>
    ///     The synthetic control above must not fire on a graph that is clean,
    ///     or "reports two" would be satisfied by a matcher that reports two
    ///     of anything.
    /// </summary>
    [Test]
    public async Task The_Closure_Walk_Stays_Silent_On_A_Clean_Synthetic_Graph()
    {
        var synthetic = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Harbor.TestKit"] = ["Harbor.Abstractions", "Harbor.Abstractions.Contracts"],
            ["Harbor.Abstractions"] = ["Harbor.Abstractions.Contracts"],
            ["Harbor.Abstractions.Contracts"] = [],
        };

        IReadOnlyList<string> reported = ForbiddenPathsFrom(CorePart, synthetic);

        await Assert.That(reported).IsEmpty()
            .Because("A core that reaches neither the UI stack nor Application is clean. Reported: "
                + string.Join("; ", reported));
    }

    // ---- R2: the parts are legal --------------------------------------------

    /// <summary>
    ///     R2 — the UI part reaches the UI stack ON PURPOSE, and the rule does
    ///     not judge it.
    /// </summary>
    /// <remarks>
    ///     This is the negative control for the whole rule. If the part were
    ///     thin, "the rule does not fire on the part" would be true for the
    ///     wrong reason; the part is fat on purpose, and the rule's exemption
    ///     is what keeps it there.
    /// </remarks>
    [Test]
    public async Task The_Part_May_Reach_The_Ui_Stack()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph = LiveProjectGraph();

        await Assert.That(graph.ContainsKey(UiPart)).IsTrue()
            .Because($"{UiPart} must exist — it is where the shared UI doubles live after #1889.");

        IReadOnlyList<string> closure = ClosureOf(UiPart, graph);

        await Assert.That(closure.Any(IsForbiddenForCore)).IsTrue()
            .Because(
                "The UI part is expected to reach the UI/Desktop stack — that is its whole job. If "
                + "it stopped, the doubles would have moved back into the core and #1889 would have "
                + "been undone. Closure: " + string.Join(", ", closure));

        await Assert.That(JudgedProjects).IsEquivalentTo(new[] { CorePart })
            .Because(
                "The rule judges the core by its exact project name, so a part is never judged by "
                + "the core's rule. Judged: " + string.Join(", ", JudgedProjects));
    }

    // ---- #1875: the generated part ------------------------------------------

    /// <summary>
    ///     The generated part's closure is exactly itself plus the two contract
    ///     assemblies (#1875).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The twelve opted-in interfaces all live in
    ///         <c>Harbor.Abstractions</c> or
    ///         <c>Harbor.Abstractions.Contracts</c>. A part whose closure were
    ///         wider would mean a double had started depending on something
    ///         deeper, and every consumer of the part would pay for it — the
    ///         same mistake #1809 removed from the core.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task The_Generated_Part_Reaches_Only_The_Two_Contract_Assemblies()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph = LiveProjectGraph();

        await Assert.That(graph.ContainsKey(GeneratedPart)).IsTrue()
            .Because(
                $"{GeneratedPart} must exist — it is where the source-generated doubles live "
                + "since #1875.");

        List<string> closure = [.. ClosureOf(GeneratedPart, graph)];

        await Assert.That(closure).IsEquivalentTo(new[]
        {
            GeneratedPart, "Harbor.Abstractions", "Harbor.Abstractions.Contracts",
        }).Because(
            "The generated part is supposed to be exactly three projects wide — itself and the "
            + "two contract assemblies every opted-in interface lives in. A different set means "
            + "the split changed shape (or the walk stopped reading the csprojs). Found: "
            + string.Join(", ", closure));
    }

    /// <summary>
    ///     The generated part reaches no UI, Desktop, Terminal-Abstractions or
    ///     Application project (#1875).
    /// </summary>
    [Test]
    public async Task The_Generated_Part_Reaches_No_Ui_Desktop_Or_Application_Project()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph = LiveProjectGraph();

        IReadOnlyList<string> forbidden = ForbiddenPathsFrom(GeneratedPart, graph);

        await Assert.That(forbidden).IsEmpty()
            .Because(
                "Every one of the twelve opted-in interfaces lives in the two contract "
                + "assemblies, so a double can only ever need those. A forbidden project in the "
                + "generated part's closure is a double that started depending on the UI stack. "
                + "Offending paths: " + (forbidden.Count == 0 ? "(none)" : string.Join("; ", forbidden)));
    }

    /// <summary>
    ///     The core does not reference the generated part, and it does not
    ///     reference the generator at runtime either (#1875).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The core's closure is pinned at four projects by
    ///         <see cref="The_Core_Closure_Contains_The_Expected_Projects" />.
    ///         A reference from the core to the generated part would make it
    ///         five, and every consumer of the core would carry the doubles
    ///         whether it uses them or not — the opposite of what the split is
    ///         for. The two halves of the kit are therefore siblings, not
    ///         parent and child.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task The_Core_Does_Not_Reference_The_Generated_Part()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph = LiveProjectGraph();

        IReadOnlyList<string> coreReferences =
            graph.TryGetValue(CorePart, out IReadOnlyList<string>? declared) && declared is not null
                ? declared
                : Array.Empty<string>();

        await Assert.That(coreReferences.Contains(GeneratedPart)).IsFalse()
            .Because(
                "The core kit is pinned at four projects wide and the generated part would be a "
                + "fifth. The core would also carry the doubles into every one of its consumers, "
                + "whether they use them or not — the opposite of what the split is for. Core "
                + "references: " + string.Join(", ", coreReferences));

        await Assert.That(coreReferences.Contains(GeneratorPart)).IsFalse()
            .Because(
                "The generator is build-time only: it reaches a project as an ANALYZER, and the "
                + "repository's reference reader reports an analyzer edge separately from a "
                + "runtime one. A runtime edge from the core to the generator would put a Roslyn "
                + "component in the core's closure. Core references: "
                + string.Join(", ", coreReferences));
    }

    // ---- the machinery ------------------------------------------------------

    /// <summary>
    ///     The repository's project graph: project directory name to the
    ///     projects it declares a <c>&lt;ProjectReference&gt;</c> to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Read from the csproj XML rather than from the compiled IL,
    ///         because Roslyn DROPS a reference whose types are never bound
    ///         (<c>EnforcerIntegrityTests</c>'s "declared but unbound" table).
    ///         Six of <c>Harbor.Ui.Framework</c>'s seven references bind no
    ///         type at all, so an IL-level walk would report a closure of 3
    ///         for a project that pulls 10 assemblies into the build.
    ///     </para>
    ///     <para>
    ///         Analyzer-only references are excluded: a source generator is not
    ///         a layer dependency and it never lands in the output directory.
    ///     </para>
    /// </remarks>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> LiveProjectGraph()
    {
        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        if (RepoPaths.RepoRoot is not { } root)
        {
            return graph;
        }

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            string normalised = csproj.Replace('\\', '/');
            if (normalised.Contains("/obj/", StringComparison.Ordinal)
                || normalised.Contains("/bin/", StringComparison.Ordinal)
                || normalised.Contains("/.worktrees/", StringComparison.Ordinal))
            {
                continue;
            }

            paths[Path.GetFileNameWithoutExtension(csproj)] = csproj;
        }

        foreach ((string name, string path) in paths)
        {
            graph[name] = RepoPaths.ReadProjectReferences(path).References;
        }

        return graph;
    }

    /// <summary>
    ///     Every project reachable from <paramref name="root" />, including the
    ///     root itself.
    /// </summary>
    internal static IReadOnlyList<string> ClosureOf(
        string root,
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string current = stack.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            if (!graph.TryGetValue(current, out IReadOnlyList<string>? references))
            {
                continue;
            }

            foreach (string reference in references)
            {
                if (!seen.Contains(reference))
                {
                    stack.Push(reference);
                }
            }
        }

        return [.. seen];
    }

    /// <summary>
    ///     The forbidden projects reachable from <paramref name="root" />, each
    ///     as the path that reaches it.
    /// </summary>
    /// <remarks>
    ///     The path is what makes a failure actionable: "Harbor.TestKit
    ///     reaches Harbor.Application" is a fact, and "Harbor.TestKit ->
    ///     Harbor.Desktop.Abstractions -> Harbor.Application" is the edge to
    ///     delete.
    /// </remarks>
    internal static IReadOnlyList<string> ForbiddenPathsFrom(
        string root,
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string project in ClosureOf(root, graph))
        {
            if (!IsForbiddenForCore(project))
            {
                continue;
            }

            IReadOnlyList<string>? path = ShortestPath(root, project, graph);
            paths.Add(path is null ? $"{root} -> {project}" : string.Join(" -> ", path));
        }

        return [.. paths];
    }

    /// <summary>
    ///     The shortest path from <paramref name="from" /> to
    ///     <paramref name="to" /> in the graph, or <see langword="null" /> when
    ///     there is none.
    /// </summary>
    private static IReadOnlyList<string>? ShortestPath(
        string from,
        string to,
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { from };
        var queue = new Queue<string>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (!graph.TryGetValue(current, out IReadOnlyList<string>? references))
            {
                continue;
            }

            foreach (string reference in references)
            {
                if (!visited.Add(reference))
                {
                    continue;
                }

                previous[reference] = current;
                if (string.Equals(reference, to, StringComparison.Ordinal))
                {
                    var path = new List<string> { to };
                    string walker = current;
                    while (!string.Equals(walker, from, StringComparison.Ordinal))
                    {
                        path.Add(walker);
                        walker = previous[walker];
                    }

                    path.Add(from);
                    path.Reverse();
                    return path;
                }

                queue.Enqueue(reference);
            }
        }

        return null;
    }
}
