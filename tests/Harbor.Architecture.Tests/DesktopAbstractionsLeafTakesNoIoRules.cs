// DesktopAbstractionsLeafTakesNoIoRules.cs — the guard for #534.
//
// WHY THIS FILE EXISTS
// --------------------
// #534 was filed as a capability-rules item: `JsonCommonConfigStore` and
// `JsonAppConfigStore<T>` sat in `Harbor.Desktop.Abstractions` and called
// `File.*` / `Directory.*`, so four rows in
// `PresentationCapabilityRules.KnownViolations` kept the rule green. The rows are
// the SYMPTOM, and a baseline row is a permission rather than a fix — #720 said
// so in its own header when it deleted the two CellForge rows for the same
// reason, and #742 repeated it for the token leaf.
//
// The defect underneath the rows is that this leaf — which the matrix places in
// PRESENTATION, and has placed there since the matrix row was created
// (`FullLayerMatrixTests.Matrix["Harbor.Desktop.Abstractions"] =
// new(Layer.Presentation, …)`, 2026-08-25, 5d2df19f) — was doing storage:
// `Harbor.Desktop.Abstractions` is `IsPackable`,
// `PackageId: Harbor.Desktop.Abstractions`, a published package, and it shipped a
// `File.WriteAllText → File.Delete → File.Move` sequence writing into the user's
// home directory. The config STORES are persistence; the config SCHEMA is not.
// #534's own "done when" — the four rows deleted, no `System.IO.File*` left in
// the project — is reachable only by taking the I/O out.
//
// #895 CORRECTED THE REASONING HERE, AND IT GOT STRONGER
// ------------------------------------------------------
// This header used to say the defect was that the leaf was the ONE project the
// layer matrix misfiled. Its exact wording is not reproduced here only because
// `LayerClaimMatchesMatrixRules` grades that phrase wherever it appears — a
// guard that fails on the file next door is a guard nobody can read. The matrix
// has said Presentation since the row was created, so the argument was built on
// a layer this code has never had. The conclusion survives — a published
// Presentation package writing into `~/.harbor` is still wrong — but it does not
// need the false premise:
//
//   * `PresentationCapabilityRules` already forbids `System.IO.File*` and
//     `System.IO.Directory*` in EVERY Presentation assembly, and its perimeter
//     is `FullLayerMatrixTests.PresentationLayerAssemblies()` — DERIVED from the
//     matrix row, not a hand-typed list. So this leaf was in scope by
//     construction, and the four rows #534 deleted were a narrowing of a rule
//     that applied anyway. `RequireLoaded` THROWS when a Presentation assembly
//     is not loadable, so "the rule ran against this assembly" is a fact the
//     test proves, not one it assumes.
//   * The leaf has carried NO baseline row since #534, so the file rules are
//     ARMED against it: one `File.*` is red on the spot, with no waiver to
//     widen. That is the opposite of the "permission" the old wording implied.
//
// So the scan below is not a special case bolted on for one unlucky package. It
// restates, as a source fact, an invariant a Presentation rule already holds —
// and adds the half no capability rule can express, which is rule 2 below.
// `IsPackable` stays in the record because it is true, and because a NuGet
// consumer is a second and independent reader of the same mistake; it is not the
// reason this file exists.
//
// WHICH DIRECTION THIS ASSUMES, AND WHY THE OTHER ONE IS NOT AVAILABLE
// --------------------------------------------------------------------
// A leaf that does I/O can be reconciled two ways: give it a seam to call (pull
// infrastructure UP into the leaf) or take the I/O out (push it DOWN into an
// outer layer). The repository's own matrix settles this in code rather than in
// prose, and it settles it the same way #742 recorded for the sibling leaf:
//
//   * `FullLayerMatrixTests.Matrix["Harbor.Desktop.Abstractions"]` allows no
//     Harbor.Storage.* / Harbor.Tools.* edge, so there is nowhere for the leaf to
//     call an infrastructure store even if a port were declared for it.
//   * An Infrastructure row may reference Domain, Application or a sibling family
//     but NEVER Presentation. `Harbor.Desktop.Abstractions` is in the Presentation
//     band, so "put the JSON+filesystem implementation in Infrastructure" — #534's
//     own first suggestion, before it considered the port — is an edge the matrix
//     forbids, not a destination.
//
// What remains is the composition root, which is what actually happened: the
// stores moved next to `ConfigurationModule`, which already constructed both of
// them. So the PORTS (`ICommonConfigStore`, `IAppConfigStore<T>`) and the DTOs
// (`CommonConfig`, `AppConfigBase`, `CompositeConfig<T>`) stay in the leaf, and
// the persistence moved out — the same split #742 made for `IThemeStore`.
//
// WHY THIS IS A NEW FILE AND NOT A `ResolvedViolations` ROW
// --------------------------------------------------------
// `PresentationCapabilityRules.ResolvedRows_AreWellFormed` resolves every row's
// type name against the real assembly and fails on a miss, so a
// ("Harbor.Desktop.Abstractions", …, "…JsonCommonConfigStore") row would go red
// the moment the type left — which is the point of the fix. A resolved-row entry
// cannot express "this type used to be here and is now gone", so the negative
// assertion has to be a SOURCE scan over the project directory, which is what
// this file is. Do not "helpfully" add the rows back: they will fail the build,
// not document the move.
//
// WHAT IS RULED
// -------------
//   1. No disk call anywhere in `src/Harbor.Desktop.Abstractions`. The
//      capability rules already assert this, over IL, with an EMPTY baseline for
//      this assembly since #534 — an armed rule, not a permission (it used to
//      read "as four baselined rows", which stopped being true when #534 deleted
//      them). This states the same invariant as a SOURCE fact, which reaches one
//      thing IL cannot: a `.cs` file sitting in the project directory that the
//      compile does not pick up is still a persistence claim in the leaf.
//   2. The two file-backed stores are not DECLARED in the leaf, while the two
//      ports they implement still are. No capability rule can say this — a
//      rule about capabilities has no vocabulary for a type that is ABSENT — and
//      rule 1 alone would be satisfied by a store that had been reduced to a
//      wrapper around someone else's I/O. This states the shape #534 actually
//      wants — the contract is the leaf's, the bytes are not — and the port half
//      keeps the "move" from turning into "delete".
//   3. The leaf enumeration is non-empty, so neither rule can pass because the
//      probe read nothing.
//   4. Non-vacuity for the disk scanner, in the shape `DesignSystemLeafTakesNoIoRules`
//      uses for the sibling leaf: the scanner must still find a real call in a
//      file that legitimately has one, so a green rule is distinguishable from a
//      broken regex.
//
// WHAT IS DELIBERATELY NOT RULED, AND WHY
// ----------------------------------------
//   * `System.IO.Path` — pure string manipulation (`Path.Combine`), no syscall.
//     Same reasoning as `PresentationCapabilityRules`' own "DELIBERATELY NOT
//     RULED" note and `DesignSystemLeafTakesNoIoRules`'; do not add it here.
//   * `Environment.GetFolderPath` / `GetEnvironmentVariable` in
//     `CommonConfig.ComputeDefaultHarborHome`. This is the one place the leaf
//     resolves a location, and it is deliberately left where it is: it is PATH
//     RESOLUTION, not persistence. `CommonConfig` is the config schema, and a
//     schema that cannot say where it lives cannot be loaded — the file it names
//     is one the layer declared as its own, which is a different act from writing
//     user data. `PresentationCapabilityRules` declines to rule these members for
//     the same reason (renderers use them pervasively) and tracks the wider
//     question in #518; #534's "done when" names only `File.*`/`Directory.*`.
//     Note the contrast with `Harbor.Desktop.Shared`'s
//     `ThemeService.LoadJson`, which is a composition root and unrestricted by
//     the matrix.
//   * WHICH outer assembly holds the implementation, and whether it is called
//     `Harbor.Hosting`. That was a decision, not a derivation, and this file is
//     about what the leaf DOES rather than where the answer went — a different
//     host would satisfy it too, or fail it, on the merits.
//   * The other baselined Presentation rows: #535 recent items, #538 jump
//     palette. Named so the next reader does not read this file as a
//     repository-wide I/O audit.
//   * `contrib/` — unmaintained, compiled by no CI job. Worth stating plainly,
//     though: `contrib/apps/Harbor.App.{Wpf,Blazor,Maui}` construct both stores
//     and reference only this leaf, so they do not see the moved types. They were
//     already outside CI's reach before this change and are tracked in #536's
//     sibling follow-up rather than fixed here.
//
// DOES NOT RULE THE #188 MATRIX EXCEPTION
// ---------------------------------------
// #534 also predicts that emptying this leaf "retires the #188 matrix exception"
// (its `Harbor.Desktop.Abstractions -> Harbor.Application` edge). It does not, and
// this guard must not pretend otherwise: that edge exists because the leaf's
// `ProviderModelPickerViewModel` / `OnboardingViewModel` read the
// `ProviderPresets` catalog out of `Harbor.Application`, which has nothing to do
// with config persistence. Retiring it is a separate move, in a separate issue.

// MECHANISM (#1086, step 2, conveyor)
// -----------------------------------
// The disk-call ban below is a ScanRule: one shape, no baseline, four planted
// controls, a discovery floor. Enumeration, matching and the control/discovery
// verdicts are ScanRunner's; this file keeps the issue prose and the test names.
//
// Two deliberate carries, not re-decisions:
//   * The verdict runs through `SourceCommentStripper.StripAll` inside
//     `ParseDiskCall` — never the shared stripper (see ScanRule.cs: divergent
//     contracts) — and reports a missing subject file the way the old probe
//     did only on the enumerate-then-read race.
//   * The declaration half (`The_File_Backed_Stores_Are_Not_Declared_Here` and
//     its control) is NOT a forbidden-shape scan — it asserts an ABSENCE (the
//     stores) and a PRESENCE (the ports) — so it stays handwritten with its
//     helpers (`DeclaredTypesInLeaf`, `FindInRepoFile`, `MatchAll`).

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     #534: <c>Harbor.Desktop.Abstractions</c> is a published Presentation
///     config leaf — the matrix has said Presentation since the row was created
///     — and it was doing persistence, which
///     <c>PresentationCapabilityRules</c> forbids every Presentation assembly
///     anyway. See the file header for the direction this assumes, why the
///     argument is a plain Presentation rule rather than a special case, why it
///     is a source scan rather than a <c>ResolvedViolations</c> row, and what is
///     deliberately left alone.
/// </summary>
public sealed class DesktopAbstractionsLeafTakesNoIoRules
{
    /// <summary>The project directory of the leaf, as <c>src/&lt;dir&gt;</c>.</summary>
    private const string LeafProjectDir = "Harbor.Desktop.Abstractions";

    /// <summary>
    ///     A filesystem call: <c>File.X</c> / <c>Directory.X</c> static members, or
    ///     a constructed <c>FileInfo</c> / <c>DirectoryInfo</c>. Deliberately
    ///     narrow — see the <c>System.IO.Path</c> note in the file header.
    /// </summary>
    private static readonly Regex DiskCall = new(
        @"(?:\b(?:File|Directory)\s*\.\s*[A-Za-z_])|\bnew\s+(?:FileInfo|DirectoryInfo)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string DiskSubId = "CONFIG-LEAF-DISK-IO";

    /// <summary>The disk-call ban as data: one shape, no baseline, four controls, a floor.</summary>
    private static readonly ScanRule DiskRule = new()
    {
        Id = "DesktopAbstractionsLeafTakesNoIo.Disk",
        Trees = ["src/" + LeafProjectDir],
        Forbidden =
        [
            new ScanForbidden(
                DiskSubId,
                DiskCall,
                "the config STORES are persistence and belong to an outer layer "
                + "(Harbor.Hosting/Configuration); the config SCHEMA, the PORTS and the DTOs are "
                + "the leaf's. See issue #534."),
        ],
        Controls =
        [
            // The watcher line the old liveness test pointed at: Infrastructure,
            // watches real plugin directories, legitimately calls Directory.Exists.
            new ScanControl("Real/Watcher.cs", "            if (!Directory.Exists(dir))", DiskSubId),
            new ScanControl("Real/Store.cs", "        if (!File.Exists(sessionFile))", DiskSubId),
            // The fix's own comments name the forbidden call. A guard that fails on
            // its own documentation is a guard nobody keeps.
            new ScanControl("Prose.cs", "// #534: JsonCommonConfigStore called File.WriteAllText here.", null),
            // Path is string manipulation, not a syscall — deliberately not ruled.
            new ScanControl("Strings.cs", "            string full = Path.Combine(home, name);", null),
        ],
        MinHits = 10,
        CustomParse = ParseDiskCall,
    };

    /// <summary>
    ///     The custom parser: the old probe's line loop verbatim — the leaf's own
    ///     masking-lexer stripper, one report per matching line.
    /// </summary>
    private static IEnumerable<ScanHit> ParseDiskCall(string displayPath, string rawSource)
    {
        string[] lines = SourceCommentStripper.StripAll(
            rawSource.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        for (int i = 0; i < lines.Length; i++)
        {
            if (DiskCall.IsMatch(lines[i]))
            {
                yield return new ScanHit(DiskSubId, displayPath, i + 1, lines[i].Trim());
            }
        }
    }

    /// <summary>
    ///     A type DECLARATION in the leaf: the <c>class</c> / <c>interface</c> /
    ///     <c>record</c> / <c>struct</c> keyword followed by the name. Used to ask
    ///     "is this type still DECLARED here" as a source fact, which is the only
    ///     question a moved type can still answer.
    /// </summary>
    /// <remarks>
    ///     Deliberately anchored on the keyword and nothing after the name. A
    ///     stricter shape that insists on reaching <c>:</c> or <c>{</c> looks more
    ///     precise and is not: <c>public interface IAppConfigStore&lt;T&gt; where T :
    ///     AppConfigBase</c> puts a generic constraint between the name and the
    ///     brace, so such a pattern fails to match the ONE declaration this file
    ///     most needs to find — and the failure is silent, because the rule that
    ///     uses it is a negative assertion. An imperfect positive control would
    ///     have caught that, which is the argument for having one.
    /// </remarks>
    private static readonly Regex TypeDeclaration = new(
        @"\b(?:class|interface|record|struct)\s+(?:partial\s+)*(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     The two ports #534 keeps in the leaf. They must survive the move: the
    ///     defect was persistence in the leaf, not the presence of a contract.
    /// </summary>
    private static readonly string[] PortsThatStay =
    [
        "ICommonConfigStore",
        "IAppConfigStore",
    ];

    /// <summary>
    ///     The two file-backed stores that must no longer be DECLARED here. Named
    ///     as data so the assertion cannot drift into "the leaf is empty".
    /// </summary>
    private static readonly string[] FileBackedStoresThatMoved =
    [
        "JsonCommonConfigStore",
        "JsonAppConfigStore",
    ];

    [Test]
    public async Task The_Config_Leaf_Touches_No_Disk()
    {
        List<string> violations = ScanRunner.Evaluate(DiskRule);

        await Assert.That(violations).IsEmpty().Because(
            "Harbor.Desktop.Abstractions: the layer matrix calls it Presentation, and has since "
            + "the row was created (5d2df19f). PresentationCapabilityRules already forbids "
            + "System.IO.File* / System.IO.Directory* in every Presentation assembly, with an "
            + "EMPTY baseline for this one since #534. So this is not a special case: it is a "
            + "plain instance of a Presentation rule, restated as a source fact because IL cannot "
            + "see a file in the project directory that the compile does not pick up. The project "
            + "is also a published package (IsPackable, PackageId Harbor.Desktop.Abstractions), "
            + "which makes a stray File.* a mistake a NuGet consumer would install. Either way "
            + "the config STORES are persistence and belong to an outer layer "
            + "(Harbor.Hosting/Configuration); the config SCHEMA, the PORTS "
            + "(ICommonConfigStore, IAppConfigStore<T>) and the DTOs are the leaf's. Note the "
            + "matrix forbids the other direction too: an Infrastructure row may never "
            + "reference Presentation. Found: "
            + (violations.Count == 0 ? "(none)" : string.Join("\n", violations)));
    }

    [Test]
    public async Task The_File_Backed_Stores_Are_Not_Declared_Here()
    {
        IReadOnlyList<string> files = RepoPaths.EnumerateCsFiles(LeafProjectDir);

        await Assert.That(files.Count).IsGreaterThan(0).Because(
            "Shared precondition with The_Config_Leaf_Touches_No_Disk, and for the same reason: "
            + "an empty enumeration would make this rule pass without reading anything. Found "
            + files.Count + " .cs files under src/" + LeafProjectDir + ".");

        IReadOnlyList<string> declaredHere = DeclaredTypesInLeaf();

        foreach (string store in FileBackedStoresThatMoved)
        {
            await Assert.That(declaredHere.Contains(store)).IsFalse().Because(
                store + " is persistence: it stats the file, creates ~/.harbor, and writes "
                + "atomically through a sibling .tmp. #534 moved it to Harbor.Hosting so this "
                + "leaf could stop doing it. If a declaration reappears here the move was undone "
                + "— and if it reappears WITH its File.* calls, The_Config_Leaf_Touches_No_Disk "
                + "is what will say so. Declared here: " + Describe(declaredHere));
        }

        foreach (string port in PortsThatStay)
        {
            await Assert.That(declaredHere.Contains(port)).IsTrue().Because(
                port + " is the half of #534 that must NOT move. The contract is the config "
                + "schema's; only the bytes leave. Deleting the port would satisfy this file by "
                + "leaving consumers with nothing to implement, which is the opposite of the "
                + "fix — the composition root resolves ICommonConfigStore and "
                + "IAppConfigStore<T> out of DI, and ICommonConfigReader (Harbor.Ui.Framework."
                + "Abstractions) is bridged onto ICommonConfigStore by an adapter that would "
                + "have nothing left to bridge. Declared here: " + Describe(declaredHere));
        }
    }

    /// <summary>
    ///     Non-vacuity for <see cref="The_Config_Leaf_Touches_No_Disk" />. The same
    ///     scanner must still find a real call in a file that legitimately has one,
    ///     or "no disk calls in the leaf" is indistinguishable from "the regex reads
    ///     nothing".
    /// </summary>
    [Test]
    public async Task Disk_Scanner_Still_Sees_A_Real_Call()
    {
        // The Real/* controls carry the Infrastructure lines the old liveness test
        // read from the tree; they drive the REAL parser rather than a second
        // implementation of it. If this control ever goes red, the fix is the
        // REGEX, not the watcher.
        List<string> failures = ScanRunner.CheckControls(DiskRule);

        await Assert.That(failures).IsEmpty().Because(
            "The parser behind The_Config_Leaf_Touches_No_Disk must be able to fail. "
            + "DebouncedPluginWatcher is Infrastructure, watches real plugin directories, and "
            + "legitimately calls Directory.Exists / File.Exists — if the parser reports "
            + "nothing even there, it is reporting nothing everywhere and the leaf's pass is "
            + "meaningless. "
            + string.Join("; ", failures));
    }

    /// <summary>
    ///     Non-vacuity for the DECLARATION half of
    ///     <see cref="The_File_Backed_Stores_Are_Not_Declared_Here" />, in the same
    ///     shape and for the same reason: a scanner that can only ever find nothing
    ///     would make that rule green for the wrong reason.
    /// </summary>
    [Test]
    public async Task Declaration_Scanner_Still_Sees_A_Real_Type()
    {
        // IAppConfigStore<T> — a generic port the leaf still declares, so the
        // declaration shape must match it name-including-the-type-parameter.
        IReadOnlyList<string> hits = FindInRepoFile(TypeDeclaration, LeafFile("IAppConfigStore.cs"));

        await Assert.That(hits.Count).IsGreaterThan(0).Because(
            "The scanner behind The_File_Backed_Stores_Are_Not_Declared_Here must be able to "
            + "fail. IAppConfigStore is still declared in the leaf and is the one type the "
            + "rule requires to be THERE — if the scanner misses it, the 'the ports stayed' "
            + "assertion is passing because the probe reads nothing. If this control ever goes "
            + "red, the fix is the REGEX, not the port.");
    }

    /// <summary>
    ///     The walk really reaches the leaf. Outside a checkout — or if the project
    ///     directory were renamed — the banning rule would pass because it reads nothing.
    /// </summary>
    [Test]
    public async Task Scanner_SeesTheLeaf()
    {
        List<string> discovery = ScanRunner.CheckDiscovery(DiskRule);

        await Assert.That(discovery).IsEmpty()
            .Because(
                "Every banning rule in this file scans the leaf through the shared walk. "
                + "A near-zero count means the path is stale and the rule enforces nothing. "
                + string.Join("; ", discovery));
    }

    /// <summary>
    ///     Every baseline row states why it is tolerated, in the row itself. Vacuous
    ///     while the table is empty, and deliberately so: wired from the first row
    ///     so the first row cannot skip the argument.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_AllHaveReasons()
    {
        List<string> failures = ScanRunner.CheckReasons(DiskRule);

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every baseline row must still correspond to a real hit, so the table
    ///     cannot rot into a blanket permission.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_Are_Not_Stale()
    {
        List<string> stale = ScanRunner.StaleBaselineKeys(
            DiskRule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(DiskRule)));

        await Assert.That(stale).IsEmpty()
            .Because("a baseline row with no violation behind it is a permission for a "
                + "problem that no longer exists: " + string.Join(", ", stale));
    }

    // ---------------------------------------------------------------------
    // Probes.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     Every type declaration name in the leaf, one entry per declaration, from
    ///     comment-stripped source. Declarations only — a mere mention of a type
    ///     name in a signature is not a declaration, which is what keeps a
    ///     documenting reference to a moved type from reading as the type being here.
    /// </summary>
    private static IReadOnlyList<string> DeclaredTypesInLeaf()
    {
        var names = new List<string>();
        foreach (string file in RepoPaths.EnumerateCsFiles(LeafProjectDir))
        {
            if (!File.Exists(file))
            {
                continue;
            }

            foreach (string line in SourceCommentStripper.StripAll(File.ReadLines(file)))
            {
                Match match = TypeDeclaration.Match(line);
                if (match.Success)
                {
                    names.Add(match.Groups["name"].Value);
                }
            }
        }

        return names;
    }

    /// <summary>
    ///     The control probe, for a single repo-relative file. A missing control file
    ///     yields no hits, so the control fails — which is the point of it.
    /// </summary>
    private static IReadOnlyList<string> FindInRepoFile(Regex pattern, string relative)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string file = Path.Combine(root, relative);
        if (!File.Exists(file))
        {
            return [];
        }

        return MatchAll(root, pattern, file);
    }

    private static List<string> MatchAll(string root, Regex pattern, string file)
    {
        var hits = new List<string>();
        string[] lines = SourceCommentStripper.StripAll(File.ReadLines(file));
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = pattern.Match(lines[i]);
            if (match.Success)
            {
                hits.Add($"{Relative(root, file)}:{i + 1}  {match.Value.Trim()}");
            }
        }

        return hits;
    }

    private static string LeafFile(string name)
        => $"src/{LeafProjectDir}/Configuration/{name}";

    /// <summary>
    ///     The declared-type list, rendered for a failure message. A negative
    ///     assertion that fails with no context ("expected false, got true") makes
    ///     the reader go and re-derive the scanner, so it carries what it saw.
    /// </summary>
    private static string Describe(IReadOnlyList<string> declared)
        => declared.Count == 0 ? "(nothing)" : string.Join(", ", declared);

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');
}
