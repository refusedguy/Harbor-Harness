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

    /// <summary>
    ///     A file that legitimately keeps its disk access forever: the plugin
    ///     watcher is Infrastructure, watches real directories, and is not moving.
    ///     The right control for <see cref="DiskCall" />.
    /// </summary>
    private const string DiskControlFile = "src/Harbor.Plugins.Hosting/DebouncedPluginWatcher.cs";

    /// <summary>
    ///     A file that legitimately resolves the user's profile: the plugin host
    ///     reads the user's Harbor directory to find its own install. Infrastructure,
    ///     and not moving. The right control for <see cref="EnvironmentLookup" />.
    /// </summary>
    private const string EnvironmentControlFile = "src/Harbor.Plugins.Hosting/PluginHostOptions.cs";

    [Test]
    public async Task The_Token_Leaf_Touches_No_Disk()
    {
        IReadOnlyList<string> files = RepoPaths.EnumerateCsFiles(LeafProjectDir);
        IReadOnlyList<string> hits = FindIo(DiskCall, files);

        await Assert.That(files.Count).IsGreaterThan(0).Because(
            "Every rule in this file scans the leaf through RepoPaths.EnumerateCsFiles. "
            + "Outside a checkout — or if the project directory were renamed — that returns "
            + "nothing, and both rules would pass because they read nothing. A rule that "
            + "cannot see its subject is not a rule about its subject. Found "
            + files.Count + " .cs files under src/" + LeafProjectDir + ".");

        await Assert.That(hits.Count).IsEqualTo(0).Because(
            "Harbor.DesignSystem is the HDS v1 package: IsPackable, PackageId "
            + "Harbor.DesignSystem, an EMPTY allowed-reference set and no PackageReference. "
            + "It is the one assembly a consumer can take without pulling Harbor in, which is "
            + "exactly why it must not enumerate a directory or stat a file. The theme store "
            + "and the theme directory watcher are persistence, and persistence is an outer "
            + "layer's job — the leaf keeps the PORT (IThemeStore, #668) and the tokens. "
            + "Found: " + (hits.Count == 0 ? "(none)" : string.Join("\n", hits)));
    }

    [Test]
    public async Task The_Token_Leaf_Resolves_No_User_Location()
    {
        IReadOnlyList<string> files = RepoPaths.EnumerateCsFiles(LeafProjectDir);
        IReadOnlyList<string> hits = FindIo(EnvironmentLookup, files);

        await Assert.That(files.Count).IsGreaterThan(0).Because(
            "Shared precondition with The_Token_Leaf_Touches_No_Disk, and for the same reason: "
            + "an empty enumeration would make this rule pass without reading anything. Found "
            + files.Count + " .cs files under src/" + LeafProjectDir + ".");

        await Assert.That(hits.Count).IsEqualTo(0).Because(
            "The capability rules do NOT cover these two members — they are not File.* or "
            + "Directory.* — so nothing else in the build would fail if they stayed. But "
            + "#536 names them as part of the same defect: reading HARBOR_THEMES_DIR and "
            + "Environment.GetFolderPath(UserProfile) is how the leaf reaches into the user's "
            + "home directory, and the complaint was never 'this file opens a handle', it was "
            + "'the zero-dependency package knows where my home is'. A theme catalog that can "
            + "be told where to look is a theme catalog with a configuration surface. Found: "
            + (hits.Count == 0 ? "(none)" : string.Join("\n", hits)));
    }

    /// <summary>
    ///     Non-vacuity for <see cref="The_Token_Leaf_Touches_No_Disk" />. The same
    ///     scanner must still find a real call in a file that legitimately has one,
    ///     or "no violations in the leaf" is indistinguishable from "the regex reads
    ///     nothing".
    /// </summary>
    [Test]
    public async Task Disk_Scanner_Still_Sees_A_Real_Call()
    {
        IReadOnlyList<string> hits = FindIoInRepoFile(DiskCall, DiskControlFile);

        await Assert.That(hits.Count).IsGreaterThan(0).Because(
            "The scanner behind The_Token_Leaf_Touches_No_Disk must be able to fail. "
            + "DebouncedPluginWatcher is Infrastructure, watches real plugin directories, and "
            + "legitimately calls Directory.Exists / File.Exists — if the scanner reports "
            + "nothing even there, it is reporting nothing everywhere and the leaf's pass is "
            + "meaningless. If this control ever goes red, the fix is the REGEX, not the "
            + "watcher.");
    }

    /// <summary>
    ///     Non-vacuity for <see cref="The_Token_Leaf_Resolves_No_User_Location" />, in
    ///     the same shape and for the same reason.
    /// </summary>
    [Test]
    public async Task Environment_Scanner_Still_Sees_A_Real_Lookup()
    {
        IReadOnlyList<string> hits = FindIoInRepoFile(EnvironmentLookup, EnvironmentControlFile);

        await Assert.That(hits.Count).IsGreaterThan(0).Because(
            "The scanner behind The_Token_Leaf_Resolves_No_User_Location must be able to "
            + "fail. PluginHostOptions is Infrastructure and legitimately resolves "
            + "Environment.GetFolderPath(UserProfile) to find the user's Harbor directory — if "
            + "the scanner reports nothing even there, it is reporting nothing everywhere. If "
            + "this control ever goes red, the fix is the REGEX, not the plugin host.");
    }

    // ---------------------------------------------------------------------
    // Probes.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     Every match of <paramref name="pattern" /> in the named ABSOLUTE files, one
    ///     entry per site, as <c>repo-relative-path:line  matched-text</c>. Comments
    ///     are stripped first, so prose that NAMES <c>File.Exists</c> to explain a past
    ///     defect is not graded as code.
    /// </summary>
    /// <param name="pattern">The shape to look for.</param>
    /// <param name="files">Absolute paths to grade.</param>
    /// <param name="missingFileIsAViolation">
    ///     Whether an unreadable file is reported as a hit. TRUE for a rule, whose
    ///     subject must not vanish under it — <c>ThemeAxisStaysDataRules</c> grades
    ///     the same way, and a file the guard cannot read is a file it cannot rule
    ///     on. FALSE for a CONTROL, where a missing file would otherwise be
    ///     reported as a hit and turn the control green on the one condition that
    ///     should redden it: the control proving the scanner works must fail when
    ///     there is nothing to scan.
    /// </param>
    private static IReadOnlyList<string> FindIo(
        Regex pattern,
        IReadOnlyList<string> files,
        bool missingFileIsAViolation = true)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var hits = new List<string>();
        foreach (string file in files)
        {
            if (!File.Exists(file))
            {
                if (missingFileIsAViolation)
                {
                    hits.Add($"{Relative(root, file)}  (file is missing — the probe cannot "
                            + "grade a file it cannot read)");
                }

                continue;
            }

            string[] lines = SourceCommentStripper.StripAll(File.ReadLines(file));
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = pattern.Match(lines[i]);
                if (match.Success)
                {
                    hits.Add($"{Relative(root, file)}:{i + 1}  {match.Value.Trim()}");
                }
            }
        }

        return hits;
    }

    /// <summary>
    ///     The control probe, for a single repo-relative file. A missing control file
    ///     yields no hits, so the control fails — which is the point of it.
    /// </summary>
    private static IReadOnlyList<string> FindIoInRepoFile(Regex pattern, string relative)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        return FindIo(pattern, [Path.Combine(root, relative)], missingFileIsAViolation: false);
    }

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');
}
