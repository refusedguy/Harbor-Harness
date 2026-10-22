// DeadPublicMemberRule.cs — issue #1879. The member-level half of the
// dead-surface finding #1831 made: a public member that nothing in the tree
// references is a promise nobody keeps, and nothing in the tree said so.
//
// WHERE THIS SITS
// ---------------
// #1831's audit found THREE public interfaces carrying 22 public members with
// zero implementors and zero real consumers. #1866 landed the interface-level
// rule for that (`DeadPublicInterfaceRule.cs`): a public interface in a
// contract assembly must have an implementor or a consumer, or it is on a
// named reservation table with a reason.
//
// A dead interface is one shape of dead surface. The other is a member on a
// LIVE type: a public contract assembly publishes `HarborEnv.DemoStepMs`, a
// public constant nobody reads, and a reader of the API has no way to tell it
// apart from the thirty constants beside it that the product reads on every
// run. This file grades that.
//
// THE MEASUREMENT
// ---------------
// A public member of a public type in a contract assembly is DEAD when its
// NAME occurs at most once in the whole reference set — that one occurrence
// being its own declaration.
//
// "At most once" is deliberately the CONSERVATIVE direction:
//
//   * a member declared on two lines (an overload pair) counts as two
//     occurrences and is therefore alive, even when nobody calls either;
//   * a member whose declaration line mentions its own name twice (a default
//     implementation calling the method it overrides) is alive for the same
//     reason;
//   * a member whose name is also used by an unrelated type is alive.
//
// The rule therefore under-reports rather than over-reports. A guard that
// cries wolf gets switched off, and this one is worth keeping.
//
// The reference set is every `.cs` and every `.axaml` file under `src/`,
// `apps/` and `tests/`. The `.axaml` half matters: an Avalonia binding names
// the property it binds (`{Binding IsUser}`), so a property bound in a view
// is used even though no C# file mentions it.
//
// String literals COUNT as references, and that is deliberate: a member
// reached by reflection is looked up BY NAME (`GetProperty("Foo")`), so a
// name inside a literal is a real use. It is also the conservative
// direction — a literal that happens to contain a member's name keeps the
// member alive.
//
// THE EXEMPTIONS, AND WHY EACH ONE IS REAL
// -----------------------------------------
// A rule that reports a member nothing calls is only useful if it does NOT
// report the members that are called by something other than a name in the
// source. There are four such shapes in this tree, and every one of them is
// exempt here:
//
//   1. A MEMBER OF AN INTERFACE. The interface's declaration IS the contract:
//      every implementor must supply the member, and the implementor's own
//      declaration names it. `IPluginHost.LoadPlugin`, `.LoadedPlugins`,
//      `.ShutdownAllAsync` and `.UnloadPlugin` are the four in scope, and all
//      four belong to the interface #1866 already reserved (issue #418). The
//      interface rule owns that decision; this rule does not second-guess it.
//
//   2. A CONSTRUCTOR. A constructor has no name of its own to be referenced
//      by: it is reached by `new T(...)`, which names the TYPE. Reporting
//      `PaletteModel<TItem>`'s constructor as a dead member would report a
//      type that the product constructs on every palette open.
//
//   3. A MEMBER OF A MARKER ATTRIBUTE. `ObservedAttribute.Span`,
//      `.Counter`, `.Exclude` and friends are read by the source generator
//      that consumes the attribute BY NAME — a generator reads the named
//      arguments of an attribute out of the syntax tree, so there is no C#
//      reference to find by construction. The repository already has four
//      such markers (`MoodFrameAttribute`, `TerminalEscapeAttribute`,
//      `TuiRendererAttribute` and `ObservedAttribute`), and
//      `docs/OBSERVED_INSTRUMENTATION.md` states the mechanism: "the
//      generator — a Roslyn source generator that reads the attribute".
//
//   4. A PUBLIC PROPERTY OF A TYPE THE JSON SERIALIZER WRITES. The
//      serializer writes properties by convention and reads them back by
//      convention, so a DTO property that only the serializer touches has no
//      name reference anywhere. The repository has exactly this shape — the
//      configuration contracts are written by `ConfigJsonContext` and its
//      siblings — so the category is real even where the scan currently finds
//      no in-scope member of it.
//
// A DI-REGISTERED SERVICE needs no exemption of its own, and the reason is
// worth stating because the task of extending the rule asked for it: a
// service the container resolves is reached through the INTERFACE it
// implements, so its contract members are case 1 above, and a concrete
// service's own extra members are called by name like any other member.
//
// WHAT THE TABLE IS FOR
// ---------------------
// The members below are dead TODAY and are tolerated with a reason each. The
// table is liveness-checked: a row whose member stops being dead (someone
// started using it) or stops existing (the member was deleted) fails the
// build, so the table cannot rot into a blanket permission for dead
// surface. That is the same discipline `DeadPublicInterfaceRule` uses, and
// the same reason the reason lives in the ROW and not in a comment above it.
//
// NON-VACUITY
// -----------
//   1. `Scan_IsLive` checks that the scan really walked a checkout and
//      really graded public members, so "nothing is dead" cannot be a
//      statement about an empty set.
//   2. `NonVacuity_ASyntheticMemberWithNoReference_IsReported` hands the
//      scan a synthetic tree and requires the dead member to be reported.
//      This is the control that fails if the rule is switched off.
//   3. `NonVacuity_ASyntheticMemberThatIsUsed_IsNotReported` requires the
//      member that IS referenced to stay out of the findings, so the rule
//      cannot be replaced by "report everything".
//
// System.Text.RegularExpressions is not a global using in this project, so
// this file imports it explicitly.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     One public member the scan graded, in a contract assembly.
/// </summary>
/// <param name="TypeName">The simple type name that declares the member.</param>
/// <param name="Namespace">The namespace the type is declared in.</param>
/// <param name="Member">The member name.</param>
/// <param name="Kind">What kind of member it is: method, property, event or field.</param>
/// <param name="File">The repo-relative file that declares it.</param>
internal sealed record DeadPublicMemberEntry(string TypeName, string Namespace, string Member, string Kind, string File)
{
    /// <summary>The full type name, as it appears in a table row.</summary>
    internal string FullTypeName => string.IsNullOrEmpty(Namespace) ? TypeName : Namespace + "." + TypeName;
}

/// <summary>
///     One public member with no reference anywhere in the tree. A promise
///     nobody keeps.
/// </summary>
/// <param name="TypeName">The simple type name that declares the member.</param>
/// <param name="Namespace">The namespace the type is declared in.</param>
/// <param name="Member">The member name.</param>
/// <param name="Kind">What kind of member it is.</param>
/// <param name="File">The repo-relative file that declares it.</param>
internal sealed record DeadPublicMemberFinding(string TypeName, string Namespace, string Member, string Kind, string File)
{
    /// <summary>The full type name, as it appears in a table row.</summary>
    internal string FullTypeName => string.IsNullOrEmpty(Namespace) ? TypeName : Namespace + "." + TypeName;

    /// <summary>The row key: the type and the member together.</summary>
    internal string Key => FullTypeName + "." + Member;
}

/// <summary>
///     One row of the tolerance table: a public member that the scan reports
///     as dead and that is tolerated, with the argument for why.
/// </summary>
/// <param name="Type">The full type name that declares the member.</param>
/// <param name="Member">The member name.</param>
/// <param name="Reason">Why the dead member is tolerated here.</param>
/// <param name="TrackedBy">The issue that owns the decision, when there is one.</param>
internal sealed record ToleratedDeadMember(string Type, string Member, string Reason, string? TrackedBy);

/// <summary>The scan's output: what it found, and what it looked at.</summary>
/// <param name="Findings">Public members with no reference anywhere.</param>
/// <param name="Scoped">Every public member the scan graded.</param>
/// <param name="FilesScanned">How many files the scan read.</param>
internal sealed record DeadPublicMemberReport(
    IReadOnlyList<DeadPublicMemberFinding> Findings,
    IReadOnlyList<DeadPublicMemberEntry> Scoped,
    int FilesScanned);

/// <summary>
///     Which of the two member scans to run.
/// </summary>
/// <remarks>
///     The rewrite made the scan cheaper in two ways: the types of a file are
///     parsed only in the contract trees (the only trees whose types are
///     graded), and the member list is walked once per FILE instead of once
///     per TYPE. Both are optimisations, and an optimisation that changes a
///     rule's verdicts is not an optimisation — so the scan before the
///     rewrite is kept as the ORACLE, and
///     <c>DeadPublicMemberEquivalenceTests</c> runs both over the live tree
///     and requires the same answer.
/// </remarks>
internal enum DeadPublicMemberScanMode
{
    /// <summary>
    ///     The current scan: the types of a file are parsed only in the
    ///     contract trees, and the member list is walked once per file.
    /// </summary>
    Current,

    /// <summary>
    ///     The scan before the rewrite: every file's types are parsed, and the
    ///     member list is walked once per type. This is the oracle, not a
    ///     production path.
    /// </summary>
    Legacy,
}

/// <summary>
///     The scanner behind <see cref="DeadPublicMemberRule" />. It is
///     source-based and drivable from synthetic sources, so the rule can be
///     proven rather than merely observed.
/// </summary>
internal static class DeadPublicMemberProbe
{
    /// <summary>
    ///     The contract assemblies: the ones whose public surface a third party
    ///     compiles against.
    /// </summary>
    /// <remarks>
    ///     This is the dead-INTERFACE rule's list, read from it rather than
    ///     copied, so the two rules cannot drift on what "a published
    ///     contract" means. A member is a published promise exactly when the
    ///     interface next to it is.
    /// </remarks>
    internal static readonly string[] ContractTrees = DeadPublicInterfaceProbe.ContractTrees;

    /// <summary>The trees the scan reads.</summary>
    internal static readonly string[] ScanTrees = ["src", "apps", "tests"];

    /// <summary>
    ///     This file, excluded from the reference set.
    /// </summary>
    /// <remarks>
    ///     The tolerance table below NAMES every member it tolerates, and a
    ///     name inside a string literal counts as a reference (a member
    ///     reached by reflection is looked up by name). Without this
    ///     exclusion the rule would keep its own tolerance rows alive and
    ///     then fail its own liveness check — the rule would grade itself
    ///     out of its own findings. A rule's table is not a use of the
    ///     member; it is the record that the member is dead.
    /// </remarks>
    internal const string SelfPath = "tests/Harbor.Architecture.Tests/DeadPublicMemberRule.cs";

    /// <summary>Directory names never descended into.</summary>
    private static readonly string[] PrunedDirectories = ["obj", "bin", "contrib", ".worktrees", "node_modules"];

    /// <summary>
    ///     Matches a namespace declaration and captures the name and whether it
    ///     opens a block (which puts every type inside it one level deeper).
    /// </summary>
    private static readonly Regex NamespaceDeclaration = new(
        @"\bnamespace\s+([\w.]+)\s*(?<terminator>[;{])",
        RegexOptions.Compiled);

    /// <summary>
    ///     Matches a public type declaration and captures its kind, its name
    ///     and its base list.
    /// </summary>
    private static readonly Regex PublicTypeDeclaration = new(
        @"^[ \t]*public\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+|ref\s+|file\s+)*"
        + @"(?:record\s+struct|record\s+class|class|interface|struct|record|enum)\s+(?<name>\w+)"
        + @"(?:\s*<[^>{;]*>)?(?:\s*\([^;{]*\))?"
        + @"(?:\s*:\s*(?<bases>[^{;]+))?",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    ///     Matches a public member declaration line and captures the member
    ///     name and what follows it: a parameter list, an accessor block, an
    ///     expression body, a semicolon or an assignment.
    /// </summary>
    /// <remarks>
    ///     The type part is deliberately restricted to horizontal whitespace:
    ///     a member declaration that spans two lines must not be matched
    ///     across the newline, or a bare `public string` would swallow the
    ///     next line's identifier.
    /// </remarks>
    private static readonly Regex PublicMemberDeclaration = new(
        @"^[ \t]+public\s+"
        + @"(?:static\s+|abstract\s+|virtual\s+|override\s+|sealed\s+|async\s+|new\s+|readonly\s+|const\s+|partial\s+|required\s+|extern\s+|unsafe\s+|file\s+)*"
        + @"(?:[\w\.<>\?\[\], \t]+?[ \t]+)?"
        + @"(?<name>\w+)[ \t]*(?:<[^>]*>)?[ \t]*(?<rest>\(|\{|=>|;|=)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Matches a nested type declaration, which is a type and not a member.</summary>
    private static readonly Regex NestedTypeDeclaration = new(
        @"\b(?:class|interface|struct|record|enum)\s+\w+",
        RegexOptions.Compiled);

    /// <summary>Matches an identifier.</summary>
    private static readonly Regex Identifier = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);

    /// <summary>
    ///     Matches the type argument of a JSON serializer call, which is a
    ///     type the serializer writes and reads by convention.
    /// </summary>
    private static readonly Regex JsonTypeArgument = new(
        @"Json(?:Serializer\.(?:Serialize|Deserialize)(?:Async)?|Serializable\(\s*typeof\()\s*<?\s*(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    /// <summary>Reads the repository and grades it.</summary>
    /// <param name="repoRoot">The checkout to read; <c>null</c> grades nothing.</param>
    internal static DeadPublicMemberReport Scan(string? repoRoot)
    {
        if (repoRoot is null)
        {
            return new DeadPublicMemberReport([], [], 0);
        }

        return ScanFiles(ReadSources(repoRoot));
    }

    /// <summary>
    ///     Reads the tree the rule grades: every <c>.cs</c> and every
    ///     <c>.axaml</c> file under the scan trees, minus the pruned
    ///     directories and the rule's own file.
    /// </summary>
    /// <param name="repoRoot">The checkout to read.</param>
    /// <returns>The repo-relative path and the raw source of every file.</returns>
    /// <remarks>
    ///     The reading is its own method because the equivalence test needs
    ///     the SAME BYTES twice: it scans them with the current parse and with
    ///     the parse before the rewrite, and two reads could differ.
    /// </remarks>
    internal static List<(string Rel, string Source)> ReadSources(string? repoRoot) =>
        ReadSources(repoRoot, ScanTrees);

    /// <summary>
    ///     Reads the named trees the rule grades.
    /// </summary>
    /// <param name="repoRoot">The checkout to read.</param>
    /// <param name="trees">The trees to read, relative to the checkout.</param>
    /// <returns>The repo-relative path and the raw source of every file.</returns>
    internal static List<(string Rel, string Source)> ReadSources(string? repoRoot, IReadOnlyList<string> trees)
    {
        var sources = new List<(string Rel, string Source)>();

        if (repoRoot is null)
        {
            return sources;
        }

        foreach (string tree in trees)
        {
            string dir = Path.Combine(repoRoot, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
                if (IsPruned(rel) || rel == SelfPath)
                {
                    continue;
                }

                if (SourceScan.TryReadAllText(path) is { } text)
                {
                    sources.Add((rel, text));
                }
            }

            // The XAML half of the reference set: an Avalonia binding names
            // the property it binds, so a property bound in a view is used
            // even though no C# file mentions it.
            foreach (string path in Directory.EnumerateFiles(dir, "*.axaml", SearchOption.AllDirectories))
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

        return sources;
    }

    /// <summary>
    ///     Grades an explicit set of sources. This is the control path: the
    ///     same code that grades the tree grades a synthetic one.
    /// </summary>
    /// <param name="sources">Repo-relative path and raw source, one pair per file.</param>
    internal static DeadPublicMemberReport ScanFiles(IReadOnlyList<(string Rel, string Source)> sources) =>
        ScanFiles(sources, DeadPublicMemberScanMode.Current);

    /// <summary>
    ///     Grades an explicit set of sources with the given scan.
    /// </summary>
    /// <param name="sources">Repo-relative path and raw source, one pair per file.</param>
    /// <param name="mode">Which scan to run.</param>
    /// <returns>The report: the findings, the graded surface and the file count.</returns>
    internal static DeadPublicMemberReport ScanFiles(
        IReadOnlyList<(string Rel, string Source)> sources,
        DeadPublicMemberScanMode mode)
    {
        var files = new List<ParsedFile>(sources.Count);
        foreach ((string rel, string source) in sources)
        {
            // The TYPES of a file are only graded in the contract trees, so
            // they are only PARSED there. A file outside them contributes its
            // identifiers — it may reference a contract member — and nothing
            // else, and the seven contract trees hold 224 files out of the
            // 2 665 the rule reads.
            files.Add(ParsedFile.Parse(rel, source, mode));
        }

        // How often each identifier occurs in the whole reference set.
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ParsedFile file in files)
        {
            foreach ((string token, int count) in file.TokenCounts)
            {
                totals[token] = totals.GetValueOrDefault(token) + count;
            }
        }

        // The types the JSON serializer writes by convention.
        var jsonTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (ParsedFile file in files)
        {
            foreach (string name in file.JsonTypeNames)
            {
                jsonTypes.Add(name);
            }
        }

        var scoped = new List<DeadPublicMemberEntry>();
        var findings = new List<DeadPublicMemberFinding>();

        foreach (ParsedFile file in files)
        {
            if (!IsInContractTree(file.Rel))
            {
                continue;
            }

            foreach (ParsedType type in file.PublicTypes)
            {
                foreach (ParsedMember member in type.Members)
                {
                    scoped.Add(new DeadPublicMemberEntry(
                        type.Name, file.Namespace, member.Name, member.Kind, file.Rel));

                    if (IsExempt(type, member, jsonTypes))
                    {
                        continue;
                    }

                    if (totals.GetValueOrDefault(member.Name) <= 1)
                    {
                        findings.Add(new DeadPublicMemberFinding(
                            type.Name, file.Namespace, member.Name, member.Kind, file.Rel));
                    }
                }
            }
        }

        return new DeadPublicMemberReport(findings, scoped, files.Count);
    }

    /// <summary>
    ///     Whether a member is exempt from the rule, and why.
    /// </summary>
    /// <param name="type">The type that declares the member.</param>
    /// <param name="member">The member itself.</param>
    /// <param name="jsonTypes">The types the JSON serializer writes by convention.</param>
    /// <returns><see langword="true" /> when the member must not be reported.</returns>
    private static bool IsExempt(ParsedType type, ParsedMember member, HashSet<string> jsonTypes)
    {
        // 1. An interface's members belong to the interface rule.
        if (type.Kind == "interface")
        {
            return true;
        }

        // 2. A constructor is reached by `new T(...)`, which names the type.
        if (member.Kind == "constructor")
        {
            return true;
        }

        // 3. A marker attribute's named arguments are read by a source
        //    generator by name; there is no C# reference to find.
        if (type.IsMarkerAttribute)
        {
            return true;
        }

        // 4. A property of a type the JSON serializer writes by convention.
        if (member.Kind == "property" && jsonTypes.Contains(type.Name))
        {
            return true;
        }

        return false;
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

    /// <summary>
    ///     How many public types the scan finds in one file.
    /// </summary>
    /// <param name="rel">The repo-relative path.</param>
    /// <param name="source">The raw source.</param>
    /// <param name="mode">Which scan to run.</param>
    /// <returns>The number of public types the file declares.</returns>
    /// <remarks>
    ///     The equivalence test uses it to show that the gate really does
    ///     skip work: a file outside the contract trees has public types
    ///     under the legacy scan and NONE under the current one.
    /// </remarks>
    internal static int PublicTypeCount(string rel, string source, DeadPublicMemberScanMode mode) =>
        ParsedFile.Parse(rel, source, mode).PublicTypes.Count;

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
    ///     One public type declaration, reduced to the facts the rule grades.
    /// </summary>
    private sealed class ParsedType
    {
        /// <summary>The simple type name.</summary>
        internal required string Name { get; init; }

        /// <summary>The kind: class, interface, struct, record or enum.</summary>
        internal required string Kind { get; init; }

        /// <summary>
        ///     Whether the type is a MARKER ATTRIBUTE — a type whose base list
        ///     names an <c>...Attribute</c> type.
        /// </summary>
        internal required bool IsMarkerAttribute { get; init; }

        /// <summary>The public members the type declares.</summary>
        internal required List<ParsedMember> Members { get; init; }
    }

    /// <summary>One public member declaration.</summary>
    private sealed class ParsedMember
    {
        /// <summary>The member name.</summary>
        internal required string Name { get; init; }

        /// <summary>The kind: method, property, event, field or constructor.</summary>
        internal required string Kind { get; init; }
    }

    /// <summary>
    ///     One source file, reduced to the facts the rule grades.
    /// </summary>
    private sealed class ParsedFile
    {
        /// <summary>The repo-relative path.</summary>
        internal required string Rel { get; init; }

        /// <summary>The file's namespace, or the empty string.</summary>
        internal required string Namespace { get; init; }

        /// <summary>How often each identifier occurs in the file.</summary>
        internal required Dictionary<string, int> TokenCounts { get; init; }

        /// <summary>The simple names of the types the JSON serializer writes.</summary>
        internal required HashSet<string> JsonTypeNames { get; init; }

        /// <summary>The public types the file declares.</summary>
        internal required List<ParsedType> PublicTypes { get; init; }

        /// <summary>Parses one file into the facts above.</summary>
        /// <param name="rel">The repo-relative path.</param>
        /// <param name="source">The raw source.</param>
        /// <param name="mode">Which scan to run.</param>
        internal static ParsedFile Parse(string rel, string source, DeadPublicMemberScanMode mode)
        {
            // TWO views of the same file, on purpose.
            //
            // The STRUCTURE view has the string and char literal contents
            // blanked as well as the comments. This rule counts braces to
            // find the body of a type, and a "}" inside a literal would throw
            // the depth off and hide a declaration — which reads as "no
            // members" and would report a live contract as dead. The dead-
            // interface rule's own lexer is used rather than a second copy.
            //
            // The TOKEN view keeps the literal contents, because a member
            // reached by reflection is looked up BY NAME: a name inside a
            // string literal is a real use.
            string structure = SourceParse.BlankLiteralsAndComments(source);
            string tokens = SourceScan.StripComments(source);

            Match namespaceMatch = NamespaceDeclaration.Match(structure);
            string ns = namespaceMatch.Success ? namespaceMatch.Groups[1].Value : string.Empty;
            bool namespaceIsBlock = namespaceMatch.Success
                && namespaceMatch.Groups["terminator"].Value == "{";
            int baseDepth = namespaceIsBlock ? 1 : 0;

            // The identifiers of the file, counted. The legacy form walked the
            // matches of a regex and materialised a `Match` AND a `string` for
            // every identifier of every file in the tree; the substrate counts
            // them through a span lookup, so a string exists only for a token
            // the dictionary has not seen. The tokens are the regex's own.
            var tokenCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            SourceParse.CountIdentifiers(tokens, tokenCounts);

            var jsonTypeNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match json in JsonTypeArgument.Matches(structure))
            {
                jsonTypeNames.Add(json.Groups["name"].Value);
            }

            // The types of a file outside the contract trees are never graded,
            // so they are never parsed: the brace frames, the two declaration
            // regexes and the member walk are the expensive half of this
            // method, and 94 % of the files the rule reads do not need them.
            //
            // The LEGACY scan parses them anyway — that is what makes it the
            // oracle: it is the scan before the rewrite, and the equivalence
            // test requires the two to say the same thing.
            bool parseTypes = mode == DeadPublicMemberScanMode.Legacy || IsInContractTree(rel);
            if (!parseTypes)
            {
                return new ParsedFile
                {
                    Rel = rel,
                    Namespace = ns,
                    TokenCounts = tokenCounts,
                    JsonTypeNames = jsonTypeNames,
                    PublicTypes = [],
                };
            }

            (int[] starts, int[] depths) = ComputeLineFrames(structure);
            var frames = (starts, depths);

            // The member declarations of the file, ONCE. `Regex.Matches`
            // re-runs the pattern on every enumeration, so a file with many
            // types would otherwise re-scan the whole file per type.
            var memberMatches = new List<Match>();
            foreach (Match candidate in PublicMemberDeclaration.Matches(structure))
            {
                memberMatches.Add(candidate);
            }

            var publicTypes = new List<ParsedType>();

            // The member list is walked ONCE, not once per type. The rule used
            // to iterate EVERY member declaration of the file for EVERY type
            // of the file, which is the "a loop inside a loop" shape: a file
            // with twenty types and two hundred members cost four thousand
            // iterations, and each of them materialised the member's text
            // twice. The types are in source order and their bodies are
            // disjoint, so a cursor that only moves forward is enough: it
            // skips the members that belong to an EARLIER type and stops at
            // the first member that belongs to a LATER one.
            int memberCursor = 0;

            foreach (Match declaration in PublicTypeDeclaration.Matches(structure))
            {
                // Only a TOP-LEVEL type is a published contract. A public type
                // nested inside another type is an internal detail that
                // happens to be public, and the interface rule says the same
                // thing for the same reason.
                if (DepthAt(frames, declaration.Index) != baseDepth)
                {
                    continue;
                }

                string kind = KindOf(declaration.Value);
                string name = declaration.Groups["name"].Value;

                string bases = declaration.Groups["bases"].Success
                    ? declaration.Groups["bases"].Value
                    : string.Empty;

                bool isMarker = IsMarkerAttributeBase(bases);

                var members = new List<ParsedMember>();
                int bodyEnd = EndOfType(structure, declaration);

                if (mode == DeadPublicMemberScanMode.Legacy)
                {
                    // The scan BEFORE the rewrite: EVERY member declaration of
                    // the file is walked for EVERY type of the file. This is the
                    // "a loop inside a loop" shape — a file with twenty types
                    // and two hundred members cost four thousand iterations,
                    // and each of them materialised the member's text twice.
                    // It is kept because it is the ORACLE: the equivalence
                    // test requires this walk and the cursor walk below to
                    // find the same members.
                    foreach (Match member in memberMatches)
                    {
                        // A member of THIS type: inside the type's own body,
                        // one level down. A member of a LATER type in the same
                        // file is outside the body, and a member of a NESTED
                        // type is deeper — neither is this type's surface.
                        if (member.Index < declaration.Index || member.Index > bodyEnd)
                        {
                            continue;
                        }

                        if (DepthAt(frames, member.Index) != baseDepth + 1)
                        {
                            continue;
                        }

                        // A nested type is a type, not a member of the contract.
                        if (NestedTypeDeclaration.IsMatch(member.Value))
                        {
                            continue;
                        }

                        members.Add(MemberOf(member, name));
                    }
                }
                else
                {
                    while (memberCursor < memberMatches.Count
                        && memberMatches[memberCursor].Index < declaration.Index)
                    {
                        memberCursor++;
                    }

                    for (int m = memberCursor; m < memberMatches.Count; m++)
                    {
                        Match member = memberMatches[m];

                        // A member of a LATER type in the same file is outside this
                        // type's body, and it starts a later type's window.
                        if (member.Index > bodyEnd)
                        {
                            break;
                        }

                        // A member of THIS type: inside the type's own body, one
                        // level down. A member of a NESTED type is deeper — it is
                        // not this type's surface.
                        if (DepthAt(frames, member.Index) != baseDepth + 1)
                        {
                            continue;
                        }

                        // A nested type is a type, not a member of the contract.
                        if (NestedTypeDeclaration.IsMatch(member.Value))
                        {
                            continue;
                        }

                        members.Add(MemberOf(member, name));
                    }
                }

                publicTypes.Add(new ParsedType
                {
                    Name = name,
                    Kind = kind,
                    IsMarkerAttribute = isMarker,
                    Members = members,
                });
            }

            return new ParsedFile
            {
                Rel = rel,
                Namespace = ns,
                TokenCounts = tokenCounts,
                JsonTypeNames = jsonTypeNames,
                PublicTypes = publicTypes,
            };
        }

        /// <summary>
        ///     One member declaration, reduced to the two facts the rule
        ///     grades: its name and what kind of member it is.
        /// </summary>
        /// <param name="member">The member declaration match.</param>
        /// <param name="typeName">The name of the type that declares it.</param>
        private static ParsedMember MemberOf(Match member, string typeName)
        {
            string memberName = member.Groups["name"].Value;
            string rest = member.Groups["rest"].Value;

            return new ParsedMember
            {
                Name = memberName,
                Kind = MemberKind(member.Value, memberName, typeName, rest),
            };
        }

        /// <summary>
        ///     The offset just past the body of the type declared at
        ///     <paramref name="declaration" />.
        /// </summary>
        /// <param name="code">The comment- and literal-stripped source.</param>
        /// <param name="declaration">The type declaration match.</param>
        private static int EndOfType(string code, Match declaration)
        {
            int open = code.IndexOf('{', declaration.Index + declaration.Length);
            if (open < 0)
            {
                return declaration.Index;
            }

            int depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{')
                {
                    depth++;
                }
                else if (code[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return code.Length;
        }

        /// <summary>
        ///     The brace depth before each line of the file, and the offset each
        ///     line starts at.
        /// </summary>
        /// <param name="code">The comment- and literal-stripped source.</param>
        private static (int[] Starts, int[] Depths) ComputeLineFrames(string code)
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

            return ([.. starts], [.. depths]);
        }

        /// <summary>
        ///     The brace depth at the offset <paramref name="index" />, from the
        ///     per-line depth table.
        /// </summary>
        /// <param name="frames">The per-line frames.</param>
        /// <param name="index">The offset.</param>
        private static int DepthAt((int[] Starts, int[] Depths) frames, int index)
        {
            int[] starts = frames.Starts;
            int[] depths = frames.Depths;

            int low = 0;
            int high = starts.Length - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (starts[mid] <= index)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return depths[low];
        }

        /// <summary>
        ///     The kind of a type declaration: class, interface, struct, record
        ///     or enum.
        /// </summary>
        /// <param name="declaration">The declaration text.</param>
        private static string KindOf(string declaration)
        {
            if (declaration.Contains("interface ", StringComparison.Ordinal))
            {
                return "interface";
            }

            if (declaration.Contains("enum ", StringComparison.Ordinal))
            {
                return "enum";
            }

            if (declaration.Contains("struct ", StringComparison.Ordinal))
            {
                return "struct";
            }

            if (declaration.Contains("record ", StringComparison.Ordinal))
            {
                return "record";
            }

            return "class";
        }

        /// <summary>
        ///     Whether a base list names an ATTRIBUTE type, which makes the
        ///     declaring type a marker attribute whose named arguments are read
        ///     by a source generator by name.
        /// </summary>
        /// <param name="bases">The base list text.</param>
        private static bool IsMarkerAttributeBase(string bases)
        {
            foreach (Match token in Identifier.Matches(bases))
            {
                if (token.Value.EndsWith("Attribute", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///     The kind of a member declaration: method, property, event, field
        ///     or constructor.
        /// </summary>
        /// <param name="declaration">The declaration line.</param>
        /// <param name="memberName">The member name.</param>
        /// <param name="typeName">The name of the declaring type.</param>
        /// <param name="rest">What follows the name: <c>(</c>, <c>{</c>, <c>=&gt;</c>, <c>;</c> or <c>=</c>.</param>
        private static string MemberKind(string declaration, string memberName, string typeName, string rest)
        {
            if (declaration.Contains("event ", StringComparison.Ordinal))
            {
                return "event";
            }

            if (rest == "(")
            {
                return string.Equals(memberName, typeName, StringComparison.Ordinal)
                    ? "constructor"
                    : "method";
            }

            if (rest is "{" or "=>")
            {
                return "property";
            }

            return "field";
        }
    }
}

/// <summary>
///     Issue #1879: a public member in a contract assembly must be referenced
///     somewhere in the tree, unless it is on the named tolerance table. The
///     table's rows state their reason, carry their tracking issue, and are
///     liveness-checked.
/// </summary>
public sealed class DeadPublicMemberRule
{
    /// <summary>
    ///     The tolerance table: public members that the scan reports as dead
    ///     and that are tolerated, each with the argument for why.
    /// </summary>
    /// <remarks>
    ///     Every row is a real dead member with a real reason — a constant
    ///     the read sites bypass with a literal, a helper nobody calls, a
    ///     public property of a model nothing renders yet. The table may only
    ///     shrink: the liveness test below fails when a row stops being a
    ///     finding, so it cannot rot into a blanket permission.
    /// </remarks>
    private static readonly ToleratedDeadMember[] ToleratedSurface =
    [
        new(
            "Harbor.Abstractions.Env.HarborEnv",
            "DemoStepMs",
            "The demo recorder reads the variable by its literal spelling "
            + "(\"HARBOR_DEMO_STEP_MS\" in apps/Harbor.App.Cli/Demo/DemoCellForgeScreen.cs), so the "
            + "constant is bypassed: the environment variable is live and the constant is not. The fix is "
            + "one line at the read site, and until then the constant is a second spelling of a name "
            + "that already has one.",
            "https://github.com/refusedguy/Harbor-Harness/issues/1879"),
        new(
            "Harbor.Abstractions.Env.HarborEnv",
            "McpOAuthToken",
            "The MCP registry reads the variable by its literal spelling "
            + "(\"HARBOR_MCP_OAUTH_TOKEN\" in src/Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs), so the "
            + "constant is bypassed while the fallback it names is live. The fix is one line at the read "
            + "site; the constant's own doc comment promises that every read site references it.",
            "https://github.com/refusedguy/Harbor-Harness/issues/1879"),
        new(
            "Harbor.Abstractions.Env.HarborEnv",
            "SkillFreshness",
            "The panel registration carries its own constant for the same "
            + "variable (SkillFreshnessPanelRegistration.OptInVariable), so this constant has no reader at "
            + "all. Two constants for one variable is the defect this row records; the fix is to point the "
            + "registration at this one.",
            "https://github.com/refusedguy/Harbor-Harness/issues/1879"),
        new(
            "Harbor.Abstractions.Env.HarborEnv",
            "TuiNoBuiltinPanels",
            "The only reader of this variable is in contrib/, which CI does "
            + "not compile and AGENTS.md marks unmaintained. The opt-out therefore protects nothing that "
            + "ships; the constant advertises a knob that no shipped renderer honours.",
            "https://github.com/refusedguy/Harbor-Harness/issues/1879"),
        new(
            "Harbor.Abstractions.Env.HarborEnv",
            "WithAllProviders",
            "HARBOR_WITH_ALL_PROVIDERS is a COMPILE-TIME symbol in this tree "
            + "(the #if of the same name in src/Harbor.Hosting/HarborFeatureSet.cs and in the apps), not "
            + "an environment variable. As an environment variable it is read by nobody, so the constant "
            + "is a second spelling of a build flag.",
            "https://github.com/refusedguy/Harbor-Harness/issues/1879"),
        new(
            "Harbor.Abstractions.Tools.JsonArgs",
            "RequireInt",
            "A public helper with no caller: the sibling RequireString has "
            + "them, and no tool in the tree asks for a required integer. It is kept because it is the "
            + "documented pair of RequireString, and the pair is what a new tool would reach for — but "
            + "nothing reaches for it yet.",
            "https://github.com/refusedguy/Harbor-Harness/issues/1879"),
        new(
            "Harbor.Plugins.Abstractions.PluginScript",
            "CapabilityDirective",
            "The parser that reads the directive hardcodes the spelling "
            + "(\"harbor:capabilities\" inside the CapabilitiesPattern regular expression), so the "
            + "constant that was meant to be the single source of that spelling is never read. The "
            + "variable is live; the constant is a third spelling of it.",
            "https://github.com/refusedguy/Harbor-Harness/issues/1879"),
        new(
            "Harbor.Ui.Framework.Diagnostics.DiagnosticsPanelLoggerExtensions",
            "AddDiagnosticsPanel",
            "A DI wiring helper with no caller: both composition roots add "
            + "the provider directly (builder.Logging.AddProvider(new "
            + "DiagnosticsPanelLoggerProvider(panel))). The helper documents the intended wiring and "
            + "nobody adopted it, so it is a second way to do a thing that is done a third way.",
            "https://github.com/refusedguy/Harbor-Harness/issues/1879"),
        new(
            "Harbor.Ui.Framework.Overlays.PaletteModel",
            "VisibleItems",
            "The palette model's result set, documented as the surface a "
            + "renderer paints from, but no renderer in the tree reads it yet — the palette work is "
            + "still mid-flight (the model's own comment names epic C as pending). The property is the "
            + "model's contract with a renderer that does not exist in this tree.",
            "https://github.com/refusedguy/Harbor-Harness/issues/1879"),
    ];

    /// <summary>The live scan of the checkout, computed once.</summary>
    private static readonly Lazy<DeadPublicMemberReport> Report = new(
        () => DeadPublicMemberProbe.Scan(RepoPaths.RepoRoot));

    /// <summary>
    ///     Rule 1: every graded public member is referenced somewhere, or it is
    ///     on the tolerance table.
    /// </summary>
    [Test]
    public async Task EveryScopedPublicMember_IsReferencedOrTolerated()
    {
        DeadPublicMemberReport report = Report.Value;

        var tolerated = new HashSet<string>(
            ToleratedSurface.Select(static row => row.Type + "." + row.Member),
            StringComparer.Ordinal);

        var unexpected = report.Findings
            .Where(f => !tolerated.Contains(f.Key))
            .Select(static f => f.Kind + " " + f.Key + " (" + f.File + ")")
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToList();

        await Assert.That(unexpected).IsEmpty()
            .Because(
                "a public member of a public type in a contract assembly that nothing in src/, apps/ or "
                + "tests/ references is a promise nobody keeps: the public-API baseline freezes it, a "
                + "reader assumes it is maintained, and the next author copies it. Either use it, delete "
                + "it, or tolerate it on the DeadPublicMemberRule.ToleratedSurface table with the reason "
                + "and the owning issue. Unreferenced: " + string.Join(" | ", unexpected));
    }

    /// <summary>
    ///     Rule 2: the tolerance table is live. A row whose member is now
    ///     referenced, or no longer exists, must be removed.
    /// </summary>
    [Test]
    public async Task ToleratedSurface_RowsAreStillDeadMembers()
    {
        DeadPublicMemberReport report = Report.Value;

        var found = new HashSet<string>(
            report.Findings.Select(static f => f.Key),
            StringComparer.Ordinal);

        var scoped = new HashSet<string>(
            report.Scoped.Select(static s => s.FullTypeName + "." + s.Member),
            StringComparer.Ordinal);

        var stale = new List<string>();

        foreach (ToleratedDeadMember row in ToleratedSurface)
        {
            string key = row.Type + "." + row.Member;

            if (!scoped.Contains(key))
            {
                stale.Add(key + ": the member is not a graded public member any more");
                continue;
            }

            if (!found.Contains(key))
            {
                stale.Add(key + ": it is referenced now, so the tolerance is over");
            }
        }

        await Assert.That(stale).IsEmpty()
            .Because(
                "a tolerance row is a promise that a dead member is being kept ON PURPOSE, and the promise "
                + "ends when somebody starts using the member or deletes it. Keeping the row after that is "
                + "how an allow-list becomes a blanket permission: remove the row in the same commit that "
                + "uses or deletes the member. Stale rows: " + string.Join(" | ", stale));
    }

    /// <summary>
    ///     Rule 3: every tolerance row states why, in the row itself, and keeps
    ///     the tracking issue in its own column.
    /// </summary>
    [Test]
    public async Task ToleratedSurface_RowsStateWhyTheyAreTolerated()
    {
        IReadOnlyList<string> failures = ExemptionReason.RowsWithoutAReason(
            "DeadPublicMemberRule.ToleratedSurface",
            ToleratedSurface.Select(static r => (r.Type + "." + r.Member, new ExemptionReason.Row(r.Reason, r.TrackedBy))));

        await Assert.That(failures).IsEmpty()
            .Because(
                "a row that grants a permission and does not say why is indistinguishable from an "
                + "accident, and the reason lives in a comment no tool can read. Failures: "
                + string.Join(" | ", failures));
    }

    /// <summary>
    ///     Non-vacuity 1: the scan really walked a checkout and really graded
    ///     public members.
    /// </summary>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan needs a repository checkout; without one it grades nothing and every rule "
                   + "above is satisfied by having nothing to look at");

        DeadPublicMemberReport report = Report.Value;

        await Assert.That(report.FilesScanned).IsGreaterThan(0)
            .Because("the scan read no file under src/, apps/ or tests/; the roots or the file filter are "
                   + "wrong and the rule is vacuously green");

        await Assert.That(report.Scoped.Count).IsGreaterThan(0)
            .Because("the scan graded no public member at all, so \"nothing is dead\" is a statement about "
                   + "an empty set");

        await Assert.That(report.Scoped.Select(static s => s.FullTypeName).Distinct(StringComparer.Ordinal).Count())
            .IsGreaterThan(0)
            .Because("the scan must have graded at least one public TYPE; a member count of zero with a "
                   + "type count of zero means the contract-tree filter stopped matching everything");
    }

    /// <summary>
    ///     Non-vacuity 2: a member with no reference is reported. This is the
    ///     control that fails if the rule is switched off.
    /// </summary>
    [Test]
    public async Task NonVacuity_ASyntheticMemberWithNoReference_IsReported()
    {
        DeadPublicMemberReport report = DeadPublicMemberProbe.ScanFiles(
        [
            ("src/Harbor.Abstractions/Synthetic/SyntheticDead.cs",
                """
                namespace Harbor.Abstractions.Synthetic;

                public sealed class SyntheticDead
                {
                    public string NeverReferenced { get; set; } = string.Empty;

                    public void NeverCalled()
                    {
                    }
                }
                """),
        ]);

        var reported = report.Findings
            .Select(static f => f.Key)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(reported).IsEquivalentTo(new[]
            {
                "Harbor.Abstractions.Synthetic.SyntheticDead.NeverCalled",
                "Harbor.Abstractions.Synthetic.SyntheticDead.NeverReferenced",
            })
            .Because(
                "this is the control that says the rule is load-bearing: a public member nobody references "
                + "must come out of the scan. If this fails, the rule is switched off and every promise it "
                + "was making is unkept. Reported: " + string.Join(" | ", reported));
    }

    /// <summary>
    ///     Non-vacuity 3: a member that IS referenced is not reported, so the
    ///     rule cannot be replaced by "report everything".
    /// </summary>
    [Test]
    public async Task NonVacuity_ASyntheticMemberThatIsUsed_IsNotReported()
    {
        DeadPublicMemberReport report = DeadPublicMemberProbe.ScanFiles(
        [
            ("src/Harbor.Abstractions/Synthetic/SyntheticUsed.cs",
                """
                namespace Harbor.Abstractions.Synthetic;

                public sealed class SyntheticUsed
                {
                    public string Referenced { get; set; } = string.Empty;

                    public void Called()
                    {
                    }
                }
                """),
            ("src/Harbor.Tools.Builtin/Synthetic/SyntheticUser.cs",
                """
                namespace Harbor.Tools.Builtin.Synthetic;

                using Harbor.Abstractions.Synthetic;

                public sealed class SyntheticUser
                {
                    public void Use(SyntheticUsed used)
                    {
                        used.Referenced = string.Empty;
                        used.Called();
                    }
                }
                """),
        ]);

        var reported = report.Findings
            .Select(static f => f.Key)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(reported).IsEmpty()
            .Because(
                "a member that another file references is used, and a rule that reports it is a rule that "
                + "cries wolf. Reported: " + string.Join(" | ", reported));
    }
}
