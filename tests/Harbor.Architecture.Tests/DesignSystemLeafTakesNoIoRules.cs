// DesignSystemLeafTakesNoIoRules.cs — the guard for #536.
//
// WHY THIS FILE EXISTS
// --------------------
// #536 was filed as a capability-rules item: `ThemeStore` and
// `ThemeDirectoryWatcher` sit in `Harbor.DesignSystem` and call `File.*` /
// `Directory.*`, so four rows in `PresentationCapabilityRules.KnownViolations`
// kept the rule green. The rows are the SYMPTOM, and a baseline row is a
// permission rather than a fix — #720 said so in its own header when it deleted
// the two CellForge rows for the same reason.
//
// The defect underneath the rows is that those two types were never token-catalog
// work. `Harbor.DesignSystem` is the HDS v1 package: `IsPackable`,
// `PackageId: Harbor.DesignSystem`, an EMPTY allowed-reference set, no
// PackageReference at all. It is the one assembly in this repository a consumer
// can depend on without pulling Harbor in — which is exactly why it must not
// resolve `~/.harbor/themes` from the process environment, enumerate that
// directory, and stat it on a timer. A design-system package that reads the
// user's home directory is not a design system with a storage feature; it is a
// storage feature that also ships tokens.
//
// #668 removed the duplicate read on the CellForge side and #622 made the axis
// reachable from a product. What is left is this assembly's own half, and
// #536's "done when" — the four rows deleted — is reachable only by taking the
// I/O OUT of the leaf. Re-baselining them would leave a green build and the
// defect, which is the thing this file exists to make impossible.
//
// WHICH DIRECTION THIS ASSUMES, AND WHY THE OTHER ONE IS NOT AVAILABLE
// --------------------------------------------------------------------
// There are two ways to reconcile a leaf that does I/O: give it a seam to call
// (pull infrastructure UP into the leaf) or take the I/O out (push it DOWN into
// an outer layer). The repository's own matrix settles it in code rather than
// in prose:
//
//     FullLayerMatrixTests.Matrix["Harbor.DesignSystem"] = new(Layer.Presentation, [])
//
// An empty allowed-reference set, so there is nowhere for the leaf to call even
// if a port were declared for it — the same reason `IThemeStore` lives HERE
// (#668 needed an assembly all four participants could name). The port therefore
// stays in DesignSystem: the contract belongs to the catalog, and where the
// bytes come from belongs to an outer layer.
//
// WHAT IS RULED
// -------------
//   1. No disk call anywhere in `src/Harbor.DesignSystem`. The capability rules
//      already assert this — as four baselined rows, which is a permission.
//      This states it as the invariant, so the code cannot come back behind a
//      re-granted waiver.
//   2. No `Environment.GetEnvironmentVariable` / `Environment.GetFolderPath`
//      either. The capability rules do NOT cover these — they are not
//      `File.*`/`Directory.*` — and #536 names them as part of the same defect
//      ("also reads HARBOR_THEMES_DIR and Environment.GetFolderPath(UserProfile)").
//      Leaving them behind would have left the leaf still reaching into the
//      user's home directory behind a green build: the half-defect that makes
//      a fix look like a fix.
//   3. The leaf enumeration is non-empty, so neither rule can pass because the
//      probe read nothing.
//   4. Non-vacuity for both scanners, in the shape the two sibling theme guards
//      already use: each must still find its target in a file that legitimately
//      has it, so a green rule is distinguishable from a broken regex.
//
// WHAT IS DELIBERATELY NOT RULED, AND WHY
// ----------------------------------------
//   * `System.IO.Path` — pure string manipulation (Combine / GetFileName), no
//     syscall, and a token catalog legitimately composes names. Same reasoning
//     as `PresentationCapabilityRules`' own "DELIBERATELY NOT RULED" note and
//     `ThemeStoreSeamRules`'s; do not "helpfully" add it here.
//   * `Environment.CurrentDirectory` / `NewLine` / `Exit`. Not user-profile
//     reach, and naming two members is the point: a rule grown to "no
//     Environment at all" would be a different and broader claim than the one
//     #536 makes.
//   * The other baselined Presentation rows (#534 config stores, #538 jump
//     palette) and the desktop `ThemeService.LoadJson`, which the layer matrix
//     leaves unrestricted because an app composition root is not a Presentation
//     assembly. Named so the next reader does not read this file as a
//     repository-wide I/O audit. (#535's recent items were in this list until
//     that type was deleted; `DesktopSharedTakesNoIoRules` now rules that
//     project instead.)
//   * WHICH outer assembly holds the implementation. That was a decision, not a
//     derivation, and this file is deliberately about what the leaf DOES rather
//     than where the answer went — a different host would satisfy it too, or
//     fail it, on the merits.
//   * `contrib/` — unmaintained, compiled by no CI job.
//
// MECHANISM (#1086, step 2, conveyor)
// -----------------------------------
// The two bans below are ScanRules sharing the leaf scope: the disk-call ban
// and the environment-lookup ban, both fully armed (no baseline). Enumeration,
// matching and the control/discovery verdicts are ScanRunner's; this file keeps
// the issue prose and the test names.
//
// Two deliberate carries, not re-decisions:
//   * The matcher input is `SourceCommentStripper.StripAll`, NOT the shared
//     `SourceScan.StripComments` — the old probe graded through the former, and
//     the two strippers have divergent contracts (see ScanRule.cs: the shared
//     one preserves string literals where the masking lexer blanks them).
//     Folding silently would change what the matcher sees. So both rules grade
//     through `ParseLeafIo`, which runs the old line loop verbatim.
//   * The old probe reported an unreadable file as a hit ("the probe cannot
//     grade a file it cannot read"). The rule reads what the walk yields, so
//     that arm fired only on the enumerate-then-read race; on a stable tree the
//     verdict is identical, and a scope that lost the leaf fails the discovery
//     floor instead.
// The planted controls are synthetic lines in the REAL shapes (the watcher and
// store lines the old liveness tests pointed at), so a broken regex goes red
// here exactly as it did there.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     #536: <c>Harbor.DesignSystem</c> is the HDS v1 token leaf, and a token
///     leaf that reaches the filesystem or the user's home directory is not a
///     token leaf. See the file header for the direction this assumes and for
///     what is deliberately left alone.
/// </summary>
public sealed class DesignSystemLeafTakesNoIoRules
{
    private const string DiskSubId = "TOKEN-LEAF-DISK-IO";
    private const string EnvSubId = "TOKEN-LEAF-USER-LOCATION";

    /// <summary>The project directory of the leaf, as <c>src/&lt;dir&gt;</c>.</summary>
    private const string LeafProjectDir = "Harbor.DesignSystem";

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
        Id = "DesignSystemLeafTakesNoIo.Disk",
        Trees = ["src/" + LeafProjectDir],
        Forbidden =
        [
            new ScanForbidden(
                DiskSubId,
                DiskCall,
                "persistence is an outer layer's job — the leaf keeps the PORT (IThemeStore, #668) "
                + "and the tokens. See issue #536."),
        ],
        Controls =
        [
            // The watcher line the old liveness test pointed at: Infrastructure,
            // watches real plugin directories, legitimately calls Directory.Exists.
            new ScanControl("Real/Watcher.cs", "            if (!Directory.Exists(dir))", DiskSubId),
            // The session-store line: Infrastructure, the ISessionStore the whole
            // repository persists through.
            new ScanControl("Real/Store.cs", "        if (!File.Exists(sessionFile))", DiskSubId),
            // The fix's own comments name the forbidden call. A guard that fails on
            // its own documentation is a guard nobody keeps.
            new ScanControl("Prose.cs", "// #536: ThemeStore used File.ReadAllText here before #742.", null),
            // Path is string manipulation, not a syscall — deliberately not ruled.
            new ScanControl("Strings.cs", "                        TabName = Path.GetFileName(path);", null),
        ],
        MinHits = 5,
        CustomParse = ParseDiskCall,
    };

    /// <summary>The user-location ban as data: one shape, no baseline, four controls, a floor.</summary>
    private static readonly ScanRule EnvRule = new()
    {
        Id = "DesignSystemLeafTakesNoIo.Env",
        Trees = ["src/" + LeafProjectDir],
        Forbidden =
        [
            new ScanForbidden(
                EnvSubId,
                EnvironmentLookup,
                "reading HARBOR_THEMES_DIR or the user profile is how the leaf reaches into the "
                + "user's home directory. A theme catalog that can be told where to look has a "
                + "configuration surface. See issue #536."),
        ],
        Controls =
        [
            // The composition-root lines the old liveness test pointed at: the
            // ThemeStore legitimately resolves the user's Harbor directory.
            new ScanControl(
                "Real/ThemeStoreEnv.cs",
                "            string? env = Environment.GetEnvironmentVariable(\"HARBOR_THEMES_DIR\");",
                EnvSubId),
            new ScanControl(
                "Real/ThemeStorePath.cs",
                "                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),",
                EnvSubId),
            // #536 names both members in prose; documenting them must not trip the rule.
            new ScanControl(
                "Prose.cs",
                "// #536: also reads HARBOR_THEMES_DIR and Environment.GetFolderPath(UserProfile).",
                null),
            // NewLine is not user-profile reach — deliberately not ruled.
            new ScanControl("Strings.cs", "            Lines = text.Split(Environment.NewLine);", null),
        ],
        MinHits = 5,
        CustomParse = ParseEnvLookup,
    };

    /// <summary>
    ///     The custom parsers: the old probe's line loop verbatim — the leaf's own
    ///     masking-lexer stripper, one report per matching line. The shared line
    ///     scan would grade different input, so the rules carry their stripper.
    /// </summary>
    private static IEnumerable<ScanHit> ParseDiskCall(string displayPath, string rawSource) =>
        ParseLeafIo(displayPath, rawSource, DiskCall, DiskSubId);

    /// <inheritdoc cref="ParseDiskCall" />
    private static IEnumerable<ScanHit> ParseEnvLookup(string displayPath, string rawSource) =>
        ParseLeafIo(displayPath, rawSource, EnvironmentLookup, EnvSubId);

    private static IEnumerable<ScanHit> ParseLeafIo(
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
    public async Task The_Token_Leaf_Touches_No_Disk()
    {
        List<string> violations = ScanRunner.Evaluate(DiskRule);

        await Assert.That(violations).IsEmpty()
            .Because(
                "Harbor.DesignSystem is the HDS v1 package: IsPackable, PackageId "
                + "Harbor.DesignSystem, an EMPTY allowed-reference set and no PackageReference. "
                + "It is the one assembly a consumer can take without pulling Harbor in, which is "
                + "exactly why it must not enumerate a directory or stat a file. The theme store "
                + "and the theme directory watcher are persistence, and persistence is an outer "
                + "layer's job — the leaf keeps the PORT (IThemeStore, #668) and the tokens. "
                + "Found: " + (violations.Count == 0 ? "(none)" : string.Join("\n", violations)));
    }

    [Test]
    public async Task The_Token_Leaf_Resolves_No_User_Location()
    {
        List<string> violations = ScanRunner.Evaluate(EnvRule);

        await Assert.That(violations).IsEmpty()
            .Because(
                "The capability rules do NOT cover these two members — they are not File.* or "
                + "Directory.* — so nothing else in the build would fail if they stayed. But "
                + "#536 names them as part of the same defect: reading HARBOR_THEMES_DIR and "
                + "Environment.GetFolderPath(UserProfile) is how the leaf reaches into the user's "
                + "home directory, and the complaint was never 'this file opens a handle', it was "
                + "'the zero-dependency package knows where my home is'. A theme catalog that can "
                + "be told where to look is a theme catalog with a configuration surface. Found: "
                + (violations.Count == 0 ? "(none)" : string.Join("\n", violations)));
    }

    /// <summary>
    ///     The walk really reaches the leaf. Outside a checkout — or if the project
    ///     directory were renamed — both rules would pass because they read nothing.
    /// </summary>
    [Test]
    public async Task Scanner_SeesTheLeaf()
    {
        List<string> diskDiscovery = ScanRunner.CheckDiscovery(DiskRule);
        List<string> envDiscovery = ScanRunner.CheckDiscovery(EnvRule);

        await Assert.That(diskDiscovery).IsEmpty()
            .Because(
                "Every rule in this file scans the leaf through the shared walk. "
                + "A near-zero count means the path is stale and the rules enforce nothing. "
                + string.Join("; ", diskDiscovery));
        await Assert.That(envDiscovery).IsEmpty()
            .Because(
                "Shared precondition with The_Token_Leaf_Touches_No_Disk, and for the same reason: "
                + "an empty enumeration would make the second rule pass without reading anything. "
                + string.Join("; ", envDiscovery));
    }

    /// <summary>
    ///     Non-vacuity for <see cref="The_Token_Leaf_Touches_No_Disk" />. The same
    ///     parser must still find a real call in the watcher/store shape, or "no
    ///     violations in the leaf" is indistinguishable from "the regex reads
    ///     nothing".
    /// </summary>
    [Test]
    public async Task Disk_Scanner_Still_Sees_A_Real_Call()
    {
        // The Real/* controls carry the Infrastructure lines the old liveness test
        // read from the tree; they drive the REAL parser rather than a second
        // implementation of it. If the regex stops matching, this goes red — and
        // the fix is the REGEX, not the watcher.
        List<string> failures = ScanRunner.CheckControls(DiskRule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "The parser behind The_Token_Leaf_Touches_No_Disk must be able to fail. "
                + string.Join("; ", failures));
    }

    /// <summary>
    ///     Non-vacuity for <see cref="The_Token_Leaf_Resolves_No_User_Location" />, in
    ///     the same shape and for the same reason.
    /// </summary>
    [Test]
    public async Task Environment_Scanner_Still_Sees_A_Real_Lookup()
    {
        List<string> failures = ScanRunner.CheckControls(EnvRule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "The parser behind The_Token_Leaf_Resolves_No_User_Location must be able to "
                + "fail. "
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
