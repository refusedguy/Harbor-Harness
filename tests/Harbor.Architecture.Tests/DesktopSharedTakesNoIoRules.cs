// DesktopSharedTakesNoIoRules.cs — the guard for #535.
//
// WHY THIS FILE EXISTS
// --------------------
// #535 was filed as a capability-rules item: `RecentItemsService` sits in
// `Harbor.Desktop.Shared` and calls `File.Exists` / `File.ReadAllText` /
// `File.WriteAllText` (`:89,:90,:117`) and `Directory.CreateDirectory` (`:110`),
// so two rows in `PresentationCapabilityRules.KnownViolations` kept the rule
// green. It also reads `Environment.GetFolderPath(UserProfile)` (`:45`), which
// the capability rules cannot see, because it is neither `File.*` nor
// `Directory.*`.
//
// The defect underneath the rows is that a service which persists "recently
// opened" state to `~/.harbor/recent.json` was never desktop-shell work.
// `Harbor.Desktop.Shared` is Presentation:
//
//     FullLayerMatrixTests.Matrix["Harbor.Desktop.Shared"] = new(Layer.Presentation, …)
//
// so `Presentation_MustNot_TouchTheFilesystem_Files` and its `_Directories`
// twin are the rules that were supposed to stop it, and two baseline rows is
// what actually stopped it. A baseline row is a permission rather than a fix —
// #720 said so in its own header, and #742 deleted four of them for exactly
// this reason rather than re-baselining.
//
// WHY THIS IS A PROJECT RULE AND NOT A RESOLVED-VIOLATION ROW
// ------------------------------------------------------------
// `PresentationCapabilityRules.ResolvedViolations` looks like the natural home
// for "this capability must not come back", and it is — for a type that still
// exists. `ResolvedRows_AreWellFormed` resolves every row's type name against
// the real assembly and fails on a miss, so a row naming a type that has been
// DELETED would redden the build. A whole-project rule has no such coupling: it
// names a directory, so it holds whether the offending type was moved out,
// deleted, or renamed, and it also covers the NEXT type, which a per-type row
// cannot. That is why #742 wrote `DesignSystemLeafTakesNoIoRules` the same way.
//
// WHICH DIRECTION THIS ASSUMES, AND WHY IT IS NOT #742's
// ------------------------------------------------------
// #742 fixed the same class of defect in `Harbor.DesignSystem` by MOVING the
// persistence out of the leaf into `src/Harbor.Hosting/Themes` and keeping the
// port (`IThemeStore`) behind. The shape is right for a type with callers, and
// it is deliberately NOT what happens here, for one reason that is a fact about
// this repository rather than a preference:
//
//     `RecentItemsService` is constructed NOWHERE.
//
// Not in `src/`, not in `apps/`, not in `tests/`. The only occurrence of the
// identifier outside its own file is the constructor's own declaration, a
// `<c>` reference in a `PromptHistory` doc comment, two architecture-baseline
// rows, and three docs. `docs/KILLER_FEATURES.md` §6.2 states it outright — the
// file "exists in `Harbor.Desktop.Shared/Services/` but is NOT used by the
// Avalonia command palette" — and `apps/Harbor.App.Avalonia`'s
// `CommandPaletteViewModel` has no MRU of its own to receive it.
//
// The pull-up direction #535 asks for is not forbidden by the matrix here —
// unlike the DesignSystem leaf, whose allowed-reference set is EMPTY, this
// project's is `{Harbor.Desktop.Abstractions, Harbor.Ui.Framework.Abstractions}`.
// An `IRecentItemsStore` port declared here would be legal. It would also have
// exactly zero callers, because there is nothing to wire: a seam with no
// consumer substitutes nothing, and it would leave the persisting code alive in
// a second place with a second set of rows to grandfather. So the persistence
// is deleted rather than relocated, and this file is what makes that deletion
// non-reversible.
//
// WHAT IS RULED
// -------------
//   1. No disk call anywhere in `src/Harbor.Desktop.Shared`. The capability
//      rules already assert this — as two baselined rows, which is a
//      permission. This states it as the invariant, so the code cannot come back
//      behind a re-granted waiver.
//   2. No `Environment.GetEnvironmentVariable` / `Environment.GetFolderPath`
//      either. The capability rules do NOT cover these, and #535 names the
//      second one as part of the same defect. Leaving it behind would leave the
//      project still reaching into the user's home directory behind a green
//      build: the half-defect that makes a fix look like a fix.
//   3. The enumeration is non-empty, so neither rule can pass because the probe
//      read nothing.
//   4. Non-vacuity for both scanners: each must still find its target in a file
//      that legitimately has it, so a green rule is distinguishable from a
//      broken regex.
//
// WHAT IS DELIBERATELY NOT RULED, AND WHY
// ----------------------------------------
//   * `System.IO.Path` — pure string manipulation, no syscall. Same reasoning
//     as `DesignSystemLeafTakesNoIoRules` and `PresentationCapabilityRules`'
//     own "DELIBERATELY NOT RULED" note; do not "helpfully" add it here.
//   * `Environment.CurrentDirectory` / `NewLine` / `Exit`. Not user-profile
//     reach, and naming two members is the point.
//   * The OTHER baselined Presentation rows: the #534 config stores in
//     `Harbor.Desktop.Abstractions`, the #538 jump palette, and the desktop
//     `ThemeService.LoadJson`, which the layer matrix leaves unrestricted
//     because an app composition root is not a Presentation assembly. Named so
//     the next reader does not read this file as a repository-wide I/O audit.
//   * `contrib/` — unmaintained, compiled by no CI job.
//
// ANTI-TYPO NOTE ON THE CONTROLS
// -------------------------------
// The old liveness tests read the control files by path, and a path that
// stopped existing made the control go RED, not green: the probe passed
// `missingFileIsAViolation: false`, so an unreadable control yielded zero hits
// and the control failed. The synthetic controls below preserve that direction
// differently — each carries a REAL line copied from the file it names, so a
// regex that stops matching still goes red — but they no longer pin the PATH:
// if the control file is renamed, no test here notices. That is the documented
// cost of the cut; the shapes, not the addresses, are what this rule owns.
//
// MECHANISM (#1086, step 2, conveyor)
// -----------------------------------
// The two bans below are ScanRules sharing the project scope: the disk-call ban
// and the environment-lookup ban, both fully armed (no baseline). Enumeration,
// matching and the control/discovery verdicts are ScanRunner's; this file keeps
// the issue prose and the test names. Same two carries as the sibling leaf
// (`DesignSystemLeafTakesNoIoRules`): the verdict runs through
// `SourceCommentStripper.StripAll` inside `ParseAuditedIo` — never the shared
// stripper — and the old missing-file arm is dropped as an
// enumerate-then-read race with an identical verdict on a stable tree.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     #535: <c>Harbor.Desktop.Shared</c> is Presentation, and a shared desktop
///     service that reaches the filesystem or the user's home directory is not
///     shared desktop chrome. See the file header for the direction this assumes
///     and for what is deliberately left alone.
/// </summary>
public sealed class DesktopSharedTakesNoIoRules
{
    private const string DiskSubId = "SHARED-DESKTOP-DISK-IO";
    private const string EnvSubId = "SHARED-DESKTOP-USER-LOCATION";

    /// <summary>The project directory under audit, as <c>src/&lt;dir&gt;</c>.</summary>
    private const string AuditedProjectDir = "Harbor.Desktop.Shared";

    /// <summary>
    ///     A filesystem call: <c>File.X</c> / <c>Directory.X</c> static members, or
    ///     a constructed <c>FileInfo</c> / <c>DirectoryInfo</c>. Deliberately
    ///     narrow — see the <c>System.IO.Path</c> note in the file header.
    /// </summary>
    private static readonly Regex DiskCall = new(
        @"(?:\b(?:File|Directory)\s*\.\s*[A-Za-z_])|\bnew\s+(?:FileInfo|DirectoryInfo)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     The two <c>Environment</c> members that resolve a LOCATION: the process
    ///     configuration variable and the user's profile directory. These are the
    ///     two the capability rules cannot see, because they are not
    ///     <c>File.*</c>/<c>Directory.*</c>.
    /// </summary>
    private static readonly Regex EnvironmentLookup = new(
        @"\bEnvironment\s*\.\s*Get(?:EnvironmentVariable|FolderPath)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The disk-call ban as data: one shape, no baseline, four controls, a floor.</summary>
    private static readonly ScanRule DiskRule = new()
    {
        Id = "DesktopSharedTakesNoIo.Disk",
        Trees = ["src/" + AuditedProjectDir],
        Forbidden =
        [
            new ScanForbidden(
                DiskSubId,
                DiskCall,
                "persisting user state is Infrastructure behind a substitutable port; a shared "
                + "desktop service is the layer that consumes it. See issue #535."),
        ],
        Controls =
        [
            // The JsonlSessionStore line: Infrastructure, the ISessionStore the
            // whole repository persists sessions through — the code #535 says
            // persistence should look like.
            new ScanControl("Real/Store.cs", "        if (!File.Exists(sessionFile))", DiskSubId),
            new ScanControl(
                "Real/StoreAppend.cs",
                "            await File.AppendAllTextAsync(sessionFile, line).ConfigureAwait(false);",
                DiskSubId),
            // The fix's own comments name the forbidden call. A guard that fails on
            // its own documentation is a guard nobody keeps.
            new ScanControl("Prose.cs", "// #535: RecentItemsService called File.Exists here.", null),
            // Path is string manipulation, not a syscall — deliberately not ruled.
            new ScanControl("Strings.cs", "            string full = Path.Combine(dir, name);", null),
        ],
        MinHits = 3,
        CustomParse = ParseDiskCall,
    };

    /// <summary>The user-location ban as data: one shape, no baseline, four controls, a floor.</summary>
    private static readonly ScanRule EnvRule = new()
    {
        Id = "DesktopSharedTakesNoIo.Env",
        Trees = ["src/" + AuditedProjectDir],
        Forbidden =
        [
            new ScanForbidden(
                EnvSubId,
                EnvironmentLookup,
                "that call is how this project found ~/.harbor/recent.json in the first place. "
                + "See issue #535."),
        ],
        Controls =
        [
            // The ThemeStore lines: the composition-root destination #742 chose
            // for the theme persistence, legitimately resolving the directory.
            new ScanControl(
                "Real/ThemeStoreEnv.cs",
                "            string? env = Environment.GetEnvironmentVariable(\"HARBOR_THEMES_DIR\");",
                EnvSubId),
            new ScanControl(
                "Real/ThemeStorePath.cs",
                "            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),",
                EnvSubId),
            // #535 names GetFolderPath in prose; documenting it must not trip the rule.
            new ScanControl(
                "Prose.cs",
                "// #535: it also reads Environment.GetFolderPath(UserProfile) at :45.",
                null),
            // CurrentDirectory is not user-profile reach — deliberately not ruled.
            new ScanControl("Strings.cs", "            string cwd = Environment.CurrentDirectory;", null),
        ],
        MinHits = 3,
        CustomParse = ParseEnvLookup,
    };

    /// <summary>
    ///     The custom parsers: the old probe's line loop verbatim — the project's own
    ///     masking-lexer stripper, one report per matching line. The shared line
    ///     scan would grade different input, so the rules carry their stripper.
    /// </summary>
    private static IEnumerable<ScanHit> ParseDiskCall(string displayPath, string rawSource) =>
        ParseAuditedIo(displayPath, rawSource, DiskCall, DiskSubId);

    /// <inheritdoc cref="ParseDiskCall" />
    private static IEnumerable<ScanHit> ParseEnvLookup(string displayPath, string rawSource) =>
        ParseAuditedIo(displayPath, rawSource, EnvironmentLookup, EnvSubId);

    private static IEnumerable<ScanHit> ParseAuditedIo(
        string displayPath, string rawSource, Regex pattern, string subId)
    {
        string[] lines = SourceCommentStripper.StripAll(
            rawSource.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        for (int i = 0; i < lines.Length; i++)
        {
            if (pattern.IsMatch(lines[i]))
            {
                yield return new ScanHit(subId, displayPath, i + 1, lines[i].Trim());
            }
        }
    }

    [Test]
    public async Task The_Shared_Desktop_Layer_Touches_No_Disk()
    {
        List<string> violations = ScanRunner.Evaluate(DiskRule);

        await Assert.That(violations).IsEmpty()
            .Because(
                "Harbor.Desktop.Shared is Presentation (FullLayerMatrixTests), so the filesystem "
                + "capability rules apply to it, and the only reason they were green for this "
                + "project was two KnownViolations rows naming one type — a permission, not a "
                + "fix. Persisting user state is Infrastructure behind a substitutable port; a "
                + "shared desktop service is the layer that consumes it. Found: "
                + (violations.Count == 0 ? "(none)" : string.Join("\n", violations)));
    }

    [Test]
    public async Task The_Shared_Desktop_Layer_Resolves_No_User_Location()
    {
        List<string> violations = ScanRunner.Evaluate(EnvRule);

        await Assert.That(violations).IsEmpty()
            .Because(
                "The capability rules do NOT cover these two members — they are not File.* or "
                + "Directory.* — so nothing else in the build would fail if they stayed. But "
                + "#535 names Environment.GetFolderPath(UserProfile) as part of the same defect: "
                + "that call is how this project found ~/.harbor/recent.json in the first place, "
                + "and a package whose constructor learns where the user's home is has a "
                + "configuration surface nobody declared. Found: "
                + (violations.Count == 0 ? "(none)" : string.Join("\n", violations)));
    }

    /// <summary>
    ///     The walk really reaches the project. Outside a checkout — or if the
    ///     project directory were renamed — both rules would pass because they read nothing.
    /// </summary>
    [Test]
    public async Task Scanner_SeesTheProject()
    {
        List<string> diskDiscovery = ScanRunner.CheckDiscovery(DiskRule);
        List<string> envDiscovery = ScanRunner.CheckDiscovery(EnvRule);

        await Assert.That(diskDiscovery).IsEmpty()
            .Because(
                "Every rule in this file scans the project through the shared walk. "
                + "A near-zero count means the path is stale and the rules enforce nothing. "
                + string.Join("; ", diskDiscovery));
        await Assert.That(envDiscovery).IsEmpty()
            .Because(
                "Shared precondition with The_Shared_Desktop_Layer_Touches_No_Disk, and for the "
                + "same reason: an empty enumeration would make the second rule pass without "
                + "reading anything. "
                + string.Join("; ", envDiscovery));
    }

    /// <summary>
    ///     Non-vacuity for <see cref="The_Shared_Desktop_Layer_Touches_No_Disk" />. The
    ///     same parser must still find a real call in the store shape, or "no disk
    ///     calls in the project" is indistinguishable from "the regex reads nothing".
    /// </summary>
    [Test]
    public async Task Disk_Scanner_Still_Sees_A_Real_Call()
    {
        // The Real/* controls carry the Infrastructure lines the old liveness test
        // read from the tree; they drive the REAL parser rather than a second
        // implementation of it. If the regex stops matching, this goes red — and
        // the fix is the REGEX or the PATH, not the store.
        List<string> failures = ScanRunner.CheckControls(DiskRule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "The parser behind The_Shared_Desktop_Layer_Touches_No_Disk must be able to "
                + "fail. "
                + string.Join("; ", failures));
    }

    /// <summary>
    ///     Non-vacuity for <see cref="The_Shared_Desktop_Layer_Resolves_No_User_Location" />,
    ///     in the same shape and for the same reason.
    /// </summary>
    [Test]
    public async Task Environment_Scanner_Still_Sees_A_Real_Lookup()
    {
        List<string> failures = ScanRunner.CheckControls(EnvRule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "The parser behind The_Shared_Desktop_Layer_Resolves_No_User_Location must be "
                + "able to fail. "
                + string.Join("; ", failures));
    }

    /// <summary>
    ///     Every baseline row states why it is tolerated, in the row itself. Vacuous
    ///     while both tables are empty, and deliberately so: wired from the first
    ///     row so the first row cannot skip the argument.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_AllHaveReasons()
    {
        List<string> disk = ScanRunner.CheckReasons(DiskRule);
        List<string> env = ScanRunner.CheckReasons(EnvRule);

        await Assert.That(disk).IsEmpty().Because(string.Join("\n", disk));
        await Assert.That(env).IsEmpty().Because(string.Join("\n", env));
    }

    /// <summary>
    ///     Every baseline row must still correspond to a real hit, so the table
    ///     cannot rot into a blanket permission.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_Are_Not_Stale()
    {
        List<string> disk = ScanRunner.StaleBaselineKeys(
            DiskRule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(DiskRule)));
        List<string> env = ScanRunner.StaleBaselineKeys(
            EnvRule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(EnvRule)));

        await Assert.That(disk).IsEmpty()
            .Because("a baseline row with no violation behind it is a permission for a "
                + "problem that no longer exists: " + string.Join(", ", disk));
        await Assert.That(env).IsEmpty()
            .Because("a baseline row with no violation behind it is a permission for a "
                + "problem that no longer exists: " + string.Join(", ", env));
    }
}
