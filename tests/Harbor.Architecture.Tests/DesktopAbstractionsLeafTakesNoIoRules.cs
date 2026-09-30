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
// The defect underneath the rows is that this leaf is the ONE project the layer
// matrix calls Domain (docs/ARCHITECTURE_LAYERS.md §2) while behaving like
// storage: `Harbor.Desktop.Abstractions` is `IsPackable`,
// `PackageId: Harbor.Desktop.Abstractions`, a published package, and it shipped a
// `File.WriteAllText → File.Delete → File.Move` sequence writing into the user's
// home directory. The config STORES are persistence; the config SCHEMA is not.
// #534's own "done when" — the four rows deleted, no `System.IO.File*` left in
// the project — is reachable only by taking the I/O out.
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
//      capability rules already assert this — as four baselined rows, which is a
//      permission. This states it as the invariant, so the code cannot come back
//      behind a re-granted waiver.
//   2. The two file-backed stores are not DECLARED in the leaf, while the two
//      ports they implement still are. Rule 1 alone would be satisfied by a store
//      that had been reduced to a wrapper around someone else's I/O; this states
//      the shape #534 actually wants — the contract is the leaf's, the bytes are
//      not — and the port half keeps the "move" from turning into "delete".
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

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     #534: <c>Harbor.Desktop.Abstractions</c> is a published, Domain-labelled
///     config leaf, and it was doing persistence. See the file header for the
///     direction this assumes, why it is a source scan rather than a
///     <c>ResolvedViolations</c> row, and what is deliberately left alone.
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
    ///     A file that legitimately keeps its disk access forever: the plugin
    ///     watcher is Infrastructure, watches real directories, and is not moving.
    ///     The right control for <see cref="DiskCall" />.
    /// </summary>
    private const string DiskControlFile = "src/Harbor.Plugins.Hosting/DebouncedPluginWatcher.cs";

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
        IReadOnlyList<string> files = RepoPaths.EnumerateCsFiles(LeafProjectDir);
        IReadOnlyList<string> hits = FindInLeaf(DiskCall);

        await Assert.That(files.Count).IsGreaterThan(0).Because(
            "Every rule in this file scans the leaf through RepoPaths.EnumerateCsFiles. "
            + "Outside a checkout — or if the project directory were renamed — that returns "
            + "nothing and both rules would pass because they read nothing. A rule that cannot "
            + "see its subject is not a rule about its subject. Found " + files.Count
            + " .cs files under src/" + LeafProjectDir + ".");

        await Assert.That(hits.Count).IsEqualTo(0).Because(
            "Harbor.Desktop.Abstractions is a published package: IsPackable, PackageId "
            + "Harbor.Desktop.Abstractions. It is the only project the layer matrix labels "
            + "Domain (§2), and a Domain-labelled package that writes into the user's home "
            + "directory is not a domain model — it is a storage engine that also ships a "
            + "schema. The config STORES are persistence and belong to an outer layer "
            + "(Harbor.Hosting/Configuration); the config SCHEMA, the PORTS "
            + "(ICommonConfigStore, IAppConfigStore<T>) and the DTOs are the leaf's. Note the "
            + "matrix forbids the other direction too: an Infrastructure row may never "
            + "reference Presentation. Found: "
            + (hits.Count == 0 ? "(none)" : string.Join("\n", hits)));
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
        IReadOnlyList<string> hits = FindInRepoFile(DiskCall, DiskControlFile);

        await Assert.That(hits.Count).IsGreaterThan(0).Because(
            "The scanner behind The_Config_Leaf_Touches_No_Disk must be able to fail. "
            + "DebouncedPluginWatcher is Infrastructure, watches real plugin directories, and "
            + "legitimately calls Directory.Exists / File.Exists — if the scanner reports "
            + "nothing even there, it is reporting nothing everywhere and the leaf's pass is "
            + "meaningless. If this control ever goes red, the fix is the REGEX, not the "
            + "watcher.");
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
    ///     Every match of <paramref name="pattern" /> in the leaf, as
    ///     <c>repo-relative-path:line  matched-text</c>. A file the probe cannot read
    ///     is reported as a hit: the subject of this rule must not be able to vanish
    ///     under it.
    /// </summary>
    private static IReadOnlyList<string> FindInLeaf(Regex pattern)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var hits = new List<string>();
        foreach (string file in RepoPaths.EnumerateCsFiles(LeafProjectDir))
        {
            if (!File.Exists(file))
            {
                hits.Add($"{Relative(root, file)}  (file is missing — the probe cannot grade a "
                        + "file it cannot read)");
                continue;
            }

            hits.AddRange(MatchAll(root, pattern, file));
        }

        return hits;
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
