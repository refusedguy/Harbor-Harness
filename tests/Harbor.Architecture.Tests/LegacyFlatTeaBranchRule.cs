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
//   3. None of the dead types is DECLARED under `src/` or `apps/`:
//      - the #594 trio: `AppReducer` in `Harbor.Ui.Framework.Reducers`,
//        `AppStore` in `Harbor.Ui.Framework.State`, `EventBusAppStoreDispatcher`
//        anywhere;
//      - the #597 additions: the four flat state records, and the two
//        `State`-namespace VIEW-MODEL SHADOWS `ChatLineViewModel` /
//        `ToolCallViewModel` (see below).
//   4. THE LIVE HALF IS STILL THERE: `State/ChatAppReducer.cs` and
//      `State/UiStore.cs` must exist.
//
// THE TWO SHADOWS ARE THE PART #597 ADDS, AND THEY ARE NOT "ALSO DEAD RECORDS"
//   They are a DUPLICATE-NAME collision, which is the failure this repo has
//   already paid for twice (#558 hid a dead copy behind a live name; #920
//   inverted a stripper and blanked 845 lines). `ChatViewState.cs` declared
//   `Harbor.Ui.Framework.State.ChatLineViewModel` and
//   `Harbor.Ui.Framework.State.ToolCallViewModel` — same simple names as the
//   LIVE `Harbor.Ui.Framework.ViewModels` pair that every consumer actually
//   binds to. Measured on this branch: 8 files use `ChatLineViewModel` and 12 use
//   `ToolCallViewModel`, and every one of them reaches the ViewModels copy via
//   an alias, a `using`-narrowing, or full qualification. Zero bind to the State
//   copies. So the hazard is not "someone reads the wrong record" — it is that a
//   new file that does `using Harbor.Ui.Framework.State;` and writes the bare
//   name gets CS0104, or picks the shadow and silently projects the wrong shape.
//   That is why they are matched by (name, namespace) like the reducers, and why
//   the positive control carries the LIVE ViewModels pair as the decoy that must
//   NOT be reported.
//
// WHY THE FOUR RECORDS ARE IN SCOPE NOW AND WERE NOT IN #594
//   #594 deleted the branch that produced them and deliberately left the records,
//   recording the decision in docs/ROADMAP.md as an open item. #597 is that item.
//   Measured per kind of "zero", because a record can be zero in one sense and
//   not another, and the difference is the whole decision:
//     AppState            0 writers, 0 prod readers, 0 registrations, 0 impls —
//                         but 2 BENCHMARK readers. Those benchmarks measure a
//                         shape (`ImmutableArray` record tree + selectors) that is
//                         real and still measured; they were pointed at the dead
//                         record. They are REPOINTED at `UiState`, which carries
//                         every field they touch, so the measurement survives and
//                         the dead record does not.
//     ChatViewState       0 writers, 0 readers anywhere (prod AND tests), 0 regs.
//                         Plus the two shadows. Dead outright.
//     ChromeViewState     0 writers, 0 readers — but ONE live `<see cref>` from
//                         Harbor.Desktop.Abstractions. A doc reference is not a
//                         reader; the cref is repointed at the live toast shape.
//     SessionsViewState   0 writers, 0 readers — BUT ITS FILE ALSO DECLARES
//                         `SessionInfo`, which is LIVE: 9 prod sites including a
//                         `new SessionInfo(...)` in the CLI's SessionSwitchManager
//                         and `ChatDomainState.Sessions`. The RECORD is dead; the
//                         FILE is not. `SessionInfo` moves to its own file so the
//                         record can go without taking a live type with it.
//
//   A file that holds both a dead record and a live type is the shape this rule
//   has to be careful about: deleting the file by path would delete the live type
//   with it, and every existing gate would be green. Hence the split.
//
// PERIMETER IS UNCHANGED AND STATED AGAIN BECAUSE IT NOW MATTERS TWICE
//   `src/` + `apps/` for declarations, whole repo for project/solution references.
//   `contrib/` holds its own `AppStore` and its own `ChatLineViewModel` and is
//   unmaintained, outside CI, and out of scope by owner decision (AGENTS.md).
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
// (`contrib/tui/Harbor.Tui.SpectreTui/State/AppStore.cs`) and its own
// `ChatLineViewModel`, and is unmaintained, outside CI, and out of scope by owner
// decision (AGENTS.md) — it is neither scanned nor expected to be clean, and that
// gap is deliberate rather than implicit.
//
// NON-VACUITY
// -----------
//   1. Scan_IsLive — the scan walked a checkout, read real files, and found the
//      live `ChatAppReducer` it is about to assert on. A rule satisfied by
//      looking at nothing is the failure this repo has already paid for twice.
//   2. NonVacuity_Scan_DetectsTheLegacyBranchInSyntheticSources — THE POSITIVE
//      CONTROL. The probe is handed the #594 + #597 shape item for item (a file
//      at the deleted path, the dead `AppReducer` in the deleted namespace, the
//      dead `AppStore` in the State namespace, the dead dispatcher in a surviving
//      project, a `<ProjectReference>` to the deleted project, a solution entry
//      for it, the four flat records, the view-model shadow) and nine decoys it
//      must NOT report — including the LIVE `AppReducer` that shares its name
//      with the dead one, the LIVE `ChatLineViewModel` / `ToolCallViewModel`
//      twins, the LIVE `SessionInfo` that shares a file with a dead record, a
//      commented-out reference, and a near-miss name.
//   3. NonVacuity_LiveViewModelTwinsAreNotReported — the twins are fed to the
//      real matcher one per pair and asserted unreported, so "the rule
//      distinguishes a shadow from its twin" has a witness rather than resting
//      on the decoys happening to be scanned. #880's lesson, inverted.

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
    ///     Scans one C# file for the dead type declarations. Comments are
    ///     stripped first, so the XML docs that name <c>AppStore</c>,
    ///     <c>EventBusAppStoreDispatcher</c> or <c>ChatViewState</c> cannot be
    ///     mistaken for a declaration — and the stripper is
    ///     <see cref="SourceCommentStripper.StripAll" />, whose lexing was
    ///     corrected in #919 after #920 found it inverted (#920 blanked 845 real
    ///     code lines across 5 files while preserving <c>"src/*"</c>).
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
    ///     Whether a type name, in a namespace, is one of the #594 / #597 removed
    ///     types. Every entry is matched by (name, namespace), never by name alone:
    ///     <c>AppReducer</c> and both <c>*ViewModel</c> names are ALSO the live
    ///     names, in <c>Harbor.Ui.Framework.State</c> and
    ///     <c>Harbor.Ui.Framework.ViewModels</c> respectively.
    /// </summary>
    internal static bool IsBannedDeclaration(string typeName, string declaringNamespace) =>
        (typeName, declaringNamespace) switch
        {
            // ── #594: the deleted Reducers branch. ──
            ("AppReducer", "Harbor.Ui.Framework.Reducers") => true,
            ("AppStore", "Harbor.Ui.Framework.State") => true,
            ("EventBusAppStoreDispatcher", _) => true,

            // ── #597: the four producer-less flat state records. They live in a
            //    project that is very much alive, so the namespace is named
            //    explicitly rather than by a path rule: the record is what's
            //    banned, not the directory.
            ("AppState", "Harbor.Ui.Framework.State") => true,
            ("ChatViewState", "Harbor.Ui.Framework.State") => true,
            ("ChromeViewState", "Harbor.Ui.Framework.State") => true,
            ("SessionsViewState", "Harbor.Ui.Framework.State") => true,

            // ── #597: the two State-namespace view-model SHADOWS. The LIVE copies
            //    are in Harbor.Ui.Framework.ViewModels and are what every consumer
            //    binds to; a name-keyed rule would condemn those and pass these.
            ("ChatLineViewModel", "Harbor.Ui.Framework.State") => true,
            ("ToolCallViewModel", "Harbor.Ui.Framework.State") => true,

            _ => false,
        };

    /// <summary>
    ///     The live twin of each banned shadow, named so the positive control can
    ///     assert the rule does NOT report it.
    /// </summary>
    /// <remarks>
    ///     Declared as (simpleName, namespace) rather than as a path because the
    ///     rule matches on the pair — a control that asserted on a path while the
    ///     rule matched on a namespace would be testing something else.
    /// </remarks>
    internal static readonly (string TypeName, string Namespace)[] LiveShadowTwins =
    [
        ("ChatLineViewModel", "Harbor.Ui.Framework.ViewModels"),
        ("ToolCallViewModel", "Harbor.Ui.Framework.ViewModels"),
    ];

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
    ///     solution file naming it, and none of the dead types declared —
    ///     neither the #594 trio nor the #597 records and view-model shadows.
    ///     #594 shipped a whole second TEA branch whose <c>AppReducer</c> doc
    ///     claimed every renderer funnelled through it; no composition root ever
    ///     constructed it. #597 removed what it left behind: four
    ///     producer-less flat records and two same-named view-model shadows whose
    ///     live twins are in <c>Harbor.Ui.Framework.ViewModels</c>.
    /// </summary>
    [Test]
    public async Task LegacyFlatProjectionBranch_IsNotInTheTree()
    {
        var violations = Report.Value.Violations;

        await Assert.That(violations).IsEmpty()
            .Because(
                "the #594 legacy branch must not come back, and neither must the #597 residue. #594 was a "
                + "second home for the AgentEvent → UI-state fold that no composition root ever "
                + "constructed: the renderers dispatch into UiStore and reduce through "
                + "Harbor.Ui.Framework.State's ChatAppReducer, and the dead "
                + "AppReducer/AppStore/EventBusAppStoreDispatcher were reachable only from each other. "
                + "The four flat records (#597) had no writer at all in src/ or apps/ once that branch "
                + "went, and the two State-namespace view-model shadows share their simple names with the "
                + "LIVE ViewModels pair — the duplicate-name shape that hides a dead copy. A ProjectReference "
                + "to the deleted project also breaks the build on its own, but a bare directory of unbuilt "
                + ".cs files does not — so the path and the declarations are both checked, and every "
                + "declaration is matched by (name, namespace) so the live twins are never condemned. "
                + "Found: " + Describe(violations));
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
    ///     THE POSITIVE CONTROL. The probe is handed the #594 + #597 shape — a
    ///     file at the deleted path, the dead <c>AppReducer</c> in the deleted
    ///     namespace, the dead <c>AppStore</c> in the State namespace, the dead
    ///     dispatcher in a surviving project, a <c>&lt;ProjectReference&gt;</c>
    ///     to the deleted project and a solution entry for it, plus the four
    ///     producer-less flat records and the view-model shadow — and nine decoys
    ///     it must NOT report, including the LIVE <c>AppReducer</c> and the LIVE
    ///     <c>ChatLineViewModel</c> / <c>ToolCallViewModel</c> twins that share
    ///     their simple names with dead ones.
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
            // (3b) #597: one of the four producer-less flat records, back in the
            // live State project. The record is what is banned, not the path.
            ("src/Harbor.Ui.Framework.State/AppState.cs",
            [
                "namespace Harbor.Ui.Framework.State;",
                string.Empty,
                "public sealed record AppState",
                "{",
                "}",
            ]),
            // (3c) #597: the record that SHARES a file with a live type. If the
            // rule were a path rule this file would be untouchable; matched by
            // (name, namespace) the dead record is reportable and `SessionInfo`
            // is not — which is what lets the record go without the live type.
            ("src/Harbor.Ui.Framework.State/SessionsViewState.cs",
            [
                "namespace Harbor.Ui.Framework.State;",
                string.Empty,
                "public sealed record SessionsViewState",
                "{",
                "}",
            ]),
            // (3d) #597: a view-model SHADOW — same simple name as the live
            // ViewModels copy, wrong namespace. This is the duplicate-name shape
            // that makes a name-keyed rule wrong.
            ("src/Harbor.Ui.Framework.State/ChatViewState.cs",
            [
                "namespace Harbor.Ui.Framework.State;",
                string.Empty,
                "public sealed record ToolCallViewModel(string Id)",
                "{",
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
            // THE LIVE VIEW-MODEL TWINS. Same simple names as the #597 shadows,
            // different namespace, and these are the copies every consumer binds
            // to. A rule keyed on the name alone condemns the working pair — the
            // #558 mistake again, and the reason IsBannedDeclaration is keyed on
            // (name, namespace).
            ("src/Harbor.Ui.Framework.ViewModels/ViewModels/ChatLineViewModel.cs",
            [
                "namespace Harbor.Ui.Framework.ViewModels;",
                string.Empty,
                "public sealed record ChatLineViewModel(ChatRole Role, string Text)",
                "{",
                "}",
            ]),
            ("src/Harbor.Ui.Framework.ViewModels/ViewModels/ToolCallViewModel.cs",
            [
                "namespace Harbor.Ui.Framework.ViewModels;",
                string.Empty,
                "public sealed partial class ToolCallViewModel : ObservableObject",
                "{",
                "}",
            ]),
            // THE LIVE TYPE THAT SHARES A FILE WITH A DEAD RECORD. `SessionInfo`
            // is constructed by the CLI's SessionSwitchManager and read by
            // ChatDomainState.Sessions; it must survive the deletion of
            // `SessionsViewState`. A rule that matched by path would either have
            // to exempt this file — leaving the dead record ungoverned — or ban
            // it and take a live type with it.
            ("src/Harbor.Ui.Framework.State/SessionInfo.cs",
            [
                "namespace Harbor.Ui.Framework.State;",
                string.Empty,
                "public sealed record SessionInfo(string Id, string Title)",
                "{",
                "}",
            ]),
            // A state record whose name merely CONTAINS a banned name is a
            // different type — the same near-miss the AppStore decoy covers.
            ("src/Harbor.Ui.Framework.State/ChatViewStateCache.cs",
            [
                "namespace Harbor.Ui.Framework.State;",
                string.Empty,
                "public sealed record ChatViewStateCache",
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
                + " | project-reference@src/Harbor.Ui.Framework.Services/Harbor.Ui.Framework.Services.csproj"
                + " | dead-type-declared@src/Harbor.Ui.Framework.State/AppState.cs"
                + " | dead-type-declared@src/Harbor.Ui.Framework.State/ChatViewState.cs"
                + " | dead-type-declared@src/Harbor.Ui.Framework.State/SessionsViewState.cs")
            .Because(
                "this is the #594 + #597 shape, item for item: a .cs file at the deleted path, the dead "
                + "AppReducer in the deleted namespace, the dead AppStore in the State namespace, the dead "
                + "dispatcher in a surviving project, a ProjectReference to the deleted project, and a "
                + "solution entry for it — plus the four producer-less flat records and the view-model "
                + "SHADOW, which is the duplicate-name shape #597 adds. Ten findings. The commented-out "
                + "reference, the live ViewModels twins, the live SessionInfo and the near-miss "
                + "ChatViewStateCache must NOT appear. A miss on any of them means the rule cannot see the "
                + "branch it was written for; a hit on one of them means it condemns the working code. The "
                + "order is the probe's own ordinal sort by file then line — `Services` before `State`, "
                + "because 'e' < 't' — so this string asserts the ordering, not just the set. "
                + "Reported: " + Describe(report.Violations));

        await Assert.That(report.FilesScanned).IsEqualTo(6)
            .Because("all six synthetic sources must be read; a discovery filter that dropped one "
                   + "would leave the corresponding rule unexercised");
    }

    /// <summary>
    ///     The live view-model twins are named by <see cref="LiveShadowTwins" /> and
    ///     the rule is keyed on (name, namespace), so the twins are non-findings BY
    ///     CONSTRUCTION rather than by luck. Asserting it directly is what keeps a
    ///     future edit to <see cref="LegacyFlatTeaBranchProbe.IsBannedDeclaration" />
    ///     from quietly turning a namespace-keyed rule into a name-keyed one.
    /// </summary>
    /// <remarks>
    ///     This is the #880 failure inverted: a guard that is green because the
    ///     decoys happen not to be scanned is not a guard. Here the twins are fed to
    ///     the real matcher and asserted unreported, one per pair, so "the rule
    ///     distinguishes the shadow from its twin" is a claim with a witness.
    /// </remarks>
    [Test]
    public async Task NonVacuity_LiveViewModelTwinsAreNotReported()
    {
        foreach ((string typeName, string twinNamespace) in LegacyFlatTeaBranchProbe.LiveShadowTwins)
        {
            LegacyBranchReport report = LegacyFlatTeaBranchProbe.ScanFiles(
                [("src/Harbor.Ui.Framework.ViewModels/ViewModels/" + typeName + ".cs",
                    [
                        "namespace " + twinNamespace + ";",
                        string.Empty,
                        "public sealed record " + typeName,
                        "{",
                        "}",
                    ])],
                []);

            await Assert.That(report.Violations).IsEmpty()
                .Because(
                    typeName + " in " + twinNamespace + " is the LIVE copy every consumer binds to: "
                    + "8 sites use ChatLineViewModel and 12 use ToolCallViewModel, all of which reach the "
                    + "ViewModels declaration. The State-namespace shadow of the same name is what #597 "
                    + "bans, and a rule that cannot tell the two apart condemns the working one. "
                    + "Reported: " + Describe(report.Violations));
        }
    }

    private static string Describe(IReadOnlyList<LegacyBranchViolation> violations) =>
        violations.Count == 0
            ? "(nothing)"
            : string.Join(" | ", violations.Select(v => v.Kind + " at " + v.File + ":" + v.Line));

    private static string Describe(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? "(nothing)" : string.Join(" | ", paths.OrderBy(p => p, StringComparer.Ordinal));
}
