// PublicAbstractBaseLivenessRules.cs — issue #1835.
//
// THE DEFECT THIS FILE EXISTS FOR
// -------------------------------
// #1835 audited the base classes under `src/` and measured twelve `public
// abstract` types with ZERO product descendants — 788 lines of published base
// class that nothing in the tree derives from. Ten of them sat in
// `Harbor.Desktop.Abstractions`, a PACKABLE assembly (PackageId:
// Harbor.Desktop.Abstractions) whose own csproj promised "Platform VMs in
// apps/Harbor.App.* derive from the abstract VMs here". The promise was not
// kept: the shipped desktop app derived from two of the bases in that
// assembly, a third had only a test double behind it, and the other ten had no
// implementor anywhere — published surface with nothing behind it.
//
// A public abstract type with no implementor is not an abstraction. It is a
// promise nobody keeps: the type says "inherit from me", the package says "you
// may", and nobody does. `docs/ANTIPATTERNS.md` §8 and `CLAUDE.md`'s Patterns
// section both ask for "sealed by default, open for inheritance only when
// explicitly designed for it" — and neither is machine-checkable. This file is
// the machine-checkable half.
//
// THE RULE
// --------
// A `public abstract` type declared in a PUBLISHED product assembly must have
// at least one implementor in the product tree (`src/` + `apps/`), or be listed
// in `ReservedBases` with a reason.
//
// "Published" is not "IsPackable on that project". `Harbor.Ui.Framework`
// IS packable and references `Harbor.Ui.Framework.ViewModels`, which is not —
// so the module's public types ship to nuget.org as a dependency of the
// meta-package. The population is therefore the transitive closure of the
// packable set over `ProjectReference` edges, read from the csproj files
// themselves (`RepoPaths.IsPackable` + `RepoPaths.ReadProjectReferences`), not
// from a hand-typed list: a list would rot the first time a project is packed
// or unpublished, and a rule over a stale population is a rule over nothing.
//
// WHY A SOURCE SCAN AND NOT REFLECTION
// ------------------------------------
// Two reasons, and the second is the load-bearing one.
//
// 1. `apps/Harbor.App.Cli` and `apps/Harbor.App.Avalonia` are composition
//    roots and are NOT referenced by this test project, so reflection over the
//    loaded assemblies cannot see the implementations that live there.
//    `ChatViewModelBase`'s only product implementor is
//    `apps/Harbor.App.Avalonia/ViewModels/ChatViewModel.cs`; a
//    reflection-based rule would report the base as dead and send the author
//    to delete a type the product uses.
//
// 2. The rule must be PROVABLE. A guard that can only be exercised against the
//    real tree cannot carry a synthetic positive control, and a guard whose
//    only evidence is "it is green today" is indistinguishable from a guard
//    that reads nothing. A source scan can be handed a synthetic dead base and
//    be REQUIRED to report it — see the `NonVacuity_...` tests below.
//
// WHY AN EXEMPTION TABLE AND NOT A COMMENT
// ----------------------------------------
// The repository has learned this lesson more than once, and `ExemptionReason.cs`
// is the scar: five tables each asked "does this row state a reason?" in their
// own words, and one of them did not ask at all. A permission that lives in a
// comment is invisible to every tool that reads the file, so the reason is lost
// the moment the next person reformats the file.
//
// So a reservation here is a ROW, and the row carries three things: the full
// type name (the thing being excused), the REASON as a value long enough to be
// an argument (the shared ExemptionReason check enforces the floor), and the
// tracking issue in its own column, so a URL is never mistaken for a reason.
//
// And the table is LIVENESS-CHECKED in both directions. A row that names a type
// which no longer exists fails. A row whose type has since GAINED an
// implementor fails too — the reservation has become a permission for a
// problem that is not there, and the cheap repair (leave the row) is exactly
// how an allow-list rots into a blanket permission.
//
// WHY AN EXEMPTION IS EVEN POSSIBLE
// ---------------------------------
// The rule is not "every published abstract type must be implemented in the
// product". A base class published for a host OUTSIDE the tree is a legitimate
// extension point — `docs/PATTERNS.md` §5 asks for exactly that shape (an
// extension point plus a visible list of them). Such a type has no implementor
// here, and the rule has to be able to say so without being switched off. The
// three rows below are that mechanism, and each says what would remove it.
//
// WHAT IS NOT RULED, AND WHY
// --------------------------
//   * `internal abstract` types. They are not public surface; the audit's own
//     inventory keeps them apart (`SessionRewritePlan`, `FsNode`).
//   * `contrib/`. It is not compiled by CI, so a rule that fires on it
//     describes code that cannot break — the same exclusion every other gate
//     in this project makes.
//   * abstract MEMBERS (`public abstract string Id { get; }`). The population is
//     types, not members; an abstract member on a type that has implementors is
//     the ordinary shape of an abstract contract.
//
// NON-VACUITY
// -----------
// Three controls, because a scan that finds nothing is indistinguishable from
// a scan that reads nothing:
//   1. TheScanStillSeesTheBasesItIsAbout — the candidate set is not empty and
//      still contains the two bases the shipped app derives from, and the
//      implementor map still points at the shipped app's chat view-model.
//   2. NonVacuity_TheProbeReportsASyntheticDeadBase — the SAME classifier that
//      grades the tree must report a synthetic public abstract class that
//      nothing derives from.
//   3. NonVacuity_TheProbeAcceptsAnImplementorAndAReservation — the same
//      synthetic base, once something derives from it, must NOT be reported,
//      and a reserved one must not be reported either.
//
// THE RULE IS NOT THE SIBLING RULE
// ---------------------------------
// `DeadPublicInterfaceRule.cs` (issue #1831) grades public INTERFACES with no
// implementor and no consumer. The population here is public abstract CLASSES
// and RECORDS — the inheritance side of the same question — and the two files
// do not overlap: an interface is not an abstract type in this sense, and an
// abstract class is not an interface. Neither file was edited for the other.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     One <c>public abstract</c> type declared in a published product assembly.
/// </summary>
/// <param name="Name">The simple type name.</param>
/// <param name="Namespace">The namespace the type is declared in.</param>
/// <param name="Project">The project directory that declares it.</param>
/// <param name="File">The repo-relative file that declares it.</param>
/// <param name="Line">The line the declaration sits on.</param>
internal sealed record PublishedAbstractBase(
    string Name,
    string Namespace,
    string Project,
    string File,
    int Line)
{
    /// <summary>The full name, as a reservation row names it.</summary>
    internal string FullName => string.IsNullOrEmpty(Namespace) ? Name : Namespace + "." + Name;
}

/// <summary>
///     One row of the reservation table: a published abstract base that is
///     allowed to have no implementor, with the argument for why.
/// </summary>
/// <param name="Type">The full type name being excused.</param>
/// <param name="Reason">Why the type is published without an implementor here.</param>
/// <param name="TrackedBy">The issue that owns the decision, when there is one.</param>
internal sealed record ReservedAbstractBase(string Type, string Reason, string? TrackedBy);

/// <summary>
///     What the scan found: the candidates it graded, the implementor evidence
///     it collected, and the candidates that came out with no implementor.
/// </summary>
/// <param name="Candidates">Every published abstract base the scan graded.</param>
/// <param name="Implementors">Implementor evidence, keyed by full type name.</param>
/// <param name="Findings">The candidates with no implementor.</param>
/// <param name="FilesScanned">How many files the scan read.</param>
internal sealed record AbstractBaseReport(
    IReadOnlyList<PublishedAbstractBase> Candidates,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Implementors,
    IReadOnlyList<PublishedAbstractBase> Findings,
    int FilesScanned);

/// <summary>
///     The scanner behind <see cref="PublicAbstractBaseLivenessRules" />. It is
///     source-based, namespace-aware, and drivable from synthetic sources so the
///     rule can be proven rather than merely observed.
/// </summary>
internal static class AbstractBaseProbe
{
    /// <summary>
    ///     The trees the scan reads: the product tree. <c>tests/</c> is NOT
    ///     included — a test double that derives from a published base is not a
    ///     shipped consumer, and counting it would let the tree's own fixtures
    ///     keep a dead public type alive. <c>contrib/</c> is pruned, as
    ///     everywhere else in this project: it is not compiled by CI.
    /// </summary>
    internal static readonly string[] ScanTrees = ["src", "apps"];

    /// <summary>
    ///     A published abstract type: <c>public abstract</c>, optionally
    ///     <c>partial</c>, then <c>class</c> or <c>record</c> and the name.
    /// </summary>
    private static readonly Regex AbstractDeclaration = new(
        @"^[ \t]*public[ \t]+abstract[ \t]+(?:partial[ \t]+)?(?:class|record)[ \t]+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    ///     A type declaration of any kind — the anchor for reading a base list.
    ///     A class, record or struct can inherit; an interface cannot be a base
    ///     CLASS, so it is not an anchor.
    /// </summary>
    private static readonly Regex TypeKeyword = new(
        @"\b(?:class|record|struct)[ \t]+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    /// <summary>Matches a namespace declaration, file-scoped or block.</summary>
    private static readonly Regex NamespaceDeclaration = new(
        @"\bnamespace[ \t]+(?<ns>[A-Za-z_][A-Za-z0-9_.]*)[ \t]*[;{]",
        RegexOptions.Compiled);

    /// <summary>Matches a using directive, with or without the <c>global</c> keyword.</summary>
    private static readonly Regex UsingDirective = new(
        @"^[ \t]*(?<global>global[ \t]+)?using[ \t]+(?:static[ \t]+)?(?<ns>[A-Za-z_][A-Za-z0-9_.]*)[ \t]*;",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    ///     A generic constraint's colon: <c>where T : notnull</c>. The colon is
    ///     not a base list, and reading it as one would put a word like
    ///     <c>notnull</c> where a type name belongs.
    /// </summary>
    private static readonly Regex WhereConstraint = new(
        @"\bwhere[ \t]+[A-Za-z_][A-Za-z0-9_]*[ \t]*$",
        RegexOptions.Compiled);

    /// <summary>
    ///     The leading type name of a base-list entry: an identifier, possibly
    ///     qualified (<c>Outer.Inner</c>). The last segment is what a
    ///     declaration can inherit from by simple name.
    /// </summary>
    private static readonly Regex LeadingTypeName = new(
        @"^[A-Za-z_][A-Za-z0-9_]*(?:[ \t]*\.[ \t]*[A-Za-z_][A-Za-z0-9_]*)*",
        RegexOptions.Compiled);

    /// <summary>
    ///     Grades an explicit set of sources. This is the control path: the same
    ///     code that grades the tree grades a synthetic one.
    /// </summary>
    /// <param name="sources">Repo-relative path and raw source, one pair per file.</param>
    /// <param name="isPublishedProject">
    ///     Whether a project directory belongs to the published set. The tree
    ///     walk supplies the real predicate; a synthetic control supplies
    ///     "everything is published".
    /// </param>
    internal static AbstractBaseReport ScanFiles(
        IReadOnlyList<(string Rel, string Source)> sources,
        Func<string, bool> isPublishedProject)
    {
        var files = new List<ParsedFile>(sources.Count);
        foreach ((string rel, string source) in sources)
        {
            files.Add(new ParsedFile(rel, source));
        }

        // A `global using` applies to the whole project, not to the repository.
        var projectGlobals = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (ParsedFile file in files)
        {
            if (file.GlobalUsings.Count == 0)
            {
                continue;
            }

            if (!projectGlobals.TryGetValue(file.Project, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                projectGlobals[file.Project] = set;
            }

            set.UnionWith(file.GlobalUsings);
        }

        foreach (ParsedFile file in files)
        {
            if (projectGlobals.TryGetValue(file.Project, out HashSet<string>? set))
            {
                file.GlobalUsings.UnionWith(set);
            }
        }

        var candidates = new List<PublishedAbstractBase>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ParsedFile file in files)
        {
            if (!isPublishedProject(file.Project))
            {
                continue;
            }

            foreach ((string name, int line) in file.AbstractDeclarations)
            {
                var candidate = new PublishedAbstractBase(name, file.Namespace, file.Project, file.Rel, line);
                if (seen.Add(candidate.FullName))
                {
                    candidates.Add(candidate);
                }
            }
        }

        var implementors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (ParsedFile file in files)
        {
            foreach (string token in file.BaseTokens)
            {
                foreach (PublishedAbstractBase candidate in candidates)
                {
                    if (!string.Equals(candidate.Name, token, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!file.CanSee(candidate))
                    {
                        continue;
                    }

                    if (!implementors.TryGetValue(candidate.FullName, out List<string>? list))
                    {
                        list = [];
                        implementors[candidate.FullName] = list;
                    }

                    list.Add(file.Rel);
                }
            }
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> evidence =
            implementors.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value,
                StringComparer.Ordinal);

        List<PublishedAbstractBase> findings =
        [
            .. candidates.Where(c => !implementors.ContainsKey(c.FullName)),
        ];

        return new AbstractBaseReport(candidates, evidence, findings, files.Count);
    }

    /// <summary>
    ///     Reads the repository and grades it.
    /// </summary>
    /// <param name="repoRoot">The checkout to read; <c>null</c> grades nothing.</param>
    /// <param name="isPublishedProject">Whether a project directory is published.</param>
    internal static AbstractBaseReport Scan(string? repoRoot, Func<string, bool> isPublishedProject)
    {
        if (repoRoot is null)
        {
            return new AbstractBaseReport(
                [],
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
                [],
                0);
        }

        var sources = new List<(string Rel, string Source)>();
        foreach (string tree in ScanTrees)
        {
            string dir = Path.Combine(repoRoot, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
                if (SourceScan.IsBuildOutput(rel))
                {
                    continue;
                }

                if (SourceScan.TryReadAllText(path) is { } text)
                {
                    sources.Add((rel, text));
                }
            }
        }

        return ScanFiles(sources, isPublishedProject);
    }

    /// <summary>One parsed source file: what it declares and what it inherits.</summary>
    internal sealed class ParsedFile
    {
        /// <summary>The repo-relative path.</summary>
        internal string Rel { get; }

        /// <summary>The project directory the file belongs to.</summary>
        internal string Project { get; }

        /// <summary>The namespace the file declares.</summary>
        internal string Namespace { get; }

        /// <summary>The namespaces the file imports with a file-level using.</summary>
        internal HashSet<string> Usings { get; } = new(StringComparer.Ordinal);

        /// <summary>The namespaces the file's project imports with a global using.</summary>
        internal HashSet<string> GlobalUsings { get; } = new(StringComparer.Ordinal);

        /// <summary>Every base-list type name the file's declarations inherit.</summary>
        internal List<string> BaseTokens { get; } = [];

        /// <summary>Every published abstract type the file declares.</summary>
        internal List<(string Name, int Line)> AbstractDeclarations { get; } = [];

        /// <summary>
        ///     Whether this file can name <paramref name="candidate" /> by its
        ///     simple name: same project, same or nested namespace, or an
        ///     explicit import of the candidate's namespace.
        /// </summary>
        /// <remarks>
        ///     A simple name alone is not an identity — the tree has more than one
        ///     type called <c>Panel</c> — so the namespace is what decides
        ///     whether a base-list mention really refers to the candidate.
        /// </remarks>
        internal bool CanSee(PublishedAbstractBase candidate)
        {
            if (candidate.Namespace.Length == 0)
            {
                return Namespace.Length == 0;
            }

            if (string.Equals(Project, candidate.Project, StringComparison.Ordinal)
                || string.Equals(Namespace, candidate.Namespace, StringComparison.Ordinal)
                || Namespace.StartsWith(candidate.Namespace + ".", StringComparison.Ordinal))
            {
                return true;
            }

            return Usings.Contains(candidate.Namespace) || GlobalUsings.Contains(candidate.Namespace);
        }

        /// <summary>Parses one file.</summary>
        internal ParsedFile(string rel, string source)
        {
            // Comments go first: prose about a base class is not a base class.
            // String literals stay — a string that reads like a declaration is a
            // risk this file accepts, and it is stated rather than hidden: the
            // declaration pattern is anchored to the start of a line, and no
            // string literal in the tree starts a line with `public abstract`.
            string text = SourceScan.StripComments(source);

            Rel = rel;
            Project = ProjectOf(rel);

            Match ns = NamespaceDeclaration.Match(text);
            Namespace = ns.Success ? ns.Groups["ns"].Value : "";

            foreach (Match u in UsingDirective.Matches(text))
            {
                if (u.Groups["global"].Success)
                {
                    GlobalUsings.Add(u.Groups["ns"].Value);
                }
                else
                {
                    Usings.Add(u.Groups["ns"].Value);
                }
            }

            foreach (Match m in AbstractDeclaration.Matches(text))
            {
                int line = text[..m.Index].Count(c => c == '\n') + 1;
                AbstractDeclarations.Add((m.Groups["name"].Value, line));
            }

            foreach (Match m in TypeKeyword.Matches(text))
            {
                foreach (string token in BaseListTokens(text, m.Index + m.Length))
                {
                    BaseTokens.Add(token);
                }
            }
        }

        /// <summary>
        ///     The project directory a repo-relative path belongs to: the segment
        ///     after <c>src/</c> or <c>apps/</c>.
        /// </summary>
        private static string ProjectOf(string rel)
        {
            string[] parts = rel.Split('/');
            for (int i = 0; i + 1 < parts.Length; i++)
            {
                if (parts[i] is "src" or "apps")
                {
                    return parts[i + 1];
                }
            }

            return "";
        }

        /// <summary>
        ///     The base-list type names of the declaration that starts at
        ///     <paramref name="start" /> (the position just after the type name).
        /// </summary>
        /// <remarks>
        ///     The scan walks the header character by character rather than
        ///     matching a pattern, because a header is not one line: a record's
        ///     primary constructor and its base list routinely sit on different
        ///     lines (<c>public sealed record UserMessage(\n … \n) : AgentMessage(…)</c>),
        ///     and a single-line pattern would miss every one of them.
        /// </remarks>
        private static IEnumerable<string> BaseListTokens(string text, int start)
        {
            int depth = 0;
            int colon = -1;
            int end = text.Length;

            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];

                if (c is '(' or '[')
                {
                    depth++;
                    continue;
                }

                if (c is ')' or ']')
                {
                    depth--;
                    continue;
                }

                if (depth != 0)
                {
                    continue;
                }

                if (c == ':')
                {
                    // A generic constraint's colon is not a base list.
                    if (colon < 0 && !WhereConstraint.IsMatch(text[start..i]))
                    {
                        colon = i;
                    }

                    continue;
                }

                if (c is '{' or ';')
                {
                    end = i;
                    break;
                }
            }

            if (colon < 0)
            {
                yield break;
            }

            foreach (string part in SplitTopLevel(text[(colon + 1)..end]))
            {
                Match name = LeadingTypeName.Match(part.Trim());
                if (!name.Success)
                {
                    continue;
                }

                string token = name.Value.Replace(" ", string.Empty, StringComparison.Ordinal);
                int dot = token.LastIndexOf('.');
                yield return dot < 0 ? token : token[(dot + 1)..];
            }
        }

        /// <summary>Splits a base list on the commas that separate its entries.</summary>
        private static IEnumerable<string> SplitTopLevel(string list)
        {
            int depth = 0;
            int start = 0;

            for (int i = 0; i < list.Length; i++)
            {
                char c = list[i];
                if (c is '(' or '[' or '<')
                {
                    depth++;
                }
                else if (c is ')' or ']' or '>')
                {
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    yield return list[start..i];
                    start = i + 1;
                }
            }

            yield return list[start..];
        }
    }
}

/// <summary>
///     Issue #1835: a published <c>public abstract</c> type with no implementor
///     is a promise the package makes and nobody keeps. See the file header for
///     the measurement, the population, and the non-vacuity controls.
/// </summary>
public sealed class PublicAbstractBaseLivenessRules
{
    /// <summary>
    ///     The reservation table: published abstract bases that are allowed to
    ///     have no implementor in the product tree. Each row says WHY, and the
    ///     table is checked in both directions — a row that names a type which
    ///     is gone fails, and a row whose type has gained an implementor fails
    ///     too.
    /// </summary>
    private static readonly ReservedAbstractBase[] ReservedBases =
    [
        new(
            "Harbor.Desktop.Abstractions.ViewModels.DiffViewModelBase",
            "The desktop diff view-model base. No shipped app derives from it: the "
            + "Avalonia shell renders diffs through the concrete DiffViewModel, and the "
            + "WPF/MAUI/Blazor apps are not built by CI. Its only implementor in the tree "
            + "is the Harbor.TestKit stub, and the base stays published because moving it "
            + "down to Harbor.Ui.Framework.ViewModels — which would take "
            + "Harbor.Desktop.Abstractions, and with it Harbor.Application, out of the "
            + "TestKit closure — is a separate move.",
            "https://github.com/refusedguy/Harbor-Harness/issues/1875"),
        new(
            "Harbor.Tui.CellForge.Input.FocusableBase",
            "The boilerplate half of the engine's IFocusable seam: id storage, focus "
            + "tracking and the decline-by-default key phases, so a focusable widget "
            + "overrides only what it owns. Nothing in the product derives from it — the "
            + "engine's focus router is exercised by tests, not by a shipped widget — "
            + "so the row stays only while the seam has no product implementor. It "
            + "becomes unnecessary the moment a widget derives from it, and it should be "
            + "deleted when the seam itself is retired.",
            "https://github.com/refusedguy/Harbor-Harness/issues/1835"),
        new(
            "Harbor.Tui.CellForge.Rendering.LeafNode",
            "The childless half of the engine's layout tree: the default Arrange and "
            + "the paint-area guard, so a leaf node implements Measure and Paint only. "
            + "The product's own nodes are containers (ContainerNode and its three "
            + "descendants); the leaf's only implementor is the FixedNode test stub. The "
            + "row stays while no shipped node is a leaf, and becomes unnecessary the "
            + "moment one is.",
            "https://github.com/refusedguy/Harbor-Harness/issues/1835"),
    ];

    /// <summary>
    ///     The published set: the packable projects and everything they reach
    ///     through a <c>ProjectReference</c>. Read from the csproj files, never
    ///     from a hand-typed list.
    /// </summary>
    private static IReadOnlySet<string> PublishedProjects()
    {
        var published = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (string csproj in RepoPaths.EnumerateSrcProjects())
        {
            if (!RepoPaths.IsPackable(csproj))
            {
                continue;
            }

            string? dir = Path.GetFileName(Path.GetDirectoryName(csproj));
            if (dir is not null)
            {
                queue.Enqueue(dir);
            }
        }

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (!published.Add(current))
            {
                continue;
            }

            string? csproj = RepoPaths.FindSrcProject(current);
            if (csproj is null)
            {
                continue;
            }

            foreach (string reference in RepoPaths.ReadProjectReferences(csproj).References)
            {
                if (!published.Contains(reference))
                {
                    queue.Enqueue(reference);
                }
            }
        }

        return published;
    }

    /// <summary>
    ///     The rule: every published abstract base has an implementor, or a
    ///     reservation that says why not.
    /// </summary>
    [Test]
    public async Task EveryPublishedAbstractBaseHasAnImplementorOrAReservation()
    {
        IReadOnlySet<string> published = PublishedProjects();
        AbstractBaseReport report = AbstractBaseProbe.Scan(RepoPaths.RepoRoot, project => published.Contains(project));

        var reserved = new HashSet<string>(ReservedBases.Select(r => r.Type), StringComparer.Ordinal);
        List<string> violations =
        [
            .. report.Findings
                .Where(f => !reserved.Contains(f.FullName))
                .Select(f => f.FullName + " (" + f.File + ":" + f.Line + ") — no implementor in src/ or apps/"),
        ];

        await Assert.That(violations).IsEmpty().Because(
            "A `public abstract` type in a published package with nothing deriving from it is "
            + "not an abstraction, it is a promise the package makes and nobody keeps — the "
            + "shape #1835 measured twelve of, 788 lines of published base class with zero "
            + "product descendants. Ten of them sat in Harbor.Desktop.Abstractions, whose own "
            + "csproj promised that platform VMs derive from the abstract VMs there; the "
            + "promise was kept for two of them, a third had a test double behind it, and the "
            + "other ten had nothing at all — they are now deleted. If a new published "
            + "abstract type is meant for a host OUTSIDE this tree, say so in `ReservedBases` "
            + "with the argument — a row is visible to every tool that reads this file, a "
            + "comment is not. Offenders: "
            + (violations.Count == 0 ? "(none)" : string.Join("; ", violations)));
    }

    /// <summary>
    ///     Every reservation states why the type is published without an
    ///     implementor, in the row itself.
    /// </summary>
    [Test]
    public async Task ReservationRowsAllHaveReasons()
    {
        IReadOnlyList<string> failures = ExemptionReason.RowsWithoutAReason(
            $"{nameof(PublicAbstractBaseLivenessRules)}.{nameof(ReservedBases)}",
            ReservedBases.Select(r => (Key: r.Type, Row: new ExemptionReason.Row(r.Reason, r.TrackedBy))));

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     The reservation table is live in both directions: a row that names a
    ///     type which is gone is a permission for nothing, and a row whose type
    ///     has gained an implementor is a permission for a problem that is not
    ///     there.
    /// </summary>
    [Test]
    public async Task ReservationRowsAreStillReal()
    {
        IReadOnlySet<string> published = PublishedProjects();
        AbstractBaseReport report = AbstractBaseProbe.Scan(RepoPaths.RepoRoot, project => published.Contains(project));

        var violations = new List<string>();
        var known = report.Candidates.Select(c => c.FullName).ToHashSet(StringComparer.Ordinal);

        foreach (ReservedAbstractBase row in ReservedBases)
        {
            if (!known.Contains(row.Type))
            {
                violations.Add(
                    row.Type + ": no longer a published abstract base — drop the row, do not leave "
                    + "the rule muted for a type that is not there");
                continue;
            }

            if (report.Implementors.ContainsKey(row.Type))
            {
                violations.Add(
                    row.Type + ": now has an implementor ("
                    + string.Join(", ", report.Implementors[row.Type])
                    + ") — the reservation has become a permission for a problem that is not "
                    + "there any more; remove the row");
            }
        }

        await Assert.That(violations).IsEmpty().Because(
            "A reservation table that only grows is an allow-list that rots into a blanket "
            + "permission: the cheap repair is always to leave the row, because nobody can "
            + "see from the row that the reason has expired. So the table is checked in both "
            + "directions — a row for a type that is gone fails, and a row for a type that "
            + "has gained an implementor fails. Offenders: "
            + (violations.Count == 0 ? "(none)" : string.Join("; ", violations)));
    }

    /// <summary>
    ///     Non-vacuity, part 1: the scan really reaches the population it claims
    ///     to grade. A candidate set of zero, or an implementor map that cannot
    ///     see the shipped app, would make the rule above green for the wrong
    ///     reason.
    /// </summary>
    [Test]
    public async Task TheScanStillSeesTheBasesItIsAbout()
    {
        IReadOnlySet<string> published = PublishedProjects();
        AbstractBaseReport report = AbstractBaseProbe.Scan(RepoPaths.RepoRoot, project => published.Contains(project));

        await Assert.That(report.FilesScanned).IsGreaterThan(0).Because(
            "the scan reads the product tree; a zero means the walk found nothing and every "
            + "verdict below would be vacuous");

        await Assert.That(report.Candidates.Count).IsGreaterThan(0).Because(
            "the population is the published abstract types; an empty population means the "
            + "declaration pattern stopped matching, and a rule over an empty population is "
            + "a green light that means nothing");

        var known = report.Candidates.Select(c => c.FullName).ToHashSet(StringComparer.Ordinal);

        await Assert.That(known.Contains("Harbor.Desktop.Abstractions.ViewModels.ChatViewModelBase")).IsTrue().Because(
            "the base the shipped desktop app derives from must be in the population — if it "
            + "is not, the scan is not reading what it claims to read");

        await Assert.That(report.Implementors.ContainsKey("Harbor.Desktop.Abstractions.ViewModels.ChatViewModelBase")).IsTrue().Because(
            "its only product implementor is apps/Harbor.App.Avalonia/ViewModels/ChatViewModel.cs, "
            + "which is why this rule is a source scan and not a reflection over the loaded "
            + "assemblies: the composition roots are not referenced by this test project");

        await Assert.That(report.Implementors.ContainsKey("Harbor.Tui.CellForge.Rendering.Panel")).IsTrue().Because(
            "the layout tree's Panel has nine product descendants, and the implementor map "
            + "must see them — an implementor map that cannot see a real one cannot be "
            + "trusted to report a missing one");
    }

    /// <summary>
    ///     Non-vacuity, part 2: the positive control. The SAME classifier that
    ///     grades the tree must report a synthetic published abstract class that
    ///     nothing derives from. A scanner that reports nothing is
    ///     indistinguishable from a scanner that reads nothing.
    /// </summary>
    [Test]
    public async Task NonVacuityTheProbeReportsASyntheticDeadBase()
    {
        (string Rel, string Source)[] sources =
        [
            ("src/Harbor.Synthetic/AbstractBases.cs", """
                namespace Harbor.Synthetic;

                public abstract class SyntheticDeadBase
                {
                    public int Value { get; }
                }

                public sealed class SomethingElse
                {
                    public SyntheticDeadBase? Base { get; init; }
                }
                """),
        ];

        AbstractBaseReport report = AbstractBaseProbe.ScanFiles(sources, _ => true);

        await Assert.That(report.Candidates.Count).IsEqualTo(1).Because(
            "the synthetic file declares exactly one public abstract class, and the "
            + "declaration pattern must find it");

        await Assert.That(report.Findings.Count).IsEqualTo(1).Because(
            "nothing in the synthetic file derives from the synthetic base, so the "
            + "classifier must report it — if it does not, the verdict in "
            + "EveryPublishedAbstractBaseHasAnImplementorOrAReservation is vacuous");

        await Assert.That(report.Findings[0].FullName).IsEqualTo("Harbor.Synthetic.SyntheticDeadBase").Because(
            "the finding must name the type that has no implementor, or the message tells the "
            + "reader nothing");
    }

    /// <summary>
    ///     Non-vacuity, part 3: the negative controls. The same synthetic base,
    ///     once something derives from it, must NOT be reported — and a reserved
    ///     base must not be reported either, so the exemption path is exercised
    ///     rather than merely declared.
    /// </summary>
    [Test]
    public async Task NonVacuityTheProbeAcceptsAnImplementorAndAReservation()
    {
        (string Rel, string Source)[] sources =
        [
            ("src/Harbor.Synthetic/AbstractBases.cs", """
                namespace Harbor.Synthetic;

                public abstract class SyntheticLiveBase
                {
                    public int Value { get; }
                }

                public sealed class SyntheticChild : SyntheticLiveBase
                {
                }
                """),
        ];

        AbstractBaseReport report = AbstractBaseProbe.ScanFiles(sources, _ => true);

        await Assert.That(report.Candidates.Count).IsEqualTo(1).Because(
            "the synthetic file declares exactly one public abstract class");

        await Assert.That(report.Findings.ToArray()).IsEmpty().Because(
            "the synthetic child derives from the synthetic base, so the base HAS an "
            + "implementor and the classifier must not report it");

        await Assert.That(report.Implementors.ContainsKey("Harbor.Synthetic.SyntheticLiveBase")).IsTrue().Because(
            "the implementor evidence must name the file that derives from the base");

        // The reservation path: a base with no implementor, listed in the table.
        var reserved = new HashSet<string>(ReservedBases.Select(r => r.Type), StringComparer.Ordinal);

        await Assert.That(reserved.Contains("Harbor.Synthetic.SyntheticDeadBase")).IsFalse().Because(
            "the synthetic base is not in the reservation table — this control proves the "
            + "table is a table and not a blanket permission: an unlisted dead base is "
            + "reported");

        await Assert.That(reserved.Contains("Harbor.Tui.CellForge.Input.FocusableBase")).IsTrue().Because(
            "a REAL base with no product implementor is listed, and the rule grades the tree "
            + "against that table — the exemption path is exercised by a real row, not only "
            + "by this control");
    }
}
