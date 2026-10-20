// ContribIsolationRules.cs — GUARD for the `contrib/` boundary.
//
// THE CONVENTION (AGENTS.md)
// --------------------------
// `contrib/` is unmaintained and OUTSIDE CI: no solution CI builds lists a row
// in it, and no project under `src/` or `apps/` may reach into it. The
// direction that IS allowed is `contrib/` -> `src/` — a contributed renderer
// uses Harbor's abstractions, and every project under `contrib/` does exactly
// that. The direction that is NOT allowed is `src/`/`apps/` -> `contrib/`,
// because that is what puts unmaintained code into the product's build graph.
//
// WHY NO RULE ENFORCED IT UNTIL NOW
// ---------------------------------
// The convention was prose in AGENTS.md and nothing measured it. A convention
// that is only prose is a convention that erodes one edit at a time, and the
// shape it erodes into is quiet: a `ProjectReference` from `src/` into
// `contrib/` compiles an entire unmaintained project into the product, and
// the only symptom is that the Release build got slower and that a file
// nobody reads started failing.
//
// This rule exists because of a concrete move, not because the convention was
// abstract: the vendored SharpConsoleUI renderer (and the third-party tree it
// wraps) left `src/` for `contrib/`, and the invariant that move established —
// the product does not compile the vendored tree — is worth holding in place
// mechanically rather than in prose.
//
// THE THREE CLAIMS
// ----------------
//   1. A `ProjectReference` from `src/` or `apps/` into `contrib/` must be
//      CONDITIONAL — i.e. it must sit inside a conditioned ItemGroup or carry
//      its own `Condition`. The unconditional shape is a violation.
//
//      The conditional shape is not a loophole: the one live example is the
//      Spectre family in `src/Harbor.Hosting/Harbor.Hosting.csproj`, gated on
//      `$(HarborWithSpectreTui)`, which the root `Directory.Build.props` does
//      NOT define — so the default build compiles none of it, and the six
//      renderer factories behind `#if HARBOR_WITH_SPECTRE_TUI` never open.
//      (The CLI's own copy of that block is commented out.) A reference that
//      is unconditional has no such story: it is in the graph, always.
//
//   2. The VENDORED tree (`contrib/external/`) may not be referenced from
//      `src/` or `apps/` AT ALL — not even conditionally. That tree is
//      third-party source under a moving pin, and the boundary props beside it
//      exist precisely so the root's CPM and warning policy do not reach it.
//      A product that referenced it would compile it, which is the thing the
//      move was made to stop.
//
//   3. `Harbor.slnx` — the solution CI builds — must not carry a row under
//      `contrib/`. `Harbor.Samples.slnx` DOES carry such rows and is not a
//      counter-example: no workflow builds it, which is why it is not the
//      solution this claim is about.
//
// NON-VACUITY
// -----------
//   1. `The_Walk_Reads_Projects_And_References` — the walk really found
//      product projects and really parsed `ProjectReference` elements out of
//      them. Without it, "no violations" and "read nothing" are the same
//      green.
//   2. `The_Rule_Fires_On_A_Synthetic_Src_To_Contrib_Reference` — the POSITIVE
//      CONTROL, and it drives the REAL matcher (the same resolution and the
//      same classification) over three planted csproj bodies: an unconditional
//      `src/` -> `contrib/` reference must be reported, a conditional one must
//      not, and a conditional reference into the vendored tree must be
//      reported anyway. The control is hermetic — no file is written, the
//      planted XML is handed to the same function the live scan uses.

using System.Xml.Linq;

namespace Harbor.Architecture.Tests;

/// <summary>One <c>src/</c>/<c>apps/</c> -&gt; <c>contrib/</c> edge found in a csproj.</summary>
/// <param name="From">Repo-relative path of the declaring project.</param>
/// <param name="To">Repo-relative path of the referenced project.</param>
/// <param name="Conditional">Whether the edge sits behind a MSBuild condition.</param>
/// <param name="Vendored">Whether the target is inside the vendored third-party tree.</param>
internal sealed record ContribEdge(string From, string To, bool Conditional, bool Vendored);

/// <summary>Everything the live scan measured, so a failure message can carry the evidence.</summary>
/// <param name="Edges">Every <c>src/</c>/<c>apps/</c> -&gt; <c>contrib/</c> edge found.</param>
/// <param name="Violations">The subset that breaks the convention.</param>
/// <param name="ProjectsRead">How many product csproj files were parsed.</param>
/// <param name="ReferencesRead">How many <c>ProjectReference</c> elements were seen.</param>
/// <param name="SolutionRowsInContrib">Rows of <c>Harbor.slnx</c> that point into <c>contrib/</c>.</param>
internal sealed record ContribScanReport(
    IReadOnlyList<ContribEdge> Edges,
    IReadOnlyList<ContribEdge> Violations,
    int ProjectsRead,
    int ReferencesRead,
    IReadOnlyList<string> SolutionRowsInContrib);

/// <summary>Reads the <c>src/</c>/<c>apps/</c> -&gt; <c>contrib/</c> edges out of csproj XML.</summary>
internal static class ContribIsolationProbe
{
    /// <summary>Repo-relative root of the unbuilt tree.</summary>
    internal const string ContribRoot = "contrib";

    /// <summary>Repo-relative root of the vendored third-party tree inside it.</summary>
    internal const string VendoredRoot = "contrib/external";

    /// <summary>The trees whose projects may not reach into <c>contrib/</c>.</summary>
    internal static readonly string[] ProductTrees = ["src", "apps"];

    /// <summary>
    ///     Walks the product trees and reads every csproj.
    /// </summary>
    /// <param name="repoRoot">Repository root, or <c>null</c> outside a checkout.</param>
    internal static ContribScanReport Scan(string? repoRoot)
    {
        var edges = new List<ContribEdge>();
        int projectsRead = 0;
        int referencesRead = 0;

        if (repoRoot is not null)
        {
            foreach (string tree in ProductTrees)
            {
                string absolute = Path.Combine(repoRoot, tree);
                if (!Directory.Exists(absolute))
                {
                    continue;
                }

                foreach (string csproj in Directory.GetFiles(absolute, "*.csproj", SearchOption.AllDirectories))
                {
                    if (SourceScan.IsBuildOutput(csproj))
                    {
                        continue;
                    }

                    string relative = Path.GetRelativePath(repoRoot, csproj).Replace('\\', '/');
                    string xml;
                    try
                    {
                        xml = File.ReadAllText(csproj);
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    projectsRead++;
                    IReadOnlyList<ContribEdge> found = EdgesIn(relative, xml, out int read);
                    referencesRead += read;
                    edges.AddRange(found);
                }
            }
        }

        IReadOnlyList<ContribEdge> violations =
            [.. edges.Where(static e => !e.Conditional || e.Vendored)];

        return new ContribScanReport(
            edges,
            violations,
            projectsRead,
            referencesRead,
            SolutionRowsInContrib(repoRoot));
    }

    /// <summary>
    ///     The matcher: every <c>ProjectReference</c> in one csproj body that points into
    ///     <c>contrib/</c>, classified.
    /// </summary>
    /// <remarks>
    ///     Exposed so the positive control drives the REAL function rather than a second
    ///     implementation of it — which is the only way "it can fail" means anything. The
    ///     resolution is LEXICAL (relative to the declaring project's directory), so the
    ///     control needs no filesystem at all and cannot be satisfied by a directory that
    ///     happens to exist.
    /// </remarks>
    /// <param name="projectRelativePath">Repo-relative path of the declaring csproj.</param>
    /// <param name="projectXml">The csproj body.</param>
    /// <param name="referencesRead">How many <c>ProjectReference</c> elements were seen.</param>
    internal static IReadOnlyList<ContribEdge> EdgesIn(
        string projectRelativePath,
        string projectXml,
        out int referencesRead)
    {
        referencesRead = 0;
        var edges = new List<ContribEdge>();

        XDocument document;
        try
        {
            document = XDocument.Parse(projectXml);
        }
        catch (System.Xml.XmlException)
        {
            return edges;
        }

        string projectDir = Path.GetDirectoryName(projectRelativePath.Replace('\\', '/'))?.Replace('\\', '/')
                            ?? string.Empty;

        foreach (XElement reference in document.Descendants().Where(static e => e.Name.LocalName == "ProjectReference"))
        {
            referencesRead++;
            string? include = reference.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            string? target = Resolve(projectDir, include);
            if (target is null || !IsUnder(target, ContribRoot))
            {
                continue;
            }

            edges.Add(new ContribEdge(
                projectRelativePath.Replace('\\', '/'),
                target,
                IsConditional(reference),
                IsUnder(target, VendoredRoot)));
        }

        return edges;
    }

    /// <summary>
    ///     The rows of <c>Harbor.slnx</c> that point into <c>contrib/</c>.
    /// </summary>
    /// <param name="repoRoot">Repository root, or <c>null</c> outside a checkout.</param>
    internal static IReadOnlyList<string> SolutionRowsInContrib(string? repoRoot)
    {
        if (repoRoot is null)
        {
            return [];
        }

        string solution = Path.Combine(repoRoot, "Harbor.slnx");
        if (!File.Exists(solution))
        {
            return [];
        }

        XDocument document;
        try
        {
            document = XDocument.Load(solution);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        return
        [
            .. document.Descendants()
                .Where(static e => e.Name.LocalName == "Project")
                .Select(static e => e.Attribute("Path")?.Value)
                .Where(static v => !string.IsNullOrWhiteSpace(v))
                .Select(static v => v!.Replace('\\', '/'))
                .Where(static v => IsUnder(v, ContribRoot))
        ];
    }

    /// <summary>
    ///     Whether a reference sits behind a MSBuild condition — its own, or one inherited
    ///     from the ItemGroup that holds it.
    /// </summary>
    /// <param name="reference">The <c>ProjectReference</c> element.</param>
    internal static bool IsConditional(XElement reference)
    {
        if (reference.Attribute("Condition") is not null)
        {
            return true;
        }

        for (XElement? parent = reference.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.Attribute("Condition") is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Resolves a <c>ProjectReference</c> include against the declaring project's
    ///     directory, lexically.
    /// </summary>
    /// <remarks>
    ///     Lexical on purpose: the answer must not depend on whether the target happens to
    ///     exist on disk, because the interesting case is a reference to a tree that is not
    ///     checked out. Returns <c>null</c> for an absolute include, which MSBuild does not
    ///     use for project references.
    /// </remarks>
    /// <param name="projectDir">Repo-relative directory of the declaring project.</param>
    /// <param name="include">The <c>Include</c> attribute value.</param>
    internal static string? Resolve(string projectDir, string include)
    {
        string normalized = include.Replace('\\', '/').Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        // An absolute include is not a project reference in the MSBuild sense.
        if (normalized.StartsWith('/') || (normalized.Length > 1 && normalized[1] == ':'))
        {
            return null;
        }

        var segments = new List<string>();
        foreach (string segment in (projectDir + "/" + normalized).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    /// <summary>Whether a repo-relative path is inside a repo-relative root.</summary>
    /// <param name="path">Repo-relative path.</param>
    /// <param name="root">Repo-relative root directory.</param>
    internal static bool IsUnder(string path, string root)
    {
        string normalizedRoot = root.Replace('\\', '/').TrimEnd('/');
        return string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
///     Holds the <c>src/</c>/<c>apps/</c> -&gt; <c>contrib/</c> boundary: the product must not
///     reach into the unbuilt tree, the vendored third-party tree must not be in the product
///     graph at all, and the solution CI builds must not list a contributed project.
/// </summary>
public sealed class ContribIsolationRules
{
    private static readonly Lazy<ContribScanReport> Report = new(
        () => ContribIsolationProbe.Scan(RepoPaths.RepoRoot));

    /// <summary>
    ///     The rule: no product project reaches into <c>contrib/</c> unconditionally, and
    ///     nothing in the product reaches into the vendored tree at all.
    /// </summary>
    [Test]
    public async Task Product_Does_Not_Reach_Into_Contrib()
    {
        ContribScanReport report = Report.Value;

        await Assert.That(string.Join(" | ", report.Violations.Select(static v => v.From + " -> " + v.To)))
            .IsEqualTo(string.Empty)
            .Because(
                "contrib/ is outside CI by owner decision (AGENTS.md): no solution CI builds compiles it, "
                + "so a project under src/ or apps/ that references it pulls unmaintained code into the "
                + "product's build graph. A reference into contrib/ is allowed only behind a MSBuild "
                + "condition — the live example is the Spectre family in Harbor.Hosting.csproj, gated on "
                + "$(HarborWithSpectreTui), which the root Directory.Build.props does not define, so the "
                + "default build compiles none of it. The vendored tree under contrib/external/ may not "
                + "be referenced at all, conditional or not: it is third-party source under a moving pin, "
                + "and the boundary props beside it exist so the root's package and warning policy do "
                + "not reach it. Violations: "
                + (report.Violations.Count == 0
                    ? "(none)"
                    : string.Join(", ", report.Violations.Select(static v => v.From + " -> " + v.To))));
    }

    /// <summary>
    ///     The solution CI builds must not carry a contributed project.
    /// </summary>
    [Test]
    public async Task The_Solution_CI_Builds_Has_No_Contrib_Rows()
    {
        ContribScanReport report = Report.Value;

        await Assert.That(string.Join(" | ", report.SolutionRowsInContrib))
            .IsEqualTo(string.Empty)
            .Because(
                "Harbor.slnx is what CI builds. A row under contrib/ there would put an unmaintained "
                + "project into every CI build, which is the opposite of the boundary this repository "
                + "keeps. (Harbor.Samples.slnx does carry such rows and is not a counter-example: no "
                + "workflow builds it.) Rows: "
                + (report.SolutionRowsInContrib.Count == 0 ? "(none)" : string.Join(", ", report.SolutionRowsInContrib)));
    }

    // =====================================================================
    // Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The walk really found product projects and really parsed
    ///     <c>ProjectReference</c> elements out of them.
    /// </summary>
    /// <remarks>
    ///     Without this, "no violations" and "read nothing" are the same green — the #877
    ///     vacuity that every rule in this repository is written against.
    /// </remarks>
    [Test]
    public async Task The_Walk_Reads_Projects_And_References()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule needs a repository checkout; without one the walk reads nothing and "
                   + "every claim above is satisfied by having looked at nothing");

        ContribScanReport report = Report.Value;

        await Assert.That(report.ProjectsRead).IsGreaterThan(0)
            .Because("the walk found no product csproj, so the rule is graded over an empty subject. "
                   + "Projects read: " + report.ProjectsRead);

        await Assert.That(report.ReferencesRead).IsGreaterThan(0)
            .Because("the walk parsed no ProjectReference element at all, so a rule that cannot see a "
                   + "reference would report the same green. References read: " + report.ReferencesRead);
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. Three planted csproj bodies, driven through the REAL matcher:
    ///     an unconditional <c>src/</c> -&gt; <c>contrib/</c> reference must be reported, a
    ///     conditional one must not, and a conditional reference into the vendored tree must be
    ///     reported anyway.
    /// </summary>
    [Test]
    public async Task The_Rule_Fires_On_A_Synthetic_Src_To_Contrib_Reference()
    {
        const string syntheticProject = "src/Harbor.Synthetic/Harbor.Synthetic.csproj";

        string unconditional = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\..\contrib\tui\Harbor.Tui.Synthetic\Harbor.Tui.Synthetic.csproj"/>
              </ItemGroup>
            </Project>
            """;

        string conditional = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup Condition="'$(HarborWithSynthetic)' == 'true'">
                <ProjectReference Include="..\..\contrib\tui\Harbor.Tui.Synthetic\Harbor.Tui.Synthetic.csproj"/>
              </ItemGroup>
            </Project>
            """;

        string vendored = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup Condition="'$(HarborWithSynthetic)' == 'true'">
                <ProjectReference Include="..\..\contrib\external\ConsoleEx\SharpConsoleUI\SharpConsoleUI.csproj"/>
              </ItemGroup>
            </Project>
            """;

        IReadOnlyList<ContribEdge> unconditionalEdges =
            ContribIsolationProbe.EdgesIn(syntheticProject, unconditional, out int read);
        await Assert.That(read).IsGreaterThan(0)
            .Because("the control must reach the matcher at all");
        await Assert.That(unconditionalEdges.Count).IsEqualTo(1)
            .Because("the planted body declares exactly one reference");
        await Assert.That(unconditionalEdges[0].Conditional).IsFalse()
            .Because("the planted reference sits in an UNCONDITIONED ItemGroup, which is the shape "
                   + "the rule forbids");
        await Assert.That(unconditionalEdges[0].To)
            .IsEqualTo("contrib/tui/Harbor.Tui.Synthetic/Harbor.Tui.Synthetic.csproj")
            .Because("the matcher must resolve the reference to its repo-relative target, otherwise "
                   + "the finding would not say where the edge goes");
        await Assert.That(unconditionalEdges[0].Vendored).IsFalse()
            .Because("contrib/tui is the contributed tree, not the vendored one");

        IReadOnlyList<ContribEdge> conditionalEdges =
            ContribIsolationProbe.EdgesIn(syntheticProject, conditional, out _);
        await Assert.That(conditionalEdges.Count).IsEqualTo(1)
            .Because("the planted body declares exactly one reference");
        await Assert.That(conditionalEdges[0].Conditional).IsTrue()
            .Because("the planted reference sits in a conditioned ItemGroup, which is the sanctioned "
                   + "shape — the live Spectre family has it");

        IReadOnlyList<ContribEdge> vendoredEdges =
            ContribIsolationProbe.EdgesIn(syntheticProject, vendored, out _);
        await Assert.That(vendoredEdges.Count).IsEqualTo(1)
            .Because("the planted body declares exactly one reference");
        await Assert.That(vendoredEdges[0].Vendored).IsTrue()
            .Because("the vendored tree is contrib/external, whatever condition the reference carries");
        await Assert.That(vendoredEdges[0].Conditional).IsTrue()
            .Because("the planted reference IS conditioned, and the vendored claim does not care — "
                   + "that is the whole point of the separate vendored rule");
    }
}
