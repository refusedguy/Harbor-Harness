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
// The control files are named by path, and a path that stops existing makes
// the control go RED, not green: `FindIoInRepoFile` passes
// `missingFileIsAViolation: false`, so an unreadable control yields zero hits
// and the control fails. That is the intended direction — a control proving the
// scanner works must fail when there is nothing to scan. If either ever goes
// red, the fix is the PATH or the REGEX, not the file it points at.

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

    /// <summary>
    ///     A file that legitimately keeps its disk access forever:
    ///     <c>JsonlSessionStore</c> is Infrastructure, is the <c>ISessionStore</c>
    ///     implementation the whole repository persists sessions through, and is
    ///     not moving. It is the right control for <see cref="DiskCall" />, and it
    ///     is deliberately a REAL persister rather than a convenient one — the
    ///     control should point at the code #535 says persistence should look like.
    /// </summary>
    private const string DiskControlFile = "src/Harbor.Storage.Jsonl/JsonlSessionStore.cs";

    /// <summary>
    ///     A file that legitimately resolves the user's profile: <c>ThemeStore</c>
    ///     in <c>Harbor.Hosting</c> is the composition-root destination #742
    ///     chose for the theme persistence for this same reason, and it reads both
    ///     <c>HARBOR_THEMES_DIR</c> and
    ///     <c>Environment.GetFolderPath(UserProfile)</c>. The right control for
    ///     <see cref="EnvironmentLookup" />.
    /// </summary>
    private const string EnvironmentControlFile = "src/Harbor.Hosting/Themes/ThemeStore.cs";

    [Test]
    public async Task The_Shared_Desktop_Layer_Touches_No_Disk()
    {
        IReadOnlyList<string> files = RepoPaths.EnumerateCsFiles(AuditedProjectDir);
        IReadOnlyList<string> hits = FindIo(DiskCall, files);

        await Assert.That(files.Count).IsGreaterThan(0).Because(
            "Every rule in this file scans the project through RepoPaths.EnumerateCsFiles. "
            + "Outside a checkout — or if the project directory were renamed — that returns "
            + "nothing, and both rules would pass because they read nothing. A rule that "
            + "cannot see its subject is not a rule about its subject. Found "
            + files.Count + " .cs files under src/" + AuditedProjectDir + ".");

        await Assert.That(hits.Count).IsEqualTo(0).Because(
            "Harbor.Desktop.Shared is Presentation (FullLayerMatrixTests), so the filesystem "
            + "capability rules apply to it, and the only reason they were green for this "
            + "project was two KnownViolations rows naming one type — a permission, not a "
            + "fix. Persisting user state is Infrastructure behind a substitutable port; a "
            + "shared desktop service is the layer that consumes it. Found: "
            + (hits.Count == 0 ? "(none)" : string.Join("\n", hits)));
    }

    [Test]
    public async Task The_Shared_Desktop_Layer_Resolves_No_User_Location()
    {
        IReadOnlyList<string> files = RepoPaths.EnumerateCsFiles(AuditedProjectDir);
        IReadOnlyList<string> hits = FindIo(EnvironmentLookup, files);

        await Assert.That(files.Count).IsGreaterThan(0).Because(
            "Shared precondition with The_Shared_Desktop_Layer_Touches_No_Disk, and for the "
            + "same reason: an empty enumeration would make this rule pass without reading "
            + "anything. Found " + files.Count + " .cs files under src/" + AuditedProjectDir + ".");

        await Assert.That(hits.Count).IsEqualTo(0).Because(
            "The capability rules do NOT cover these two members — they are not File.* or "
            + "Directory.* — so nothing else in the build would fail if they stayed. But "
            + "#535 names Environment.GetFolderPath(UserProfile) as part of the same defect: "
            + "that call is how this project found ~/.harbor/recent.json in the first place, "
            + "and a package whose constructor learns where the user's home is has a "
            + "configuration surface nobody declared. Found: "
            + (hits.Count == 0 ? "(none)" : string.Join("\n", hits)));
    }

    /// <summary>
    ///     Non-vacuity for <see cref="The_Shared_Desktop_Layer_Touches_No_Disk" />. The
    ///     same scanner must still find a real call in a file that legitimately has
    ///     one, or "no disk calls in the project" is indistinguishable from "the regex
    ///     reads nothing".
    /// </summary>
    [Test]
    public async Task Disk_Scanner_Still_Sees_A_Real_Call()
    {
        IReadOnlyList<string> hits = FindIoInRepoFile(DiskCall, DiskControlFile);

        await Assert.That(hits.Count).IsGreaterThan(0).Because(
            "The scanner behind The_Shared_Desktop_Layer_Touches_No_Disk must be able to "
            + "fail. JsonlSessionStore is Infrastructure and legitimately creates, appends "
            + "to, enumerates and deletes session files — if the scanner reports nothing "
            + "even there, it is reporting nothing everywhere and this project's pass is "
            + "meaningless. If this control ever goes red, the fix is the REGEX or the "
            + "PATH, not the store.");
    }

    /// <summary>
    ///     Non-vacuity for <see cref="The_Shared_Desktop_Layer_Resolves_No_User_Location" />,
    ///     in the same shape and for the same reason.
    /// </summary>
    [Test]
    public async Task Environment_Scanner_Still_Sees_A_Real_Lookup()
    {
        IReadOnlyList<string> hits = FindIoInRepoFile(EnvironmentLookup, EnvironmentControlFile);

        await Assert.That(hits.Count).IsGreaterThan(0).Because(
            "The scanner behind The_Shared_Desktop_Layer_Resolves_No_User_Location must be "
            + "able to fail. ThemeStore is the composition-root persister #742 moved theme "
            + "state to, and it legitimately resolves the user's Harbor directory — if the "
            + "scanner reports nothing even there, it is reporting nothing everywhere. If "
            + "this control ever goes red, the fix is the REGEX or the PATH, not the store.");
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
    ///     subject must not vanish under it. FALSE for a CONTROL, where a missing
    ///     file would otherwise be reported as a hit and turn the control green on
    ///     the one condition that should redden it.
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
