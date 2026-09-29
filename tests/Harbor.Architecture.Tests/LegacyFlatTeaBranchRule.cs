// LegacyFlatTeaBranchRule.cs — GUARD for issue #594.
//
// THE CONVENTION BEING ENFORCED
// ------------------------------
// The `AgentEvent` → UI-state fold has exactly ONE home in this repo, and it is
// `Harbor.Ui.Framework.State`: `UiStore.Dispatch(AgentEvent)` reduces through
// `State/ChatAppReducer.cs`, with the domain-free `State/AppReducer.cs` handling
// the `AppMsg` half. `docs/EVENT_TOPOLOGY.md` §2 calls those "the live branches".
//
// #594 found a second, parallel home that the live one had quietly outgrown:
// `src/Harbor.Ui.Framework.Reducers/`, holding `AppReducer` (over a flat
// `AppState`), `AppStore`, and the `ChatViewReducer` / `ChromeReducer` /
// `SessionsReducer` trio it delegates to — plus `EventBusAppStoreDispatcher` in
// `Harbor.Ui.Framework.Services`, the only thing that ever posted into
// `AppStore`. No composition root constructed any of them; the renderers
// dispatch into `UiStore`. The XML doc on the dead `AppReducer` claimed
// "Every interactive renderer funnels its events through this", which is the
// sentence that kept it looking maintained.
//
// WHY A RULE AND NOT JUST A DELETION
// ---------------------------------
// A deleted project is one `git revert` away from coming back, and it comes
// back looking productive: the reducers are pure, the state records are real,
// and the layer matrix already had a row for the project, so a re-added
// `<ProjectReference>` passes every pre-existing gate. The deletion needs
// teeth, and the teeth are here.
//
// THE FOUR RULES
// --------------
//   1. No `.cs` file lives under `src/Harbor.Ui.Framework.Reducers/`.
//   2. No `.csproj` and no `.slnx` anywhere in the repo names
//      `Harbor.Ui.Framework.Reducers` — a declared edge with no producer is the
//      shape that let `Harbor.Ui.Framework.Services`, `Harbor.Benchmarks` and
//      `Harbor.Architecture.Tests` keep compiling the dead assembly.
//   3. None of the three dead types is DECLARED under `src/` or `apps/`:
//      `AppReducer` in namespace `Harbor.Ui.Framework.Reducers`,
//      `AppStore` in namespace `Harbor.Ui.Framework.State`, and
//      `EventBusAppStoreDispatcher` anywhere.
//   4. THE LIVE HALF IS STILL THERE: `State/ChatAppReducer.cs` and
//      `State/UiStore.cs` must exist.
//
// RULE 4 IS THE POINT OF THE WHOLE FILE
// -------------------------------------
// Rules 1–3 on their own are satisfied by deleting the entire UI state layer,
// which would be a catastrophe wearing a cleanup's clothes. Rule 4 says the
// deletion is specifically of the LEGACY branch: the one home that stays is
// named, with its two load-bearing files, in the same file as the three that
// must not come back. A rule that cannot be satisfied by breaking the product
// is not a rule about this product.
//
// WHY NAMESPACE, NOT NAME, FOR THE REDUCERS
// ------------------------------------------
// `AppReducer` is ALSO the live name: `src/Harbor.Ui.Framework.State/State/
// AppReducer.cs` declares `Harbor.Ui.Framework.State.AppReducer` and is on the
// `UiStore` path every day. A rule keyed on the simple name would forbid the
// working reducer and pass the dead one. So the pair is qualified: the same
// name is a finding in `Harbor.Ui.Framework.Reducers` and a non-finding in
// `Harbor.Ui.Framework.State`. This is the #558 mistake inverted — there the
// duplicate name HID the dead copy, here the duplicate name would condemn the
// live one.
//
// PERIMETER
// ---------
// `src/` + `apps/` for declarations, and the whole repo for project/solution
// references. `contrib/` holds its own `AppStore`
// (`contrib/tui/Harbor.Tui.SpectreTui/State/AppStore.cs`) and is unmaintained,
// outside CI, and out of scope by owner decision (AGENTS.md) — it is neither
// scanned nor expected to be clean, and that gap is deliberate.
//
// NON-VACUITY
// -----------
//   1. Scan_IsLive — the scan walked a checkout, read real files, and found the
//      live `ChatAppReducer` it is about to assert on. A rule satisfied by
//      looking at nothing is the failure this repo has already paid for twice.
//   2. NonVacuity_Scan_DetectsTheLegacyBranchInSyntheticSources — THE POSITIVE
//      CONTROL. The probe is handed the #594 shape item for item (a file at the
//      deleted path, the dead `AppReducer` in the deleted namespace, the dead
//      `AppStore` in the State namespace, the dead dispatcher in a surviving
//      project, a `<ProjectReference>` to the deleted project, a solution entry
//      for it) and five decoys it must NOT report — including the LIVE
//      `AppReducer` that shares its name with the dead one, and a
//      commented-out reference.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>One thing the #594 scan found that must not be there.</summary>
/// <param name="Kind">
///     Machine-readable rule id — <c>source-in-deleted-project</c>,
///     <c>project-reference</c>, <c>solution-entry</c> or <c>dead-type-declared</c>.
/// </param>
/// <param name="File">Repo-relative path, forward slashes.</param>
/// <param name="Line">1-based line the violation sits on.</param>
/// <param name="Detail">What the violation is, quoted from the file.</param>
internal sealed record LegacyBranchViolation(string Kind, string File, int Line, string Detail);

/// <summary>Everything the rule needs from one repository scan.</summary>
/// <param name="Violations">Every finding, sorted — a non-empty list is the #594 failure.</param>
/// <param name="FilesScanned">How many <c>.cs</c> files were read.</param>
/// <param name="ProjectFilesScanned">How many <c>.csproj</c> / <c>.slnx</c> files were read.</param>
/// <param name="LiveFilesFound">Which of the rule-4 live files the scan actually saw.</param>
internal sealed record LegacyBranchReport(
    IReadOnlyList<LegacyBranchViolation> Violations,
    int FilesScanned,
    int ProjectFilesScanned,
    IReadOnlyList<string> LiveFilesFound);

/// <summary>
///     Finds the deleted legacy flat-TEA branch and anything still pointing at it.
/// </summary>
internal static partial class LegacyFlatTeaBranchProbe
{
    /// <summary>The project directory #594 removed, repo-relative with a trailing slash.</summary>
    internal const string DeletedProjectDir = "src/Harbor.Ui.Framework.Reducers/";

    /// <summary>
    ///     The one name that identifies the deleted project. The <c>.csproj</c>
    ///     spelling contains it as a substring, so a single bounded-ish
    ///     <c>Contains</c> covers both — two patterns would report the same line
    ///     twice and turn one real violation into two.
    /// </summary>
    internal const string DeletedProjectName = "Harbor.Ui.Framework.Reducers";

    /// <summary>The two files that must survive the deletion, named repo-relative.</summary>
    internal static readonly string[] RequiredLiveFiles =
    [
        "src/Harbor.Ui.Framework.State/State/ChatAppReducer.cs",
        "src/Harbor.Ui.Framework.State/State/UiStore.cs",
    ];

    /// <summary>Repository roots walked for type declarations. <c>contrib/</c> is excluded on purpose.</summary>
    private static readonly string[] SourceRoots = ["src", "apps"];

    /// <summary>Directory names never descended into during the scan.</summary>
    private static readonly string[] SkippedDirectories =
        [".git", "bin", "obj", "external", ".worktrees", "node_modules", "contrib"];

    /// <summary>Walks the repository. A missing checkout scans nothing, which the rule reports as a failure.</summary>
    internal static LegacyBranchReport Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new LegacyBranchReport([], 0, 0, []);
        }

        var sources = new List<(string Relative, string[] Lines)>();
        foreach (string file in EnumerateFiles(repoRoot, SourceRoots, "*.cs"))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            sources.Add((MakeRelative(repoRoot, file), lines));
        }

        var projectFiles = new List<(string Relative, string[] Lines)>();
        foreach (string pattern in new[] { "*.csproj", "*.slnx" })
        {
            foreach (string file in EnumerateFiles(repoRoot, ["."], pattern))
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(file);
                }
                catch (IOException)
                {
                    continue;
                }

                projectFiles.Add((MakeRelative(repoRoot, file), lines));
            }
        }

        return ScanFiles(sources, projectFiles);
    }

    /// <summary>
    ///     Grades already-read files. Exposed so the positive control drives the
    ///     REAL matcher — comment stripping and namespace resolution included —
    ///     rather than a second implementation of it, which is the only way
    ///     "it can fail" means anything.
    /// </summary>
    internal static LegacyBranchReport ScanFiles(
        List<(string Relative, string[] Lines)> sources,
        List<(string Relative, string[] Lines)> projectFiles)
    {
        var violations = new List<LegacyBranchViolation>();
        var liveSeen = new List<string>();

        foreach (var source in sources)
        {
            // Rule 1. Recorded and NOT short-circuited: a file at the deleted
            // path is both a misplaced file and, if it declares one of the dead
            // types, rule 3 as well. The positive control asserts both fire.
            if (source.Relative.StartsWith(DeletedProjectDir, StringComparison.Ordinal))
            {
                violations.Add(new LegacyBranchViolation(
                    "source-in-deleted-project",
                    source.Relative,
                    1,
                    "a .cs file still lives under " + DeletedProjectDir));
            }

            if (IsRequiredLiveFile(source.Relative))
            {
                liveSeen.Add(source.Relative);
            }

            violations.AddRange(FindDeadDeclarations(source.Relative, source.Lines));
        }

        foreach (var project in projectFiles)
        {
            violations.AddRange(FindProjectMentions(project.Relative, project.Lines));
        }

        violations.Sort(static (a, b) =>
        {
            int byFile = string.CompareOrdinal(a.File, b.File);
            return byFile != 0 ? byFile : a.Line.CompareTo(b.Line);
        });

        return new LegacyBranchReport(violations, sources.Count, projectFiles.Count, liveSeen);
    }

    /// <summary>
    ///     Scans one C# file for the three dead type declarations. Comments are
    ///     stripped first, so the XML docs that name <c>AppStore</c> or
    ///     <c>EventBusAppStoreDispatcher</c> cannot be mistaken for a
    ///     declaration.
    /// </summary>
    internal static List<LegacyBranchViolation> FindDeadDeclarations(string relativeFile, string[] lines)
    {
        string[] clean = SourceCommentStripper.StripAll(lines);
        var found = new List<LegacyBranchViolation>();
        string currentNamespace = string.Empty;

        for (int i = 0; i < clean.Length; i++)
        {
            string line = clean[i];

            foreach (Match match in NamespaceDeclaration().Matches(line))
            {
                currentNamespace = match.Groups["name"].Value;
            }

            foreach (Match match in TypeKeyword().Matches(line))
            {
                string name = match.Groups["name"].Value;
                if (IsBannedDeclaration(name, currentNamespace))
                {
                    found.Add(new LegacyBranchViolation(
                        "dead-type-declared",
                        relativeFile,
                        i + 1,
                        "type " + currentNamespace + "." + name + " is part of the #594 legacy branch"));
                }
            }
        }

        return found;
    }

    /// <summary>
    ///     Whether a type name, in a namespace, is one of the three #594
    ///     removed. <c>AppReducer</c> is deliberately qualified: the LIVE
    ///     reducer of the same name lives in <c>Harbor.Ui.Framework.State</c>.
    /// </summary>
    internal static bool IsBannedDeclaration(string typeName, string declaringNamespace) =>
        (typeName, declaringNamespace) switch
        {
            ("AppReducer", "Harbor.Ui.Framework.Reducers") => true,
            ("AppStore", "Harbor.Ui.Framework.State") => true,
            ("EventBusAppStoreDispatcher", _) => true,
            _ => false,
        };

    /// <summary>Whether a repo-relative path is one of the rule-4 live files.</summary>
    internal static bool IsRequiredLiveFile(string relative) =>
        RequiredLiveFiles.Any(p => string.Equals(p, relative, StringComparison.Ordinal));

    /// <summary>
    ///     Scans one project or solution file for a mention of the deleted
    ///     project. XML comments are stripped, so a disabled
    ///     <c>&lt;ProjectReference&gt;</c> left in place as a comment is not
    ///     reported as a live edge.
    /// </summary>
    internal static List<LegacyBranchViolation> FindProjectMentions(string relativeFile, string[] lines)
    {
        var found = new List<LegacyBranchViolation>();
        bool inComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            var sb = new StringBuilder(line.Length);
            int cursor = 0;

            // Two `break`s out of this loop, both on a terminal condition, so
            // `cursor` is never read after being parked at `line.Length` — hence
            // the S1854 fix below rather than an assignment to satisfy the
            // analyzer.
            while (cursor < line.Length)
            {
                if (inComment)
                {
                    int close = line.IndexOf("-->", cursor, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        break;
                    }

                    cursor = close + 3;
                    inComment = false;
                    continue;
                }

                int open = line.IndexOf("<!--", cursor, StringComparison.Ordinal);
                if (open < 0)
                {
                    sb.Append(line, cursor, line.Length - cursor);
                    break;
                }

                sb.Append(line, cursor, open - cursor);
                cursor = open + 4;
                inComment = true;
            }

            if (sb.ToString().Contains(DeletedProjectName, StringComparison.Ordinal))
            {
                found.Add(new LegacyBranchViolation(
                    relativeFile.EndsWith(".slnx", StringComparison.Ordinal)
                        ? "solution-entry"
                        : "project-reference",
                    relativeFile,
                    i + 1,
                    relativeFile + " still names the deleted project " + DeletedProjectName));
            }
        }

        return found;
    }

    /// <summary>
    ///     Every matching file under the named repository-relative roots.
    /// </summary>
    /// <remarks>
    ///     Walked by hand rather than with
    ///     <see cref="SearchOption.AllDirectories" /> so the skipped directories
    ///     are PRUNED instead of filtered after the fact: a full recursive
    ///     enumeration descends into <c>.git/</c> and the <c>external/</c>
    ///     submodule on every run, and this file runs in the arch gate during
    ///     <c>dotnet build</c> as well as in every test shard. An unreadable
    ///     directory is skipped rather than thrown, because a guard must not
    ///     fail the build for a permission it did not ask about.
    /// </remarks>
    private static IEnumerable<string> EnumerateFiles(string repoRoot, string[] roots, string pattern)
    {
        foreach (string root in roots)
        {
            string absolute = Path.Combine(repoRoot, root);
            if (!Directory.Exists(absolute))
            {
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(absolute);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                string[] files;
                try
                {
                    files = Directory.GetFiles(current, pattern);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (string file in files)
                {
                    yield return file;
                }

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
                    if (IsSkippedDirectory(directory))
                    {
                        continue;
                    }

                    pending.Push(directory);
                }
            }
        }
    }

    /// <summary>Whether a directory's NAME is one this walk never descends into.</summary>
    private static bool IsSkippedDirectory(string absolutePath)
    {
        string name = Path.GetFileName(absolutePath);
        return SkippedDirectories.Contains(name, StringComparer.Ordinal);
    }

    /// <summary>
    ///     A <c>namespace</c> declaration, file-scoped (<c>namespace X;</c>) or
    ///     block (<c>namespace X</c> before a brace). The last one seen wins,
    ///     which is exact for this repo's one-namespace-per-file style and is
    ///     stated here rather than pretended otherwise.
    /// </summary>
    [GeneratedRegex(@"\bnamespace\s+(?<name>[\w.]+)")]
    private static partial Regex NamespaceDeclaration();

    /// <summary>
    ///     A type declaration's keyword and name, at any modifier depth. Records
    ///     and structs count: the rule is about a branch, not about a keyword.
    /// </summary>
    [GeneratedRegex(@"\b(?:class|struct|record|interface)\s+(?<name>\w+)")]
    private static partial Regex TypeKeyword();

    private static string MakeRelative(string repoRoot, string path) =>
        Path.GetRelativePath(repoRoot, path).Replace(Path.DirectorySeparatorChar, '/');
}

/// <summary>
///     Guard for issue #594: the legacy flat <c>AppState</c> projection branch is
///     gone, and the one live home of the <c>AgentEvent</c> fold —
///     <c>UiStore</c> → <c>ChatAppReducer</c> — is still there.
/// </summary>
public sealed class LegacyFlatTeaBranchRule
{
    private static readonly Lazy<LegacyBranchReport> Report = new(
        () => LegacyFlatTeaBranchProbe.Scan(RepoPaths.RepoRoot));

    // =====================================================================
    // 1. The rule.
    // =====================================================================

    /// <summary>
    ///     Rules 1–3: no source file in the deleted project, no project or
    ///     solution file naming it, and none of the three dead types declared.
    ///     #594 shipped a whole second TEA branch whose <c>AppReducer</c> doc
    ///     claimed every renderer funnelled through it; no composition root ever
    ///     constructed it.
    /// </summary>
    [Test]
    public async Task LegacyFlatProjectionBranch_IsNotInTheTree()
    {
        var violations = Report.Value.Violations;

        await Assert.That(violations).IsEmpty()
            .Because(
                "the #594 legacy branch must not come back. It was a second home for the AgentEvent → "
                + "UI-state fold that no composition root ever constructed: the renderers dispatch into "
                + "UiStore and reduce through Harbor.Ui.Framework.State's ChatAppReducer, and the dead "
                + "AppReducer/AppStore/EventBusAppStoreDispatcher were reachable only from each other and "
                + "from two benchmark files. A ProjectReference to the deleted project also breaks the "
                + "build on its own, but a bare directory of unbuilt .cs files does not — so the path and "
                + "the declarations are both checked. Found: "
                + Describe(violations));
    }

    // =====================================================================
    // 2. The half that makes the rule safe to satisfy.
    // =====================================================================

    /// <summary>
    ///     Rule 4: the live fold is still in the tree. Without this, rules 1–3
    ///     are satisfied by deleting <c>Harbor.Ui.Framework.State</c>'s reducer
    ///     pair — turning a cleanup into an outage that passes every gate.
    /// </summary>
    [Test]
    public async Task LiveUiStoreToChatAppReducerFold_IsStillInTheTree()
    {
        var seen = Report.Value.LiveFilesFound;

        await Assert.That(seen.Count).IsEqualTo(LegacyFlatTeaBranchProbe.RequiredLiveFiles.Length)
            .Because(
                "this assertion is what stops the #594 rule from being satisfied by deleting the WORKING "
                + "path. renderer → UiStore → ChatAppReducer is the live fold; removing those two files "
                + "would leave every renderer without state, and rules 1–3 would report a clean tree. "
                + "Present: " + Describe(seen));

        foreach (string required in LegacyFlatTeaBranchProbe.RequiredLiveFiles)
        {
            await Assert.That(seen.Any(p => string.Equals(p, required, StringComparison.Ordinal))).IsTrue()
                .Because(required + " is the live half of the invariant. If it is genuinely gone, the TEA "
                                  + "read path moved somewhere else, and THAT change — not #594 — is what "
                                  + "the docs and the renderers have to be updated for.");
        }
    }

    // =====================================================================
    // 3. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The scan really walked a checkout, and it really found the live files
    ///     rule 4 grades. "No violations" is trivially satisfied by a scan that
    ///     read nothing, so the inventory itself is asserted.
    /// </summary>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan needs a repository checkout; without one it reports zero files and every "
                   + "rule above is satisfied by having nothing to look at");

        var report = Report.Value;

        await Assert.That(report.FilesScanned).IsGreaterThan(0)
            .Because("the scan read no .cs file under src/ or apps/; the roots or the file filter are "
                   + "wrong and the rule is vacuously green");

        await Assert.That(report.ProjectFilesScanned).IsGreaterThan(0)
            .Because("the scan read no .csproj and no .slnx, so rule 2 — the half that keeps a declared "
                   + "edge from outliving its producer — is enforcing nothing");

        await Assert.That(report.LiveFilesFound.Count).IsEqualTo(
                LegacyFlatTeaBranchProbe.RequiredLiveFiles.Length)
            .Because("the scan must find the live reducer pair by name before the rule may claim they are "
                   + "present. A scan that cannot find what should be there is a scan whose absence means "
                   + "nothing. Found: " + Describe(report.LiveFilesFound));
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The probe is handed the #594 shape — a file at
    ///     the deleted path, the dead <c>AppReducer</c> in the deleted
    ///     namespace, the dead <c>AppStore</c> in the State namespace, the dead
    ///     dispatcher in a surviving project, a <c>&lt;ProjectReference&gt;</c>
    ///     to the deleted project and a solution entry for it — and five
    ///     decoys it must NOT report, including the LIVE <c>AppReducer</c> that
    ///     shares its name with the dead one.
    /// </summary>
    [Test]
    public async Task NonVacuity_Scan_DetectsTheLegacyBranchInSyntheticSources()
    {
        List<(string Relative, string[] Lines)> legacy = new()
        {
            // (1) The dead top-level reducer, in the deleted project's namespace.
            ("src/Harbor.Ui.Framework.Reducers/AppReducer.cs",
            [
                "using Harbor.Abstractions.Events;",
                string.Empty,
                "namespace Harbor.Ui.Framework.Reducers;",
                string.Empty,
                "public static partial class AppReducer",
                "{",
                "    public static object Reduce(object @event, object state) => state;",
                "}",
            ]),
            // (2) The dead store — note it lived in the State NAMESPACE even
            // though the file sat in the Reducers directory.
            ("src/Harbor.Ui.Framework.Reducers/AppStore.cs",
            [
                "namespace Harbor.Ui.Framework.State;",
                string.Empty,
                "public sealed class AppStore",
                "{",
                "    public void Dispatch(object @event) { }",
                "}",
            ]),
            // (3) The dispatcher that fed it, in a project that survives.
            ("src/Harbor.Ui.Framework.Services/EventBusAppStoreDispatcher.cs",
            [
                "namespace Harbor.Ui.Framework.Services;",
                string.Empty,
                "public sealed class EventBusAppStoreDispatcher : IAsyncDisposable",
                "{",
                "    public ValueTask DisposeAsync() => ValueTask.CompletedTask;",
                "}",
            ]),
        };

        List<(string Relative, string[] Lines)> projectFiles = new()
        {
            // (4) A live consumer still holding the edge.
            ("src/Harbor.Ui.Framework.Services/Harbor.Ui.Framework.Services.csproj",
            [
                "<Project Sdk=\"Microsoft.NET.Sdk\">",
                "  <ItemGroup>",
                "    <ProjectReference Include=\"..\\Harbor.Ui.Framework.Reducers\\Harbor.Ui.Framework.Reducers.csproj\"/>",
                "  </ItemGroup>",
                "</Project>",
            ]),
            // (5) A solution that still lists the deleted project.
            ("Harbor.Samples.slnx",
            [
                "<Solution>",
                "    <Project Path=\"src/Harbor.Ui.Framework.Reducers/Harbor.Ui.Framework.Reducers.csproj\" />",
                "</Solution>",
            ]),
            // (6) A DECOY: the same reference, commented out. Not a live edge.
            ("src/Harbor.Benchmarks/Harbor.Benchmarks.csproj",
            [
                "<Project Sdk=\"Microsoft.NET.Sdk\">",
                "  <ItemGroup>",
                "    <!-- <ProjectReference Include=\"..\\Harbor.Ui.Framework.Reducers\\x.csproj\"/> -->",
                "  </ItemGroup>",
                "</Project>",
            ]),
        };

        List<(string Relative, string[] Lines)> decoys = new()
        {
            // The LIVE reducer. Same simple name, different namespace: a rule
            // keyed on the name alone would condemn the working one.
            ("src/Harbor.Ui.Framework.State/State/AppReducer.cs",
            [
                "namespace Harbor.Ui.Framework.State;",
                string.Empty,
                "public static class AppReducer",
                "{",
                "    public static object Update(object state, object msg) => state;",
                "}",
            ]),
            // The live store, by its real name. Naming it in a doc comment is
            // prose, not a declaration.
            ("src/Harbor.Ui.Framework.State/AppState.cs",
            [
                "namespace Harbor.Ui.Framework.State;",
                string.Empty,
                "/// <summary>Legacy flat state. Not used by AppStore.</summary>",
                "public sealed record AppStateHolder",
                "{",
                "}",
            ]),
            // A record whose name merely CONTAINS AppStore is a different type.
            ("src/Harbor.Ui.Framework.State/AppStoreFactory.cs",
            [
                "namespace Harbor.Ui.Framework.State;",
                string.Empty,
                "public sealed class AppStoreFactory",
                "{",
                "}",
            ]),
            // A same-named type in a different namespace is not the dead store.
            ("src/Harbor.Other.Place/Elsewhere.cs",
            [
                "namespace Harbor.Some.Other.Place;",
                string.Empty,
                "public sealed class AppStore",
                "{",
                "}",
            ]),
            // A dispatcher-shaped name in the same namespace, different name.
            ("src/Harbor.Ui.Framework.Services/SomethingElse.cs",
            [
                "namespace Harbor.Ui.Framework.Services;",
                string.Empty,
                "public sealed class EventBusSomethingElse",
                "{",
                "}",
            ]),
        };

        // The decoys ride through the SAME probe, so a matcher that stopped
        // matching shows up as extra findings rather than as a silent pass.
        foreach (var decoy in decoys)
        {
            LegacyBranchReport decoyReport = LegacyFlatTeaBranchProbe.ScanFiles([decoy], []);
            await Assert.That(decoyReport.Violations).IsEmpty()
                .Because(
                    decoy.Relative + " must not be reported. The live AppReducer shares its simple name "
                    + "with the deleted one, and the rest are prose or near-misses; a rule that cannot tell "
                    + "them apart condemns the working TEA path. Reported: " + Describe(decoyReport.Violations));
        }

        LegacyBranchReport report = LegacyFlatTeaBranchProbe.ScanFiles(legacy, projectFiles);

        await Assert.That(string.Join(" | ", report.Violations.Select(v => v.Kind + "@" + v.File))).IsEqualTo(
                "solution-entry@Harbor.Samples.slnx"
                + " | source-in-deleted-project@src/Harbor.Ui.Framework.Reducers/AppReducer.cs"
                + " | dead-type-declared@src/Harbor.Ui.Framework.Reducers/AppReducer.cs"
                + " | source-in-deleted-project@src/Harbor.Ui.Framework.Reducers/AppStore.cs"
                + " | dead-type-declared@src/Harbor.Ui.Framework.Reducers/AppStore.cs"
                + " | dead-type-declared@src/Harbor.Ui.Framework.Services/EventBusAppStoreDispatcher.cs"
                + " | project-reference@src/Harbor.Ui.Framework.Services/Harbor.Ui.Framework.Services.csproj")
            .Because(
                "this is the #594 shape, item for item: a .cs file at the deleted path, the dead "
                + "AppReducer in the deleted namespace, the dead AppStore in the State namespace, the dead "
                + "dispatcher in a surviving project, a ProjectReference to the deleted project, and a "
                + "solution entry for it — seven findings. The commented-out reference must NOT appear. "
                + "A miss on any of them means the rule cannot see the branch it was written for. "
                + "Reported: " + Describe(report.Violations));

        await Assert.That(report.FilesScanned).IsEqualTo(3)
            .Because("the three synthetic sources must all be read; a discovery filter that dropped one "
                   + "would leave the corresponding rule unexercised");
    }

    private static string Describe(IReadOnlyList<LegacyBranchViolation> violations) =>
        violations.Count == 0
            ? "(nothing)"
            : string.Join(" | ", violations.Select(v => v.Kind + " at " + v.File + ":" + v.Line));

    private static string Describe(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? "(nothing)" : string.Join(" | ", paths.OrderBy(p => p, StringComparer.Ordinal));
}
