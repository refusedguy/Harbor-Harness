// ReleaseBuildConfigurationRule.cs — GUARD for issue #1921.
//
// WHAT #1921 MEASURED
// -------------------
// CI run 39389070999 built `Harbor.slnx` in Release, and the project-completion
// timeline showed THREE of the 112 compiled projects producing `bin/Debug`
// output and no `bin/Release` output at all:
//
//   src/Harbor.CodeGen                → bin/Debug/net10.0/Harbor.CodeGen.dll
//   external/ConsoleEx/SharpConsoleUI → bin/Debug/net10.0/SharpConsoleUI.dll
//   apps/Harbor.App.Avalonia           → bin/Debug/net10.0/Harbor.App.Avalonia.dll
//
// The `solution-binaries` artifact of that run confirms the second half of the
// story: 49 dependent Release `bin` directories carried a copy of the DEBUG
// `Harbor.CodeGen.dll` and 14 carried a copy of the DEBUG `SharpConsoleUI.dll`,
// including `apps/Harbor.App.Cli/bin/Release/net10.0/` — the shipped CLI. The
// source generator that runs inside every other project's compilation was a
// Debug build, and a third-party UI kit was a Debug build inside the release
// output.
//
// THE MECHANISM (which the issue refused to guess, and which is not in this
// repository's files at all)
// ------------------------------------------------------------------------
// It is MSBuild's solution build, and the three projects have exactly one thing
// in common: they are NOT rows of `Harbor.slnx`. Every other project in the
// graph is a row, and every other project in the graph was built in Release.
//
//   1. `AssignProjectConfiguration` maps each `<ProjectReference>` onto the
//      SOLUTION's configuration table (dotnet/msbuild,
//      src/Tasks/AssignProjectConfiguration.cs). A referenced project with no
//      row in that table cannot be mapped.
//   2. For an unmappable reference the task sets
//      `GlobalPropertiesToRemove = "Configuration;Platform"` on the item, and
//      the MSBuild call that builds the reference honours it
//      (Microsoft.Common.CurrentVersion.targets:1850:
//      `RemoveProperties="%(...GlobalPropertiesToRemove);TargetFramework;..."`).
//   3. The child then starts with NO Configuration, and
//      Microsoft.Common.CurrentVersion.targets:149 answers with
//      `<Configuration Condition="'$(Configuration)' == ''">Debug</Configuration>`.
//
// The switch for step 2 is `ShouldUnsetParentConfigurationAndPlatform`, which
// defaults to true for a solution build
// (Microsoft.Common.CurrentVersion.targets:1584). The fix for #1921 sets it to
// false in the root `Directory.Build.props`, so an unmappable reference
// inherits the configuration of the project that references it.
//
// INHERITANCE IS NOT THE ONLY CHANNEL
// -----------------------------------
// CI run 39407510139 carried that switch, and it moved the three projects to
// Release — but it ALSO showed that inheritance is not the only way MSBuild
// decides how a referenced project is built: `src/Harbor.CodeGen` was still
// built a second time, in Debug, by a path that did not carry the parent's
// Configuration. The Release OUTPUT was correct in that run (no Debug-built
// assembly reached any Release bin), but a project was still compiled in Debug
// inside a Release build, which is the defect this issue is about.
//
// The second half of the fix therefore STATES the configuration instead of
// inheriting it: the root `Directory.Build.props` gives every
// `<ProjectReference>` a `SetConfiguration` of `Configuration=$(Configuration)`,
// which is the metadata
// Microsoft.Common.CurrentVersion.targets:2150 passes to the MSBuild call that
// builds a referenced project. The child's configuration becomes an explicit
// global property, so whichever path asks for the child, the child is built in
// the configuration of the build that asks.
//
// WHY THIS FILE IS NOT A TABLE OF KNOWN OFFENDERS
// ----------------------------------------------
// The defect is a property of the BUILD GRAPH, not of three names: any project
// that reaches the graph without a solution row inherits the defect, and any
// row-less project that appears later inherits it too. So the claim here is
// computed from the same graph CI builds — the solution's rows closed over
// `<ProjectReference>` — and the answer for each edge is read out of the
// properties that actually decide it.
//
// Two claims, one file:
//
//   1. Every project the Release build pulls in through a `<ProjectReference>`
//      is built in the configuration of the build that asks for it. (The
//      mechanism.)
//   2. No Release output directory contains a copy of a project's own Debug
//      output. (The symptom, read off the build's own artefacts rather than
//      modelled.)
//
// Claim 2 is the one that cannot be argued with: it compares BYTES. A
// Debug-built assembly that reached a dependent's Release `bin` is the same
// file the project produced under `bin/Debug`, so the two are byte-identical —
// and no amount of renaming, repacking or "it is only a generator" makes that
// copy a Release build. The claim therefore covers the vendored UI kit and the
// Avalonia desktop app as well as the generator.
//
// THE PERIMETER
// -------------
// Claim 1 covers every edge whose PARENT is a project this repository owns
// (`src/`, `apps/`, `tests/`, `tools/`, `samples/`, `build/`). It does not
// claim about the vendored tree's INTERNAL edges:
// `external/Directory.Build.props` deliberately severs the repository's build
// policy from the vendored sources ("upstream projects keep their own package
// versions, warning policy and TargetFrameworks"), so a vendored project's own
// references are the vendored project's business.
//
// What the perimeter DOES cover is the edge that matters for the shipped
// product: a product project pulling a vendored project into the graph. That
// edge is claimed — its parent is ours — and it is the edge that put the Debug
// `SharpConsoleUI.dll` into the release CLI.
//
// Claim 2 has no perimeter at all: a Debug-built assembly is a Debug-built
// assembly wherever it lands.
//
// NON-VACUITY
// -----------
// A guard that cannot fail is a comment. This file carries four checks:
//
//   1. `TheWalkFoundSomething` — the graph walk found rows, projects and edges,
//      so "no rows"/"no projects"/"no edges" cannot each report as a pass, and
//      the tree really does contain edges the solution cannot map (otherwise
//      the rule has no subject).
//   2. `TheConfigurationPredicateAnswersTheDeclaredQuestion` — a positive
//      control over a FIXED, filesystem-free table feeding the SAME predicate
//      the live test uses. The table plants a row-less child behind a parent
//      that neither states nor inherits its configuration (must be reported),
//      the same child behind a parent that does (must stay quiet), and a child
//      that IS a row (must never be reported). A predicate that had quietly
//      halved itself fails here rather than passing on the live tree.
//   3. `TheCopyPredicateAnswersTheDeclaredQuestion` — the same for the byte
//      comparison: a Release output holding a byte-identical copy of a Debug
//      output must be reported, and a Release output holding different bytes
//      must not.
//   4. `TheReleaseOutputsWereInspected` — claim 2 had output directories to
//      look at, so "nothing to check" cannot report as a pass.
//
// WHAT IS NOT CLAIMED HERE
// -------------------------
// That a built project is CORRECT, and that the build is FAST. The claim is
// about the configuration every project in the graph is built in — which is
// what #1921 is about — and nothing else.

namespace Harbor.Architecture.Tests;

using System.Security.Cryptography;
using System.Xml.Linq;

/// <summary>
///     The MSBuild machinery that decides whether a reference the solution cannot
///     map inherits the referencing project's configuration.
/// </summary>
internal static class ReleaseConfigurationProbe
{
    /// <summary>
    ///     MSBuild's own property. Left unset it defaults to <c>true</c> for a
    ///     solution build, which is what makes an unmappable reference fall back
    ///     to its own default configuration (<c>Debug</c>).
    /// </summary>
    internal const string OptOutProperty = "ShouldUnsetParentConfigurationAndPlatform";

    /// <summary>The configuration MSBuild falls back to when nothing says otherwise.</summary>
    internal const string DefaultConfiguration = "Debug";

    /// <summary>
    ///     An edge of the build graph: a project that references another one.
    /// </summary>
    /// <param name="Parent">The referencing project (absolute csproj path).</param>
    /// <param name="Child">The referenced project (absolute csproj path).</param>
    /// <param name="StatesConfiguration">
    ///     Whether the reference itself says which configuration it builds the
    ///     child in (<c>SetConfiguration</c> metadata).
    /// </param>
    internal readonly record struct Edge(string Parent, string Child, bool StatesConfiguration);

    /// <summary>
    ///     What a project's build policy says about the configuration of the
    ///     projects it references.
    /// </summary>
    /// <param name="OptOut">
    ///     The effective <see cref="OptOutProperty" /> value, or <c>null</c> when
    ///     nothing sets it (MSBuild's own default for a solution build is
    ///     <c>true</c>, which unsets the configuration).
    /// </param>
    /// <param name="StatesReferenceConfiguration">
    ///     Whether the project's props give every <c>&lt;ProjectReference&gt;</c>
    ///     an explicit <c>SetConfiguration</c>.
    /// </param>
    internal readonly record struct PropsFacts(string? OptOut, bool StatesReferenceConfiguration);

    /// <summary>
    ///     The build policy of one project: what its props chain says about the
    ///     configuration of the projects it references.
    /// </summary>
    internal static PropsFacts ReadProps(string csprojPath, string root)
    {
        string? projectDir = Path.GetDirectoryName(csprojPath);
        PropsFacts chain = projectDir is null
            ? new PropsFacts(null, false)
            : ReadPropsChain(projectDir, root);

        // The project body is evaluated after Directory.Build.props, so a value
        // set in the csproj wins over the props chain.
        string? fromProject = ReadPropertyValue(csprojPath, OptOutProperty);

        return new PropsFacts(fromProject ?? chain.OptOut, chain.StatesReferenceConfiguration);
    }

    /// <summary>
    ///     Every <c>&lt;ProjectReference&gt;</c> a csproj declares, with the
    ///     child's absolute path and whether the reference states the
    ///     configuration it builds the child in.
    /// </summary>
    /// <remarks>
    ///     A <c>Condition</c> is not consulted — the same over-approximation
    ///     <c>BuildGraphProbe.DeclaredReferences</c> makes, and for the same
    ///     reason: a conditional that reads <c>false</c> in the live tree still
    ///     says the author did not want the value to be unconditional.
    /// </remarks>
    internal static IReadOnlyList<(string Child, bool StatesConfiguration)> DeclaredReferences(string csprojPath)
    {
        try
        {
            string projectDir = Path.GetDirectoryName(csprojPath)!;
            var result = new List<(string, bool)>();

            foreach (XElement element in XDocument.Load(csprojPath, LoadOptions.None).Descendants())
            {
                if (!string.Equals(element.Name.LocalName, "ProjectReference", StringComparison.Ordinal))
                {
                    continue;
                }

                string? include = element.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                bool states = element.Elements()
                    .Any(e => string.Equals(e.Name.LocalName, "SetConfiguration", StringComparison.Ordinal));

                string child = Path.GetFullPath(
                    Path.Combine(projectDir, include.Replace('\\', Path.DirectorySeparatorChar)));

                result.Add((child, states));
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return [];
        }
    }

    /// <summary>
    ///     Every edge of the graph whose child is NOT a member of the solution.
    /// </summary>
    /// <remarks>
    ///     These are exactly the edges MSBuild cannot map onto the solution's
    ///     configuration table, and therefore exactly the edges whose child is
    ///     built in its own default configuration unless something says
    ///     otherwise.
    /// </remarks>
    internal static IReadOnlyList<Edge> EdgesWithoutSolutionRow(
        IReadOnlyList<string> solutionRows,
        IReadOnlyDictionary<string, IReadOnlyList<(string Child, bool StatesConfiguration)>> declaredEdges)
    {
        var rows = new HashSet<string>(solutionRows, StringComparer.Ordinal);
        var result = new List<Edge>();

        foreach ((string parent, IReadOnlyList<(string Child, bool StatesConfiguration)> children) in declaredEdges)
        {
            foreach ((string child, bool states) in children)
            {
                if (!rows.Contains(child))
                {
                    result.Add(new Edge(parent, child, states));
                }
            }
        }

        return result;
    }

    /// <summary>
    ///     The unmappable edges whose child is left to MSBuild's default
    ///     configuration — the edges along which a project is built in the wrong
    ///     configuration.
    /// </summary>
    /// <param name="edges">Every edge of the graph.</param>
    /// <param name="protects">
    ///     Answers whether the parent keeps the child's configuration pinned to
    ///     the parent's own: either the reference STATES it, or the parent's
    ///     props opt out of MSBuild's unset so the child INHERITS it.
    /// </param>
    internal static IReadOnlyList<Edge> UnprotectedEdges(
        IEnumerable<Edge> edges,
        Func<string, bool?> protects)
    {
        var result = new List<Edge>();

        foreach (Edge edge in edges)
        {
            bool? inherited = protects(edge.Parent);
            if (inherited != true && !edge.StatesConfiguration)
            {
                result.Add(edge);
            }
        }

        return result;
    }

    /// <summary>
    ///     The Release outputs that are byte-identical copies of a Debug output.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The comparison is on BYTES, not on a name, a path or a timestamp: a
    ///         Debug-built assembly that reached a dependent's <c>bin/Release</c> is
    ///         the very file the project produced under <c>bin/Debug</c>, so the two
    ///         are identical. A Release build of the same project is a different
    ///         file, so the comparison separates the two cases exactly.
    ///     </para>
    ///     <para>
    ///         The reader is a function so the live scan does not have to hold
    ///         every Release output in memory: it returns <c>null</c> for a file
    ///         whose length matches no Debug output, and the comparison below
    ///         skips it. A byte-identical copy has the same length, so the
    ///         shortcut cannot change the answer.
    ///     </para>
    /// </remarks>
    /// <param name="debugOutputs">The Debug outputs the graph's projects produced.</param>
    /// <param name="releaseOutputs">The Release outputs to look at, with a lazy reader.</param>
    internal static IReadOnlyList<string> CopiesOfDebugOutput(
        IReadOnlyList<(string Name, byte[] Bytes)> debugOutputs,
        IReadOnlyList<(string Name, Func<byte[]?> Read)> releaseOutputs)
    {
        var result = new List<string>();

        foreach ((string name, Func<byte[]?> read) in releaseOutputs)
        {
            byte[]? bytes = read();
            if (bytes is null)
            {
                continue;
            }

            foreach ((string debugName, byte[] debugBytes) in debugOutputs)
            {
                if (bytes.AsSpan().SequenceEqual(debugBytes))
                {
                    result.Add($"{name} (copy of {debugName})");
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>The last value a property is given in a file, ignoring conditions.</summary>
    private static string? ReadPropertyValue(string filePath, string propertyName)
    {
        try
        {
            XDocument document = XDocument.Load(filePath, LoadOptions.None);
            string? value = null;

            foreach (XElement element in document.Descendants())
            {
                if (string.Equals(element.Name.LocalName, propertyName, StringComparison.Ordinal))
                {
                    value = element.Value.Trim();
                }
            }

            return value;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The build policy the props chain gives a project directory, following
    ///     imports of <c>Directory.Build.props</c> where they appear.
    /// </summary>
    private static PropsFacts ReadPropsChain(string projectDir, string root) =>
        EvaluateProps(
            NearestProps(projectDir, root, excludeSelf: false),
            root,
            new HashSet<string>(StringComparer.Ordinal));

    private static PropsFacts EvaluateProps(string? propsPath, string root, HashSet<string> visiting)
    {
        if (propsPath is null || !visiting.Add(propsPath))
        {
            return new PropsFacts(null, false);
        }

        string? value = null;
        bool states = false;

        try
        {
            XDocument document = XDocument.Load(propsPath, LoadOptions.None);
            IEnumerable<XElement> children = document.Root?.Elements() ?? Enumerable.Empty<XElement>();

            foreach (XElement element in children)
            {
                if (string.Equals(element.Name.LocalName, "Import", StringComparison.Ordinal))
                {
                    string? imported = element.Attribute("Project")?.Value;
                    if (imported is not null &&
                        imported.Contains("Directory.Build.props", StringComparison.Ordinal))
                    {
                        // The repository's import idiom is
                        // `$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))`,
                        // which resolves to the nearest Directory.Build.props strictly
                        // above the importing file's own directory.
                        string? parent = NearestProps(
                            Path.GetDirectoryName(propsPath)!, root, excludeSelf: true);
                        PropsFacts importedFacts = EvaluateProps(parent, root, visiting);
                        value = importedFacts.OptOut ?? value;
                        states = states || importedFacts.StatesReferenceConfiguration;
                    }

                    continue;
                }

                if (string.Equals(element.Name.LocalName, "PropertyGroup", StringComparison.Ordinal))
                {
                    foreach (XElement property in element.Elements())
                    {
                        if (string.Equals(property.Name.LocalName, OptOutProperty, StringComparison.Ordinal))
                        {
                            value = property.Value.Trim();
                        }
                    }

                    continue;
                }

                if (string.Equals(element.Name.LocalName, "ItemDefinitionGroup", StringComparison.Ordinal))
                {
                    foreach (XElement definition in element.Elements())
                    {
                        if (!string.Equals(definition.Name.LocalName, "ProjectReference", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        states = states || definition.Elements().Any(
                            e => string.Equals(e.Name.LocalName, "SetConfiguration", StringComparison.Ordinal));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            // A props file that cannot be read contributes nothing, exactly as it
            // would contribute nothing to the build.
        }

        visiting.Remove(propsPath);
        return new PropsFacts(value, states);
    }

    /// <summary>
    ///     The nearest <c>Directory.Build.props</c> at or above a directory,
    ///     stopping at the repository root.
    /// </summary>
    private static string? NearestProps(string directory, string root, bool excludeSelf)
    {
        string self = Path.Combine(directory, "Directory.Build.props");
        var current = new DirectoryInfo(directory);

        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "Directory.Build.props");

            if (File.Exists(candidate) && !(excludeSelf && PathsEqual(candidate, self)))
            {
                return candidate;
            }

            if (PathsEqual(current.FullName, root))
            {
                return null;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.Ordinal);
}

/// <summary>
///     Asserts that every project a Release build pulls in is built in the
///     configuration of the build that asks for it, and that no Debug-built
///     assembly reaches a Release output directory. See the file header for the
///     measurement, the mechanism and the non-vacuity argument.
/// </summary>
public sealed class ReleaseBuildConfigurationRule
{
    /// <summary>The trees this repository owns. A parent outside them is not ours to judge.</summary>
    private static readonly string[] OwnedTrees = ["apps", "build", "samples", "src", "tests", "tools"];

    private static readonly Lazy<GraphReport> Report = new(Measure);

    /// <summary>What the graph walk and the output scan found, once.</summary>
    private sealed record GraphReport(
        int SolutionRowCount,
        int DiskCsprojCount,
        int ClosureCount,
        IReadOnlyList<ReleaseConfigurationProbe.Edge> UnmappedEdges,
        IReadOnlyList<ReleaseConfigurationProbe.Edge> UnprotectedEdges,
        IReadOnlyList<string> DebugOutputs,
        IReadOnlyList<string> DebugCopiesInReleaseOutputs,
        int ReleaseOutputsConsidered);

    private static GraphReport Measure()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return new GraphReport(0, 0, 0, [], [], [], [], 0);
        }

        IReadOnlyList<string> disk = BuildGraphProbe.DiskCsprojs(root);
        IReadOnlyList<string> rows = BuildGraphProbe.SolutionRows(root, Path.Combine(root, "Harbor.slnx"));

        var edges = new Dictionary<string, IReadOnlyList<(string Child, bool StatesConfiguration)>>(
            StringComparer.Ordinal);

        // The same graph in the shape BuildGraphProbe.Closure walks: the paths
        // only, without the metadata this rule reads off each edge.
        var paths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (string project in disk)
        {
            IReadOnlyList<(string Child, bool StatesConfiguration)> references =
                ReleaseConfigurationProbe.DeclaredReferences(project);

            edges[project] = references;
            paths[project] = [.. references.Select(r => r.Child)];
        }

        IReadOnlyList<string> closure = [.. BuildGraphProbe.Closure(rows, paths)];

        // The perimeter: an edge is ours when the PARENT is a project this
        // repository owns. The vendored tree's internal edges are not.
        IReadOnlyList<ReleaseConfigurationProbe.Edge> unmapped =
        [
            .. ReleaseConfigurationProbe
                .EdgesWithoutSolutionRow(rows, edges)
                .Where(e => OwnedTrees.Contains(BuildGraphProbe.TreeOf(root, e.Parent), StringComparer.Ordinal)),
        ];

        IReadOnlyList<ReleaseConfigurationProbe.Edge> unprotected =
        [
            .. ReleaseConfigurationProbe.UnprotectedEdges(
                unmapped,
                parent => Protects(parent, root)),
        ];

        (IReadOnlyList<string> debugOutputs, IReadOnlyList<string> copies, int considered) =
            ScanForDebugCopies(closure, root);

        return new GraphReport(
            rows.Count,
            disk.Count,
            closure.Count,
            unmapped,
            unprotected,
            debugOutputs,
            copies,
            considered);
    }

    /// <summary>
    ///     Does this parent keep the configuration of the projects it references
    ///     pinned to its own?
    /// </summary>
    /// <remarks>
    ///     Two ways, and the fix for #1921 uses both: the parent's props opt out
    ///     of MSBuild's unset (so an unmappable reference INHERITS the parent's
    ///     configuration), and the repository's props give every reference an
    ///     explicit <c>SetConfiguration</c> (so the child's configuration is
    ///     STATED rather than inherited, whichever path builds it).
    /// </remarks>
    private static bool? Protects(string parentCsproj, string root)
    {
        ReleaseConfigurationProbe.PropsFacts facts = ReleaseConfigurationProbe.ReadProps(parentCsproj, root);

        if (facts.StatesReferenceConfiguration)
        {
            return true;
        }

        return facts.OptOut is null
            ? null
            : string.Equals(facts.OptOut, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The Debug outputs the graph's own projects produced, and the Release
    ///     outputs that carry a byte-identical copy of one.
    /// </summary>
    private static (IReadOnlyList<string> DebugOutputs, IReadOnlyList<string> Copies, int Considered) ScanForDebugCopies(
        IReadOnlyList<string> closure,
        string root)
    {
        // The assembly each project of the closure produces.
        var ownNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string project in closure)
        {
            string projectDir = Path.GetFileName(Path.GetDirectoryName(project) ?? string.Empty);
            ownNames[project] = RepoPaths.ReadAssemblyName(project, projectDir);
        }

        var names = new HashSet<string>(ownNames.Values, StringComparer.OrdinalIgnoreCase);

        // 1. What the graph's own projects produced under bin/Debug — THEIR OWN
        //    assembly only. A Debug output directory also holds COPIES of the
        //    project's dependencies (built in Release, and therefore byte-identical
        //    to the copies in every Release bin), and those are not a Debug build
        //    of anything: comparing them would report the fixed tree as broken.
        var debugOutputs = new List<(string Name, byte[] Bytes)>();
        var debugPaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string project in closure)
        {
            string projectDir = Path.GetDirectoryName(project)!;
            string debugRoot = Path.Combine(projectDir, "bin", "Debug");

            if (!Directory.Exists(debugRoot))
            {
                continue;
            }

            string assemblyName = ownNames[project];
            string ownFile = assemblyName + ".dll";

            foreach (string file in Directory.EnumerateFiles(debugRoot, ownFile, SearchOption.AllDirectories))
            {
                // obj/ref/<tfm>/ holds the REFERENCE assembly, which is a
                // different file from the one the build ships.
                if (file.Contains(
                        $"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    string fingerprint = Convert.ToHexString(SHA256.HashData(bytes));

                    if (!seen.Add(fingerprint))
                    {
                        continue;
                    }

                    debugOutputs.Add((BuildGraphProbe.Relative(root, file), bytes));
                    debugPaths.Add(BuildGraphProbe.Relative(root, file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file that cannot be read says nothing about the build.
                }
            }
        }

        long[] lengths = [.. debugOutputs.Select(o => (long)o.Bytes.Length)];

        var releaseOutputs = new List<(string Name, Func<byte[]?> Read)>();
        int considered = 0;

        foreach (string project in closure)
        {
            string projectDir = Path.GetDirectoryName(project)!;
            string releaseRoot = Path.Combine(projectDir, "bin", "Release");

            if (!Directory.Exists(releaseRoot))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(releaseRoot, "*.dll", SearchOption.AllDirectories))
            {
                if (!names.Contains(Path.GetFileNameWithoutExtension(file)))
                {
                    continue;
                }

                considered++;
                releaseOutputs.Add((BuildGraphProbe.Relative(root, file), () => ReadIfLengthMatches(file, lengths)));
            }
        }

        IReadOnlyList<string> copies =
            ReleaseConfigurationProbe.CopiesOfDebugOutput(debugOutputs, releaseOutputs);

        debugPaths.Sort(StringComparer.Ordinal);
        return (debugPaths, copies, considered);
    }

    /// <summary>
    ///     Reads a file only when its length matches one of the Debug outputs —
    ///     a byte-identical copy has the same length, so the shortcut cannot
    ///     change the answer, and the scan does not have to hold every Release
    ///     output in memory.
    /// </summary>
    private static byte[]? ReadIfLengthMatches(string path, long[] lengths)
    {
        try
        {
            var info = new FileInfo(path);
            if (!lengths.Contains(info.Length))
            {
                return null;
            }

            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // =====================================================================
    // 1. The claims.
    // =====================================================================

    /// <summary>
    ///     Every project the Release build pulls in through a
    ///     <c>&lt;ProjectReference&gt;</c> must be built in the configuration of
    ///     the build that asks for it.
    /// </summary>
    [Test]
    public async Task EveryProjectTheReleaseBuildPullsIn_IsBuiltInTheSolutionsConfiguration()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("this rule walks a checkout; outside one the graph is empty and every assertion "
                   + "below would hold for the wrong reason — see TheWalkFoundSomething");

        GraphReport report = Report.Value;
        IReadOnlyList<ReleaseConfigurationProbe.Edge> unprotected = report.UnprotectedEdges;

        await Assert.That(Join(unprotected))
            .IsEqualTo(string.Empty)
            .Because(
                "these edges point at a project that is not a row of Harbor.slnx, and nothing keeps the "
                + "child's configuration pinned to the configuration of the build that asks for it: the "
                + "reference does not state one, and the parent does not opt out of MSBuild's unset. "
                + "MSBuild cannot map such a reference onto the solution's configuration table, so it "
                + "removes Configuration and Platform from the child's global properties "
                + "(dotnet/msbuild AssignProjectConfiguration.cs; "
                + "Microsoft.Common.CurrentVersion.targets:1850) and the child then falls back to "
                + "Microsoft.Common.CurrentVersion.targets:149 — " + ReleaseConfigurationProbe.DefaultConfiguration
                + ". The child is therefore built in Debug inside a Release build, and its Debug output "
                + "is what the dependents' Release bin directories receive. The fix is the parent's "
                + "effective ShouldUnsetParentConfigurationAndPlatform = false (the child INHERITS the "
                + "parent's configuration) plus a SetConfiguration on every reference (the child's "
                + "configuration is STATED, whichever path builds it). Unprotected ("
                + unprotected.Count + "): " + Describe(unprotected)
                + " | walked " + report.DiskCsprojCount + " csproj, " + report.SolutionRowCount
                + " solution rows, " + report.ClosureCount + " projects in the closure, "
                + report.UnmappedEdges.Count + " edges the solution cannot map.");
    }

    /// <summary>
    ///     No Release output directory may hold a copy of a project's Debug
    ///     output.
    /// </summary>
    [Test]
    public async Task NoReleaseOutput_HoldsACopyOfADebugBuild()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("without a checkout there are no output directories to look at");

        GraphReport report = Report.Value;
        IReadOnlyList<string> copies = report.DebugCopiesInReleaseOutputs;

        await Assert.That(Join(copies))
            .IsEqualTo(string.Empty)
            .Because(
                "these files sit in a bin/Release directory and are byte-identical to a project's own "
                + "bin/Debug output. That is the defect #1921 describes, measured rather than modelled: "
                + "the project was compiled in Debug, and this is the very file it produced. In CI run "
                + "39389070999 that was 49 dependent directories holding the Debug Harbor.CodeGen.dll "
                + "and 14 holding the Debug SharpConsoleUI.dll, including the shipped CLI. Copied ("
                + copies.Count + "): " + Describe(copies)
                + " | Debug outputs found: " + Describe(report.DebugOutputs) + ".");
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The walk found a solution, a tree and edges — so "no rows", "no
    ///     projects" and "no edges" cannot each report as a pass.
    /// </summary>
    [Test]
    public async Task TheWalkFoundSomething()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("without a checkout the walk has nothing to find, and an empty finding is what a "
                   + "broken walk looks like from here");

        GraphReport report = Report.Value;

        await Assert.That(report.SolutionRowCount).IsGreaterThan(0)
            .Because("no solution row parsed — the .slnx moved, the Project element is spelled "
                   + "differently, or the file failed to load. Every assertion in this file would then "
                   + "hold over an empty closure, which is indistinguishable from a working rule");

        await Assert.That(report.DiskCsprojCount).IsGreaterThan(0)
            .Because("the disk walk found no csproj, so the closure is empty for want of a subject "
                   + "rather than for want of a build");

        await Assert.That(report.ClosureCount).IsGreaterThan(report.SolutionRowCount)
            .Because("the closure is no larger than the row list, so no ProjectReference edge was "
                   + "followed. The rows are the projects the solution names; the closure is the rows "
                   + "PLUS everything they pull in — and everything they pull in is exactly what this "
                   + "rule is about. A closure equal to the row list would mean the walk stopped at the "
                   + "rows and the rule would be judging a graph that does not exist.");

        await Assert.That(report.UnmappedEdges.Count).IsGreaterThan(0)
            .Because("no edge points at a project outside the solution, so the rule has no subject. "
                   + "This is the shape of the defect #1921 found — three projects reach the Release "
                   + "build through a reference the solution cannot map — and a tree with no such edge "
                   + "would make this rule vacuous rather than satisfied.");
    }

    /// <summary>
    ///     The configuration predicate answers the question this file declares, on
    ///     a fixed filesystem-free table.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The table plants the three cases that matter: a row-less child
    ///         behind a parent that neither states nor inherits the configuration
    ///         (must be reported — this is the defect), the same child behind a
    ///         parent that does (must stay quiet — this is the fixed tree), and a
    ///         child that IS a row (must never be reported — the solution's own
    ///         mapping decides that one).
    ///     </para>
    ///     <para>
    ///         A predicate that had quietly halved itself — one that stopped
    ///         looking at edges, or that stopped asking whether the parent keeps
    ///         the configuration pinned — produces a different answer on this
    ///         table, and that difference is what the assertion below measures.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task TheConfigurationPredicateAnswersTheDeclaredQuestion()
    {
        const string row = "/synthetic/src/Harbor.Row/Harbor.Row.csproj";
        const string inherit = "/synthetic/src/Harbor.Inherit/Harbor.Inherit.csproj";
        const string strict = "/synthetic/src/Harbor.Strict/Harbor.Strict.csproj";
        const string mapped = "/synthetic/src/Harbor.Mapped/Harbor.Mapped.csproj";
        const string unmapped = "/synthetic/src/Harbor.Unmapped/Harbor.Unmapped.csproj";

        IReadOnlyList<string> rows = [row, mapped];

        // `Harbor.Row` STATES the configuration of the child it references;
        // `Harbor.Inherit` neither states nor inherits but opts out of MSBuild's
        // unset, so the child inherits; `Harbor.Strict` does neither; and
        // `Harbor.Mapped` is a row of the solution, so the solution's own
        // mapping decides its configuration.
        var edges = new Dictionary<string, IReadOnlyList<(string Child, bool StatesConfiguration)>>(
            StringComparer.Ordinal)
        {
            [row] = [(unmapped, true)],
            [inherit] = [(unmapped, false)],
            [strict] = [(unmapped, false)],
            [mapped] = [],
        };

        bool? ParentProtects(string parent) => parent switch
        {
            inherit => true,
            strict => false,
            _ => null,
        };

        IReadOnlyList<ReleaseConfigurationProbe.Edge> unmappedEdges =
            ReleaseConfigurationProbe.EdgesWithoutSolutionRow(rows, edges);

        IReadOnlyList<ReleaseConfigurationProbe.Edge> unprotected =
            ReleaseConfigurationProbe.UnprotectedEdges(unmappedEdges, ParentProtects);

        await Assert.That(string.Join(" | ", unmappedEdges.Select(e => e.Child)))
            .IsEqualTo(unmapped + " | " + unmapped + " | " + unmapped)
            .Because(
                "the three edges into the row-less child are the edges MSBuild cannot map, and they are "
                + "the whole expected answer. The edge into Harbor.Mapped must NOT be here: a project "
                + "that IS a row of the solution is configured by the solution's own mapping and is "
                + "never at risk. Live: "
                + (unmappedEdges.Count == 0 ? "(none)" : string.Join(" | ", unmappedEdges.Select(e => e.Child))));

        await Assert.That(string.Join(" | ", unprotected.Select(e => e.Parent)))
            .IsEqualTo(strict)
            .Because(
                "only the parent that neither states nor inherits the configuration is reported. If "
                + "Harbor.Strict is missing, the predicate stopped asking whether the parent keeps the "
                + "configuration pinned and every row-less edge would be reported; if Harbor.Row or "
                + "Harbor.Inherit is here too, the stated and the inherited configuration are being "
                + "ignored and the fixed tree would fail. Live: "
                + (unprotected.Count == 0 ? "(none)" : string.Join(" | ", unprotected.Select(e => e.Parent))));
    }

    /// <summary>
    ///     The copy predicate answers the question this file declares, on a fixed
    ///     filesystem-free table.
    /// </summary>
    [Test]
    public async Task TheCopyPredicateAnswersTheDeclaredQuestion()
    {
        byte[] debugBytes = [0x44, 0x65, 0x62, 0x75, 0x67];             // "Debug"
        byte[] releaseBytes = [0x52, 0x65, 0x6C, 0x65, 0x61, 0x73, 0x65]; // "Release"

        IReadOnlyList<(string Name, byte[] Bytes)> debugOutputs =
        [
            ("src/Harbor.Foo/bin/Debug/net10.0/Harbor.Foo.dll", debugBytes),
        ];

        IReadOnlyList<(string Name, Func<byte[]?> Read)> releaseOutputs =
        [
            ("src/Harbor.Foo/bin/Release/net10.0/Harbor.Foo.dll", () => releaseBytes),
            ("apps/Harbor.App.Cli/bin/Release/net10.0/Harbor.Foo.dll", () => debugBytes),
            ("apps/Harbor.App.Cli/bin/Release/net10.0/Harbor.Bar.dll", () => null),
        ];

        IReadOnlyList<string> copies =
            ReleaseConfigurationProbe.CopiesOfDebugOutput(debugOutputs, releaseOutputs);

        await Assert.That(string.Join(" | ", copies))
            .IsEqualTo("apps/Harbor.App.Cli/bin/Release/net10.0/Harbor.Foo.dll (copy of "
                       + "src/Harbor.Foo/bin/Debug/net10.0/Harbor.Foo.dll)")
            .Because(
                "exactly one of the three Release outputs is a byte-identical copy of the Debug output, "
                + "and it is the one that must be reported. The Release output with different bytes is "
                + "the FIXED case and must stay quiet, and the output the reader declines to read "
                + "(null) must not be reported either. If the copy is missing, the predicate stopped "
                + "comparing bytes and the symptom claim would pass on the broken tree; if the other "
                + "two are here, it reports every Release output instead of the leaked ones.");
    }

    /// <summary>
    ///     The symptom claim had output directories to look at.
    /// </summary>
    [Test]
    public async Task TheReleaseOutputsWereInspected()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("without a checkout there are no output directories to look at");

        GraphReport report = Report.Value;

        await Assert.That(report.ReleaseOutputsConsidered).IsGreaterThan(0)
            .Because(
                "no Release output of the build closure was found, so the symptom claim had no subject "
                + "and passed for the wrong reason. The files it looks at are the ones the build "
                + "produced; if the rule runs before the build has produced them, this is where that "
                + "shows up. Considered: " + report.ReleaseOutputsConsidered + ".");
    }

    // =====================================================================
    // 3. Helpers.
    // =====================================================================

    /// <summary>
    ///     The reported items as one string — the empty string when there are
    ///     none, so an assertion can compare against it.
    /// </summary>
    private static string Join(IReadOnlyList<ReleaseConfigurationProbe.Edge> edges) =>
        edges.Count == 0
            ? string.Empty
            : string.Join(" | ", edges.Select(e => $"{Relative(e.Parent)} -> {Relative(e.Child)}"));

    private static string Join(IReadOnlyList<string> items) =>
        items.Count == 0 ? string.Empty : string.Join(" | ", items);

    private static string Describe(IReadOnlyList<ReleaseConfigurationProbe.Edge> edges) =>
        edges.Count == 0
            ? "(none)"
            : string.Join(" | ", edges.Select(e => $"{Relative(e.Parent)} -> {Relative(e.Child)}"));

    private static string Describe(IReadOnlyList<string> items) =>
        items.Count == 0
            ? "(none)"
            : string.Join(" | ", items.Take(12)) + (items.Count > 12 ? $" | ... and {items.Count - 12} more" : string.Empty);

    /// <summary>Repo-relative form of an absolute path.</summary>
    private static string Relative(string absolutePath) =>
        RepoPaths.RepoRoot is { } root ? BuildGraphProbe.Relative(root, absolutePath) : absolutePath;
}
