// DeadPublicInterfaceRule.cs: issue #1831. A public interface with NO
// implementor and NO consumer is a promise nobody keeps, and nothing in the
// tree said so.
//
// THE DEFECT THIS FILE EXISTS FOR
// -------------------------------
// The audit in #1831 measured 124 public interfaces and found three of them,
// `IHarborIdeClient`, `ITuiRenderBackend` and `IRenderEngine`, carrying 22
// public members with ZERO implementors and ZERO real consumers. Two of the
// three were frozen in `src/Harbor.Abstractions/PublicAPI.Shipped.txt`, so the
// public-API gate was protecting a contract nobody implemented and nobody
// called. A gate that keeps a dead promise alive is worse than no gate,
// because it makes the promise look maintained.
//
// The three were not the whole story. Running the same measurement over the
// contract assemblies found seven, not three: `IEventSubscriber`,
// `IInputHandler` (plus its orphan `KeyPressEventArgs`) and `IPluginHost` were
// in the same shape and had simply never been measured, because the audit's
// table only listed interfaces with four or more members.
//
// WHAT THIS RULE GRADES, AND WHAT IT DOES NOT
// -------------------------------------------
// The rule: a PUBLIC interface declared in a contract assembly must have at
// least one implementor OR at least one consumer in the tree. An interface
// with neither is a contract that exists only to be published.
//
// It is not a gate demanding that every public interface be implemented. The
// repository has a legitimate case for that: a contract reserved for a product
// decision that is explicitly deferred. The rule has to be able to say so
// without being switched off, so the escape hatch is a NAMED TABLE rather than
// a comment. `ReservedSurface` below holds one row per legitimately
// unimplemented contract. Each row states WHY in the row itself and, where one
// exists, points at the issue that owns the decision.
//
// WHY A TABLE AND NOT A COMMENT
// -----------------------------
// The repo already learned this lesson twice. `ExemptionReason.cs` exists
// because five tables each asked "does this row state a reason?" in their own
// words, and one of them did not ask at all. A permission that lives in a
// comment is invisible to every tool that reads the file, so the reason is lost
// the moment the next person reformats it. The rows below therefore carry:
//   * the full type name, the thing being excused;
//   * the REASON, as a value, long enough to be an argument. The shared
//     `ExemptionReason` check enforces the floor.
//   * the tracking issue, in its own column, so a URL is never mistaken for a
//     reason.
//
// And the table is LIVENESS-CHECKED. A reservation that stops being true, the
// interface gaining an implementor or the interface being deleted, fails the
// build until the row is removed. That is the difference between a table and an
// allow-list that rots into a blanket permission. `SessionStatusTableRule`'s
// `KnownDuplicates` works the same way, and `RecordedDuplicates_AreStillReal`
// is the shape of the check this file copies.
//
// WHY A SOURCE SCAN AND NOT REFLECTION
// ------------------------------------
// Two reasons, and the second is the load-bearing one.
//
// 1. `apps/Harbor.App.Cli` and `apps/Harbor.App.Avalonia` are composition
//    roots and are NOT referenced by this test project, so reflection over the
//    loaded assemblies cannot see the implementations that live there.
//    `IDialogService`, `IThemeService` and `ISessionForker` are implemented in
//    the Avalonia app, and a reflection-based rule would report every one of
//    them as dead.
//
// 2. The rule must be provable. A guard that can only be exercised against the
//    real tree cannot carry a synthetic positive control, and a guard whose
//    only evidence is "it is green today" is indistinguishable from a guard
//    that reads nothing. A source scan can be handed a synthetic dead
//    interface and be REQUIRED to report it, which is what the
//    `NonVacuity_...` tests below do.
//
// The scan reads `src/`, `apps/` and `tests/`. `tests/` is INCLUDED on
// purpose: a test double that implements a contract is a real implementor,
// and the audit's own verdict on `IHarborIdeClient` ("the only file mentioning
// the type is a reflective test that calls none of its members") depends on the
// difference between a test that IMPLEMENTS a contract and a test that merely
// reflects over it. `contrib/` is EXCLUDED, as everywhere else in this
// project: it is not compiled by CI, so a rule that fires on it describes code
// that cannot break.
//
// WHAT COUNTS AS A "CONSUMER"
// ---------------------------
// A file that uses the interface AS A TYPE. Two things deliberately do NOT
// count:
//
//   * `typeof(IFoo)` and `nameof(IFoo)`. A reflection-only mention is not a
//     use. This is the exact shape that made `IHarborIdeClient` look alive: the
//     audit found one file mentioning it and, on reading it, "a reflective test
//     which calls none of its members". Grading a `typeof()` as a consumer
//     would have kept the dead contract alive forever.
//   * a mention inside a string literal or a comment, for the same reason the
//     lexer blanks them.
//
// An IMPLEMENTOR counts as a consumer too, since it names the type in its base
// list, so the two counts are reported separately while the verdict is the OR.
// That is the shape the audit's control asked for: "the check must give
// DIFFERENT answers for `IHarborIdeClient` (both zero) and for `IThemeService`
// (16 consumers)".
//
// NAME RESOLUTION
// ---------------
// The tree has two `IProcessHandle` interfaces (`Harbor.Abstractions.Ide` and
// `Harbor.Tools.Builtin`) and two `IInputHandler` interfaces
// (`Harbor.Abstractions.Tui` and `Harbor.Tui.CellForge.Input`), so a simple
// name is NOT an identity. A mention is attributed to a declaration by
// visibility: the file's own namespace, an ancestor of it, or an imported
// namespace (a `using` in the file, or a `global using` in the same project).
// A name that stays ambiguous after that is attributed to nobody, which is the
// conservative answer. An ambiguous mention may not be used to claim that a
// contract is alive.
//
// Only TOP-LEVEL declarations count. A probe class that declares its own
// `ISessionForker` inside a nested scope is not the product's
// `ISessionForker`, and letting it into the universe would make the real
// contract ambiguous with a test fixture.
//
// THE SCOPE, AND WHAT IS LEFT OUT
// -------------------------------
// The contract assemblies: the ones whose public surface is an extension
// contract a third party compiles against, plus `Harbor.Ui.Framework.Services`
// (which holds one of the audited interfaces) and
// `Harbor.Diagnostics.Abstractions`. Implementation assemblies are not in
// scope: a public interface on an implementation type is an internal detail
// that happens to be public, and the layering rules already grade that
// surface.
//
// KNOWN GAP: the scope is the contract assemblies, so a dead public interface
// in an implementation assembly is still invisible to this rule. The same gap
// is recorded in #1831 for `FatInterfaceShapeRatchet`, and widening it belongs
// to the arch-guard program (#1724), not to this slice.
//
// NON-VACUITY
// -----------
//   1. `Scan_IsLive` checks that the scan really walked a checkout and really
//      found the contract interfaces it grades. A scope filter that silently
//      stopped seeing the tree would otherwise make every rule below green for
//      the wrong reason.
//   2. `NonVacuity_TheAuditedThree_AreReportedWhenPresent` re-creates the three
//      interfaces the audit named as SYNTHETIC sources and requires all three
//      to be reported. This is the control that says "the rule finds these
//      three", and it survives the two that were deleted.
//   3. `NonVacuity_TheRuleDiscriminatesImplementorsConsumersAndReflection`
//      grades an interface with an implementor, one with a consumer, and one
//      mentioned only through `typeof()` differently, so the rule discriminates
//      instead of flagging every public interface in the tree.
//
// System.Text.RegularExpressions is not a global using in this project. The
// other rule files import it explicitly and so does this one.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     One public interface the scan graded, identified by namespace and simple
///     name, because a simple name alone is not an identity in this tree.
/// </summary>
/// <param name="Name">The simple type name.</param>
/// <param name="Namespace">The namespace the interface is declared in.</param>
/// <param name="File">The repo-relative file that declares it.</param>
internal sealed record ScopedPublicInterface(string Name, string Namespace, string File)
{
    /// <summary>The full name, as it appears in a reservation row.</summary>
    internal string FullName => string.IsNullOrEmpty(Namespace) ? Name : Namespace + "." + Name;
}

/// <summary>
///     One public interface with no implementor and no consumer. A contract that
///     exists only to be published.
/// </summary>
/// <param name="Name">The simple type name.</param>
/// <param name="Namespace">The namespace the interface is declared in.</param>
/// <param name="File">The repo-relative file that declares it.</param>
internal sealed record DeadPublicInterface(string Name, string Namespace, string File)
{
    /// <summary>The full name, as it appears in a reservation row.</summary>
    internal string FullName => string.IsNullOrEmpty(Namespace) ? Name : Namespace + "." + Name;
}

/// <summary>
///     One row of the reservation table: a public interface that is allowed to
///     have no implementor, with the argument for why and the issue that owns the
///     decision.
/// </summary>
/// <param name="Type">The full type name being excused.</param>
/// <param name="Reason">Why the contract is legitimately unimplemented.</param>
/// <param name="TrackedBy">The issue that owns the decision, when there is one.</param>
internal sealed record ReservedPublicInterface(string Type, string Reason, string? TrackedBy);

/// <summary>The scan's output: what it found, and what it looked at.</summary>
/// <param name="Findings">Public interfaces with no implementor and no consumer.</param>
/// <param name="Scoped">Every public interface the scan graded.</param>
/// <param name="FilesScanned">How many files the scan read.</param>
internal sealed record DeadPublicInterfaceReport(
    IReadOnlyList<DeadPublicInterface> Findings,
    IReadOnlyList<ScopedPublicInterface> Scoped,
    int FilesScanned);

/// <summary>
///     The scanner behind <see cref="DeadPublicInterfaceRule" />. It is
///     source-based, namespace-aware, and drivable from synthetic sources so the
///     rule can be proven rather than merely observed.
/// </summary>
internal static class DeadPublicInterfaceProbe
{
    /// <summary>
    ///     The contract assemblies: the ones whose public surface a third party
    ///     compiles against, plus the two that hold audited dead interfaces.
    /// </summary>
    internal static readonly string[] ContractTrees =
    [
        "src/Harbor.Abstractions",
        "src/Harbor.Abstractions.Contracts",
        "src/Harbor.Plugins.Abstractions",
        "src/Harbor.Terminal.Abstractions",
        "src/Harbor.Ui.Framework.Abstractions",
        "src/Harbor.Ui.Framework.Services",
        "src/Harbor.Diagnostics.Abstractions",
    ];

    /// <summary>
    ///     The trees the scan reads. <c>tests/</c> is included on purpose, because
    ///     a test double that IMPLEMENTS a contract is an implementor.
    ///     <c>contrib/</c> is pruned, as everywhere else in this project: it is not
    ///     compiled by CI.
    /// </summary>
    internal static readonly string[] ScanTrees = ["src", "apps", "tests"];

    /// <summary>Directory names never descended into.</summary>
    private static readonly string[] PrunedDirectories = ["obj", "bin", "contrib", ".worktrees", "node_modules"];

    /// <summary>Matches an interface declaration and captures its access modifier and name.</summary>
    private static readonly Regex InterfaceDeclaration = new(
        @"\b(public|internal|protected|private)\s+(?:partial\s+)?interface\s+(\w+)",
        RegexOptions.Compiled);

    /// <summary>Matches a class, record or struct that has a base list.</summary>
    private static readonly Regex TypeWithBaseList = new(
        @"\b(?:public\s+|internal\s+|protected\s+|private\s+|sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+|ref\s+)*"
        + @"(?:class|record|struct)\s+(\w+)\s*(?:<[^>{;]*>)?\s*(?:\([^;{]*\))?\s*:\s*([^{;]+)",
        RegexOptions.Compiled);

    /// <summary>Matches an interface declaration that has a base list.</summary>
    private static readonly Regex InterfaceWithBaseList = new(
        @"\binterface\s+(\w+)\s*(?:<[^>{;]*>)?\s*:\s*([^{;]+)",
        RegexOptions.Compiled);

    /// <summary>Matches a namespace declaration and captures the name and whether it opens a block.</summary>
    private static readonly Regex NamespaceDeclaration = new(
        @"\bnamespace\s+([\w.]+)\s*([;{])",
        RegexOptions.Compiled);

    /// <summary>Matches a using directive and captures the imported namespace.</summary>
    private static readonly Regex UsingDirective = new(
        @"^\s*(?:global\s+)?using\s+(?:static\s+)?([\w.]+)\s*;",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Matches an identifier.</summary>
    private static readonly Regex Identifier = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);

    /// <summary>
    ///     Matches a reflection-only mention of a type, `typeof(X)` and `nameof(X)`,
    ///     which is not a use of the type.
    /// </summary>
    private static readonly Regex ReflectionMention = new(
        @"\b(?:typeof|nameof)\s*\(\s*\w+\s*\)",
        RegexOptions.Compiled);

    /// <summary>Words that are never a type reference inside a base list.</summary>
    private static readonly HashSet<string> BaseListNoise = new(StringComparer.Ordinal)
    {
        "where", "class", "record", "struct", "new",
    };

    /// <summary>Reads the repository and grades it.</summary>
    /// <param name="repoRoot">The checkout to read; <c>null</c> grades nothing.</param>
    internal static DeadPublicInterfaceReport Scan(string? repoRoot)
    {
        if (repoRoot is null)
        {
            return new DeadPublicInterfaceReport([], [], 0);
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
                if (IsPruned(rel))
                {
                    continue;
                }

                if (SourceScan.TryReadAllText(path) is { } text)
                {
                    sources.Add((rel, text));
                }
            }
        }

        return ScanFiles(sources);
    }

    /// <summary>
    ///     Grades an explicit set of sources. This is the control path: the same
    ///     code that grades the tree grades a synthetic one.
    /// </summary>
    /// <param name="sources">Repo-relative path and raw source, one pair per file.</param>
    internal static DeadPublicInterfaceReport ScanFiles(IReadOnlyList<(string Rel, string Source)> sources)
    {
        var files = new List<SourceFile>(sources.Count);
        foreach ((string rel, string source) in sources)
        {
            files.Add(SourceFile.Parse(rel, source));
        }

        // A `global using` applies to the whole project, not to the repository.
        foreach (SourceFile globals in files)
        {
            if (!globals.Rel.EndsWith("GlobalUsings.cs", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (SourceFile peer in files)
            {
                if (string.Equals(peer.Project, globals.Project, StringComparison.Ordinal))
                {
                    peer.GlobalUsings.UnionWith(globals.Usings);
                }
            }
        }

        // The universe of interface declarations, keyed by simple name.
        var universe = new Dictionary<string, List<(string Namespace, string File)>>(StringComparer.Ordinal);
        var declaringFiles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var owner = new Dictionary<(string Namespace, string Name), SourceFile>();

        foreach (SourceFile file in files)
        {
            foreach ((string name, string ns) in file.Interfaces)
            {
                if (!universe.TryGetValue(name, out List<(string Namespace, string File)>? entries))
                {
                    entries = [];
                    universe[name] = entries;
                }

                entries.Add((ns, file.Rel));

                if (!declaringFiles.TryGetValue(ns + "." + name, out HashSet<string>? owners))
                {
                    owners = new HashSet<string>(StringComparer.Ordinal);
                    declaringFiles[ns + "." + name] = owners;
                }

                owners.Add(file.Rel);

                owner.TryAdd((ns, name), file);
            }
        }

        // Implementors: a class, record or struct whose base list resolves to the
        // interface, directly or through an interface it derives from.
        var implementors = new Dictionary<(string Namespace, string Name), HashSet<string>>();
        foreach (SourceFile file in files)
        {
            foreach (List<string> bases in file.TypeBaseLists)
            {
                var reached = new HashSet<(string Namespace, string Name)>();
                var pending = new Queue<(string Token, SourceFile Context)>();

                foreach (string token in bases)
                {
                    pending.Enqueue((token, file));
                }

                while (pending.Count > 0)
                {
                    (string token, SourceFile context) = pending.Dequeue();
                    (string Namespace, string Name)? resolved = Resolve(context, token, universe);
                    if (resolved is null)
                    {
                        continue;
                    }

                    if (!reached.Add(resolved.Value))
                    {
                        continue;
                    }

                    if (!owner.TryGetValue(resolved.Value, out SourceFile? declaring) || declaring is null)
                    {
                        continue;
                    }

                    if (!declaring.InterfaceBaseLists.TryGetValue(resolved.Value.Name, out List<string>? inherited)
                        || inherited is null)
                    {
                        continue;
                    }

                    foreach (string baseToken in inherited)
                    {
                        pending.Enqueue((baseToken, declaring));
                    }
                }

                foreach ((string ns, string name) in reached)
                {
                    if (!implementors.TryGetValue((ns, name), out HashSet<string>? set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        implementors[(ns, name)] = set;
                    }

                    set.Add(file.Rel);
                }
            }
        }

        // The public interfaces the rule grades.
        var scoped = new List<ScopedPublicInterface>();
        var scopedSeen = new HashSet<(string Namespace, string Name)>();
        foreach (SourceFile file in files)
        {
            if (!IsInContractTree(file.Rel))
            {
                continue;
            }

            foreach ((string name, string ns, bool isPublic) in file.PublicInterfaces)
            {
                if (isPublic && scopedSeen.Add((ns, name)))
                {
                    scoped.Add(new ScopedPublicInterface(name, ns, file.Rel));
                }
            }
        }

        // Consumers: files that use the interface as a type.
        var consumers = new Dictionary<(string Namespace, string Name), HashSet<string>>();
        foreach (ScopedPublicInterface candidate in scoped)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);

            foreach (SourceFile file in files)
            {
                if (declaringFiles.TryGetValue(candidate.FullName, out HashSet<string>? owners)
                    && owners.Contains(file.Rel))
                {
                    continue;
                }

                if (!file.UsedWords.Contains(candidate.Name))
                {
                    continue;
                }

                (string Namespace, string Name)? resolved = Resolve(file, candidate.Name, universe);
                if (resolved is { } r
                    && r.Namespace == candidate.Namespace
                    && r.Name == candidate.Name)
                {
                    found.Add(file.Rel);
                }
            }

            consumers[(candidate.Namespace, candidate.Name)] = found;
        }

        var findings = new List<DeadPublicInterface>();
        foreach (ScopedPublicInterface candidate in scoped)
        {
            bool hasImplementor = implementors.TryGetValue((candidate.Namespace, candidate.Name), out HashSet<string>? impls)
                && impls is { Count: > 0 };
            bool hasConsumer = consumers.TryGetValue((candidate.Namespace, candidate.Name), out HashSet<string>? cons)
                && cons is { Count: > 0 };

            if (!hasImplementor && !hasConsumer)
            {
                findings.Add(new DeadPublicInterface(candidate.Name, candidate.Namespace, candidate.File));
            }
        }

        return new DeadPublicInterfaceReport(findings, scoped, files.Count);
    }

    /// <summary>
    ///     Resolves a simple type name used in a file to the interface it names.
    ///     <c>null</c> means nobody: an unknown name, or a name that stays
    ///     ambiguous after visibility is applied.
    /// </summary>
    /// <param name="file">The file the name is used in.</param>
    /// <param name="name">The simple type name.</param>
    /// <param name="universe">Every interface declaration in the tree, by simple name.</param>
    private static (string Namespace, string Name)? Resolve(
        SourceFile file,
        string name,
        Dictionary<string, List<(string Namespace, string File)>> universe)
    {
        if (!universe.TryGetValue(name, out List<(string Namespace, string File)>? candidates))
        {
            return null;
        }

        var identities = new HashSet<(string Namespace, string Name)>();
        foreach (var candidate in candidates)
        {
            identities.Add((candidate.Namespace, name));
        }

        if (identities.Count == 1)
        {
            return identities.First();
        }

        var visible = new List<(string Namespace, string Name)>();
        foreach ((string ns, string simpleName) in identities)
        {
            if (IsVisible(file, ns))
            {
                visible.Add((ns, simpleName));
            }
        }

        return visible.Count == 1 ? visible[0] : null;
    }

    /// <summary>Whether a namespace is reachable from a file without qualification.</summary>
    /// <param name="file">The file asking.</param>
    /// <param name="ns">The candidate namespace.</param>
    private static bool IsVisible(SourceFile file, string ns)
    {
        if (ns.Length == 0)
        {
            return true;
        }

        if (string.Equals(file.Namespace, ns, StringComparison.Ordinal)
            || file.Namespace.StartsWith(ns + ".", StringComparison.Ordinal))
        {
            return true;
        }

        return file.Usings.Contains(ns) || file.GlobalUsings.Contains(ns);
    }

    /// <summary>Whether a repo-relative path sits under one of the contract trees.</summary>
    /// <param name="rel">The repo-relative path.</param>
    internal static bool IsInContractTree(string rel)
    {
        foreach (string tree in ContractTrees)
        {
            if (rel.StartsWith(tree + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a repo-relative path sits under a pruned directory.</summary>
    /// <param name="rel">The repo-relative path.</param>
    private static bool IsPruned(string rel)
    {
        foreach (string segment in rel.Split('/'))
        {
            if (PrunedDirectories.Contains(segment, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     One source file, reduced to the facts the rule grades.
    /// </summary>
    private sealed class SourceFile
    {
        /// <summary>The repo-relative path.</summary>
        internal required string Rel { get; init; }

        /// <summary>The project the file belongs to, the first two path segments.</summary>
        internal required string Project { get; init; }

        /// <summary>The file's namespace, or the empty string.</summary>
        internal required string Namespace { get; init; }

        /// <summary>Namespaces imported by a <c>using</c> directive in this file.</summary>
        internal required HashSet<string> Usings { get; init; }

        /// <summary>Namespaces imported by a <c>global using</c> in this file's project.</summary>
        internal HashSet<string> GlobalUsings { get; } = new(StringComparer.Ordinal);

        /// <summary>Identifiers used in this file, with reflection-only mentions removed.</summary>
        internal required HashSet<string> UsedWords { get; init; }

        /// <summary>Top-level interface declarations: name and namespace.</summary>
        internal required List<(string Name, string Namespace)> Interfaces { get; init; }

        /// <summary>Top-level interface declarations with their access modifier.</summary>
        internal required List<(string Name, string Namespace, bool IsPublic)> PublicInterfaces { get; init; }

        /// <summary>Base lists of the top-level class, record and struct declarations.</summary>
        internal required List<List<string>> TypeBaseLists { get; init; }

        /// <summary>Base lists of the top-level interface declarations, by interface name.</summary>
        internal required Dictionary<string, List<string>> InterfaceBaseLists { get; init; }

        /// <summary>Parses one file into the facts above.</summary>
        /// <param name="rel">The repo-relative path.</param>
        /// <param name="source">The raw source.</param>
        internal static SourceFile Parse(string rel, string source)
        {
            string code = BlankLiteralsAndComments(source);

            string[] segments = rel.Split('/');
            string project = segments.Length >= 2 ? segments[0] + "/" + segments[1] : segments[0];

            Match nsMatch = NamespaceDeclaration.Match(code);
            string ns = nsMatch.Success ? nsMatch.Groups[1].Value : string.Empty;
            bool nsIsBlock = nsMatch.Success && nsMatch.Groups[2].Value == "{";

            var usings = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match usingMatch in UsingDirective.Matches(code))
            {
                usings.Add(usingMatch.Groups[1].Value);
            }

            // Identifiers used as types. Reflection-only mentions are removed:
            // `typeof(IFoo)` and `nameof(IFoo)` are not uses of the type.
            string typeUse = ReflectionMention.Replace(code, static m => new string(' ', m.Length));
            var words = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match id in Identifier.Matches(typeUse))
            {
                words.Add(id.Value);
            }

            (int[] lineStarts, int[] depths) = ComputeFrames(code);
            int baseDepth = nsIsBlock ? 1 : 0;

            var interfaces = new List<(string Name, string Namespace)>();
            var publicInterfaces = new List<(string Name, string Namespace, bool IsPublic)>();
            var interfaceBases = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (Match match in InterfaceDeclaration.Matches(code))
            {
                if (!IsTopLevel(lineStarts, depths, match.Index, baseDepth))
                {
                    continue;
                }

                string name = match.Groups[2].Value;
                interfaces.Add((name, ns));
                publicInterfaces.Add((name, ns, match.Groups[1].Value == "public"));
            }

            foreach (Match match in InterfaceWithBaseList.Matches(code))
            {
                if (!IsTopLevel(lineStarts, depths, match.Index, baseDepth))
                {
                    continue;
                }

                // Merged, not overwritten: two declarations may share a simple name
                // (the tree has more than one `IInputHandler`), and the base lists of
                // both belong to the name.
                if (!interfaceBases.TryGetValue(match.Groups[1].Value, out List<string>? bases))
                {
                    bases = [];
                    interfaceBases[match.Groups[1].Value] = bases;
                }

                bases.AddRange(SplitBaseList(match.Groups[2].Value));
            }

            var typeBases = new List<List<string>>();
            foreach (Match match in TypeWithBaseList.Matches(code))
            {
                if (!IsTopLevel(lineStarts, depths, match.Index, baseDepth))
                {
                    continue;
                }

                typeBases.Add(SplitBaseList(match.Groups[2].Value));
            }

            return new SourceFile
            {
                Rel = rel,
                Project = project,
                Namespace = ns,
                Usings = usings,
                UsedWords = words,
                Interfaces = interfaces,
                PublicInterfaces = publicInterfaces,
                TypeBaseLists = typeBases,
                InterfaceBaseLists = interfaceBases,
            };
        }

        /// <summary>
        ///     The identifiers of a base list, without the noise words and without
        ///     generic arguments.
        /// </summary>
        /// <param name="baseList">The raw base list.</param>
        private static List<string> SplitBaseList(string baseList)
        {
            var tokens = new List<string>();
            foreach (Match id in Identifier.Matches(baseList))
            {
                if (!BaseListNoise.Contains(id.Value))
                {
                    tokens.Add(id.Value);
                }
            }

            return tokens;
        }

        /// <summary>
        ///     Whether the declaration starting at <paramref name="index" /> sits at
        ///     the top level of the file, rather than nested inside a class or a
        ///     probe.
        /// </summary>
        /// <param name="lineStarts">The offset each line starts at.</param>
        /// <param name="depths">The brace depth before each line.</param>
        /// <param name="index">The offset of the declaration.</param>
        /// <param name="baseDepth">The depth a top-level declaration has.</param>
        private static bool IsTopLevel(int[] lineStarts, int[] depths, int index, int baseDepth)
        {
            int line = LineOf(lineStarts, index);
            return line < depths.Length && depths[line] == baseDepth;
        }

        /// <summary>
        ///     The 0-based line an offset sits on, from the per-line frame table.
        /// </summary>
        /// <param name="lineStarts">The offset each line starts at.</param>
        /// <param name="index">The offset in the file.</param>
        private static int LineOf(int[] lineStarts, int index)
        {
            int low = 0;
            int high = lineStarts.Length - 1;

            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (lineStarts[mid] <= index)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return low;
        }

        /// <summary>
        ///     The offset each line starts at, and the brace depth before each line.
        /// </summary>
        /// <param name="code">The file with literals and comments blanked.</param>
        private static (int[] LineStarts, int[] Depths) ComputeFrames(string code)
        {
            var starts = new List<int>();
            var depths = new List<int>();

            int depth = 0;
            int lineStart = 0;

            while (true)
            {
                starts.Add(lineStart);
                depths.Add(depth);

                int newline = code.IndexOf('\n', lineStart);
                if (newline < 0)
                {
                    break;
                }

                for (int i = lineStart; i < newline; i++)
                {
                    if (code[i] == '{')
                    {
                        depth++;
                    }
                    else if (code[i] == '}')
                    {
                        depth--;
                    }
                }

                lineStart = newline + 1;
            }

            return (starts.ToArray(), depths.ToArray());
        }
    }

    /// <summary>
    ///     Blanks out comments AND string and char literal contents, preserving
    ///     the line structure.
    /// </summary>
    /// <remarks>
    ///     The shared <see cref="SourceCommentStripper" /> keeps string literals,
    ///     which is right for a rule about a rendered string and wrong here. This
    ///     rule counts BRACES to find top-level declarations, and a <c>"}"</c> inside
    ///     a literal would throw the depth off and hide a declaration, which reads
    ///     as "no implementor" and would report a live contract as dead.
    ///     <para>
    ///         The shared stripper is not used for the comment half either, because it
    ///         has no state for a raw string literal (<c>"""..."""</c>), and the tree
    ///         has dozens of files that use them. A <c>//</c> inside a raw string
    ///         would end the "string" early and desynchronise the rest of the file.
    ///         So this file carries its own small lexer that blanks comments and
    ///         literal contents in one pass, including verbatim and raw strings. The
    ///         trade is one lexer instead of the shared one, and it buys a frame that
    ///         a brace in a literal cannot move.
    ///     </para>
    /// </remarks>
    /// <param name="source">The raw source.</param>
    internal static string BlankLiteralsAndComments(string source)
    {
        var output = new StringBuilder(source.Length);
        int i = 0;
        int n = source.Length;

        while (i < n)
        {
            char c = source[i];
            char next = i + 1 < n ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < n && source[i] != '\n')
                {
                    output.Append(' ');
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                output.Append("  ");
                i += 2;

                while (i < n)
                {
                    if (source[i] == '*' && i + 1 < n && source[i + 1] == '/')
                    {
                        output.Append("  ");
                        i += 2;
                        break;
                    }

                    output.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            if (c == '@' && next == '"')
            {
                output.Append("  ");
                i += 2;

                while (i < n)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < n && source[i + 1] == '"')
                        {
                            output.Append("  ");
                            i += 2;
                            continue;
                        }

                        output.Append(' ');
                        i++;
                        break;
                    }

                    output.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            if (c == '"' && next == '"' && i + 2 < n && source[i + 2] == '"')
            {
                output.Append("   ");
                i += 3;

                while (i < n)
                {
                    if (source[i] == '"' && i + 2 < n && source[i + 1] == '"' && source[i + 2] == '"')
                    {
                        output.Append("   ");
                        i += 3;
                        break;
                    }

                    output.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            if (c == '"')
            {
                output.Append(' ');
                i++;

                while (i < n)
                {
                    if (source[i] == '\\' && i + 1 < n)
                    {
                        output.Append("  ");
                        i += 2;
                        continue;
                    }

                    if (source[i] == '"')
                    {
                        output.Append(' ');
                        i++;
                        break;
                    }

                    output.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            if (c == '\'')
            {
                output.Append(' ');
                i++;

                while (i < n)
                {
                    if (source[i] == '\\' && i + 1 < n)
                    {
                        output.Append("  ");
                        i += 2;
                        continue;
                    }

                    if (source[i] == '\'')
                    {
                        output.Append(' ');
                        i++;
                        break;
                    }

                    output.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }
}

/// <summary>
///     Issue #1831: a public interface in a contract assembly must have at least
///     one implementor or at least one consumer, unless it is on the named
///     reservation table. The table's rows state their reason, carry their
///     tracking issue, and are liveness-checked.
/// </summary>
public sealed class DeadPublicInterfaceRule
{
    /// <summary>
    ///     The reservation table: public interfaces that are allowed to have no
    ///     implementor, each with the argument for why and the issue that owns the
    ///     decision.
    /// </summary>
    /// <remarks>
    ///     This table may only shrink. Every row is a promise that a contract is
    ///     waiting for a decision, not that nobody wanted it. The liveness test
    ///     below fails when a row stops being true, so the table cannot rot into a
    ///     blanket permission for dead contracts.
    /// </remarks>
    private static readonly ReservedPublicInterface[] ReservedSurface =
    [
        new(
            "Harbor.Abstractions.Ide.IHarborIdeClient",
            "The IDE bridge is an explicitly deferred product decision (issue #1252 says "
            + "\"Do NOT implement\"), and this contract is the seam docs/IDE_INTEGRATION_PATTERNS.md "
            + "§7.5 names for a future VS Code or JetBrains adapter. It is frozen in the public-API "
            + "baseline, so deleting it would withdraw a published promise while the decision that "
            + "would replace it is still open.",
            "https://github.com/refusedguy/Harbor-Harness/issues/1252"),
        new(
            "Harbor.Abstractions.Ide.IProcessHandle",
            "The handle half of the same reserved IDE-bridge surface as IHarborIdeClient: it exists "
            + "for the process an IDE adapter would start, and the IDE bridge is deferred (issue "
            + "#1252). Not to be confused with Harbor.Tools.Builtin.IProcessHandle, which is the Bash "
            + "tool's handle and IS implemented.",
            "https://github.com/refusedguy/Harbor-Harness/issues/1252"),
        new(
            "Harbor.Abstractions.Events.IEventSubscriber",
            "This contract forwards AgentEvents to an out-of-process consumer, which is the shape the "
            + "two-process architecture (NativeAOT core plus JIT UI over UDS) needs. That architecture "
            + "is still an open exploration (issue #21), so the contract is kept and marked rather than "
            + "deleted while the decision is pending.",
            "https://github.com/refusedguy/Harbor-Harness/issues/21"),
        new(
            "Harbor.Abstractions.Plugins.IPluginHost",
            "The full load and unload lifecycle contract for a plugin host. The running host is composed "
            + "from narrower seams today (IPluginSource, IPluginCompiler, IPluginInstantiator and "
            + "IPluginRegistrar, with IPluginLoadHost as the registration sink), and whether a full "
            + "lifecycle host exists, and in which process, is the open plugin-host ADR (issue #418, a "
            + "slice of #48).",
            "https://github.com/refusedguy/Harbor-Harness/issues/418"),
    ];

    /// <summary>The live scan of the checkout, computed once.</summary>
    private static readonly Lazy<DeadPublicInterfaceReport> Report = new(
        () => DeadPublicInterfaceProbe.Scan(RepoPaths.RepoRoot));

    /// <summary>
    ///     Rule 1: every graded public interface has an implementor or a consumer,
    ///     or is on the reservation table.
    /// </summary>
    [Test]
    public async Task EveryScopedPublicInterface_HasAnImplementorOrAConsumer()
    {
        DeadPublicInterfaceReport report = Report.Value;

        var reserved = new HashSet<string>(ReservedSurface.Select(static r => r.Type), StringComparer.Ordinal);

        var unexpected = report.Findings
            .Where(f => !reserved.Contains(f.FullName))
            .Select(static f => f.FullName + " (" + f.File + ")")
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToList();

        await Assert.That(unexpected).IsEmpty()
            .Because(
                "a public interface in a contract assembly with no implementor and no consumer is a "
                + "contract nobody keeps: the public-API gate freezes it, a reader assumes it is "
                + "maintained, and the next author copies it. Either give it an implementor or a "
                + "consumer, or reserve it on the DeadPublicInterfaceRule.ReservedSurface table with "
                + "the reason and the owning issue. Unreserved: " + string.Join(" | ", unexpected));
    }

    /// <summary>
    ///     Rule 2: the reservation table is live. A row whose interface gained an
    ///     implementor, or whose interface no longer exists, must be removed.
    /// </summary>
    [Test]
    public async Task ReservedSurface_RowsAreStillRealReservations()
    {
        DeadPublicInterfaceReport report = Report.Value;

        var found = new HashSet<string>(
            report.Findings.Select(static f => f.FullName),
            StringComparer.Ordinal);

        var scoped = new HashSet<string>(
            report.Scoped.Select(static s => s.FullName),
            StringComparer.Ordinal);

        var stale = new List<string>();

        foreach (ReservedPublicInterface row in ReservedSurface)
        {
            if (!scoped.Contains(row.Type))
            {
                stale.Add(row.Type + ": the interface is not a graded public interface any more");
                continue;
            }

            if (!found.Contains(row.Type))
            {
                stale.Add(row.Type + ": it now has an implementor or a consumer, so the reservation is over");
            }
        }

        await Assert.That(stale).IsEmpty()
            .Because(
                "a reservation is a promise that a contract is WAITING for a decision, and the promise "
                + "ends when the contract is implemented or deleted. Keeping the row after that is how "
                + "an allow-list becomes a blanket permission: remove the row in the same commit that "
                + "implements or deletes the interface. Stale rows: " + string.Join(" | ", stale));
    }

    /// <summary>
    ///     Rule 3: every reservation states why, in the row itself, and keeps the
    ///     tracking issue in its own column.
    /// </summary>
    [Test]
    public async Task ReservedSurface_RowsStateWhyTheyAreReserved()
    {
        IReadOnlyList<string> failures = ExemptionReason.RowsWithoutAReason(
            "DeadPublicInterfaceRule.ReservedSurface",
            ReservedSurface.Select(static r => (r.Type, new ExemptionReason.Row(r.Reason, r.TrackedBy))));

        await Assert.That(failures).IsEmpty()
            .Because(
                "a row that grants a permission and does not say why is indistinguishable from an "
                + "accident, and the reason lives in a comment no tool can read. Failures: "
                + string.Join(" | ", failures));
    }

    /// <summary>
    ///     Non-vacuity 1: the scan really walked a checkout and really found the
    ///     contract surface it grades.
    /// </summary>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan needs a repository checkout; without one it grades nothing and every "
                   + "rule above is satisfied by having nothing to look at");

        DeadPublicInterfaceReport report = Report.Value;

        await Assert.That(report.FilesScanned).IsGreaterThan(0)
            .Because("the scan read no .cs file under src/, apps/ or tests/; the roots or the file "
                   + "filter are wrong and the rule is vacuously green");

        await Assert.That(report.Scoped.Count).IsGreaterThan(0)
            .Because("the scan graded no public interface at all, so \"no dead interface\" is a "
                   + "statement about an empty set");

        foreach (ReservedPublicInterface row in ReservedSurface)
        {
            await Assert.That(report.Scoped.Any(s => s.FullName == row.Type)).IsTrue()
                .Because(
                    row.Type + " is on the reservation table, so it must be a graded public interface: "
                    + "a row naming a type the scan never saw is a permission for a problem that "
                    + "cannot exist");
        }
    }

    /// <summary>
    ///     Non-vacuity 2: the rule reports the three interfaces the audit named.
    /// </summary>
    /// <remarks>
    ///     The sources below are SYNTHETIC and carry the original names on purpose.
    ///     Two of the three were deleted by this change, so a control written against
    ///     the live tree could only ever prove the rule on the one that survived, and
    ///     a control written against new names would pass against a matcher that had
    ///     learned those names instead of the shape. These three are the shapes the
    ///     audit measured, and the rule must still find all three.
    /// </remarks>
    [Test]
    public async Task NonVacuity_TheAuditedThree_AreReportedWhenPresent()
    {
        DeadPublicInterfaceReport report = DeadPublicInterfaceProbe.ScanFiles(
        [
            ("src/Harbor.Abstractions/Ide/IHarborIdeClient.cs",
                """
                namespace Harbor.Abstractions.Ide;

                public interface IHarborIdeClient
                {
                    Task<Result<string>> ReadFileAsync(string path, CancellationToken ct = default);
                }
                """),
            ("src/Harbor.Terminal.Abstractions/Renderers/ITuiRenderBackend.cs",
                """
                namespace Harbor.Terminal.Abstractions.Renderers;

                public interface ITuiRenderBackend
                {
                    void Write(string text);
                    void WriteLine(string? text = null);
                }
                """),
            ("src/Harbor.Ui.Framework.Services/IRenderEngine.cs",
                """
                namespace Harbor.Ui.Framework.Services;

                public interface IRenderEngine
                {
                    void RenderChatLine(ChatLine line, ChatRole role);
                    void Clear();
                }
                """),
        ]);

        var reported = report.Findings
            .Select(static f => f.FullName)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

        string[] expected =
        [
            "Harbor.Abstractions.Ide.IHarborIdeClient",
            "Harbor.Terminal.Abstractions.Renderers.ITuiRenderBackend",
            "Harbor.Ui.Framework.Services.IRenderEngine",
        ];

        await Assert.That(reported).IsEquivalentTo(expected)
            .Because(
                "these three are the interfaces the #1831 audit measured as having 22 public members, "
                + "zero implementors and zero real consumers. The rule must still find them. A control "
                + "written only against the live tree would prove nothing once the two dead ones are "
                + "deleted. Reported: " + string.Join(" | ", reported));
    }

    /// <summary>
    ///     Non-vacuity 3: the rule discriminates. An interface with an implementor,
    ///     one with a consumer, and one mentioned only through <c>typeof()</c> must be
    ///     graded differently.
    /// </summary>
    [Test]
    public async Task NonVacuity_TheRuleDiscriminatesImplementorsConsumersAndReflection()
    {
        DeadPublicInterfaceReport report = DeadPublicInterfaceProbe.ScanFiles(
        [
            // (1) A dead interface: declared, never implemented, never used.
            ("src/Harbor.Abstractions/Tools/ISyntheticDead.cs",
                """
                namespace Harbor.Abstractions.Tools;

                public interface ISyntheticDead
                {
                    void Do();
                }
                """),
            // (2) An interface WITH an implementor.
            ("src/Harbor.Abstractions/Tools/ISyntheticImplemented.cs",
                """
                namespace Harbor.Abstractions.Tools;

                public interface ISyntheticImplemented
                {
                    void Do();
                }
                """),
            ("src/Harbor.Tools.Builtin/Tools/Synthetic/SyntheticImplementation.cs",
                """
                namespace Harbor.Tools.Builtin.Tools.Synthetic;

                using Harbor.Abstractions.Tools;

                public sealed class SyntheticImplementation : ISyntheticImplemented
                {
                    public void Do()
                    {
                    }
                }
                """),
            // (3) An interface with no implementor, but a CONSUMER that holds it.
            ("src/Harbor.Abstractions/Tools/ISyntheticConsumed.cs",
                """
                namespace Harbor.Abstractions.Tools;

                public interface ISyntheticConsumed
                {
                    void Do();
                }
                """),
            ("src/Harbor.Tools.Builtin/Tools/Synthetic/SyntheticHolder.cs",
                """
                namespace Harbor.Tools.Builtin.Tools.Synthetic;

                using Harbor.Abstractions.Tools;

                public sealed class SyntheticHolder
                {
                    private readonly ISyntheticConsumed _inner;

                    public SyntheticHolder(ISyntheticConsumed inner)
                    {
                        _inner = inner;
                    }
                }
                """),
            // (4) An interface mentioned ONLY through typeof(). This is the shape that
            //     made IHarborIdeClient look alive: a reflection-only mention is not a
            //     use.
            ("src/Harbor.Abstractions/Tools/ISyntheticReflected.cs",
                """
                namespace Harbor.Abstractions.Tools;

                public interface ISyntheticReflected
                {
                    void Do();
                }
                """),
            ("tests/Harbor.Abstractions.Tests/SyntheticReflectedTests.cs",
                """
                namespace Harbor.Abstractions.Tests;

                using Harbor.Abstractions.Tools;

                public class SyntheticReflectedTests
                {
                    public int Count() => typeof(ISyntheticReflected).GetMethods().Length;
                }
                """),
        ]);

        var reported = report.Findings
            .Select(static f => f.FullName)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

        string[] expected =
        [
            "Harbor.Abstractions.Tools.ISyntheticDead",
            "Harbor.Abstractions.Tools.ISyntheticReflected",
        ];

        await Assert.That(reported).IsEquivalentTo(expected)
            .Because(
                "the rule must separate the three shapes: an interface with an implementor is alive, an "
                + "interface a consumer holds is alive, and an interface nobody touches is dead. A "
                + "reflection-only mention is NOT a use, which is the exact shape that kept "
                + "IHarborIdeClient looking alive (a test that reflects over the type and calls none of "
                + "its members). Reported: " + string.Join(" | ", reported));
    }
}
