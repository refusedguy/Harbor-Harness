// AvaloniaFileTreeWalkRules.cs — source-level guard for issue #492:
// `MainViewModel` owned a recursive filesystem scanner (`Directory.GetDirectories` /
// `Directory.GetFiles` / `new DirectoryInfo`), a hardcoded depth cap, a hardcoded
// ignore list and a hardcoded extension→icon `switch`, all inside a shell view-model.
//
// THE PRECEDENT THIS FOLLOWS
// --------------------------
// `AvaloniaTerminalPaneSpawnRules` (#672) and `AvaloniaFireAndForgetRules` (#569)
// are both SOURCE-TEXT rules spanning `apps/Harbor.App.Avalonia`, because that
// project is an app — a composition root — and is deliberately absent from
// `FullLayerMatrixTests.AllSrcAssemblies` (a `src/`-only list) and therefore from
// the IL probe in `PresentationCapabilityRules`. A source scan needs no
// reference edge, which is the only way a rule can reach a project the test
// assembly must not reference.
//
// WHY NO BASELINE ROW IN PresentationCapabilityRules
// -------------------------------------------------
// Same argument as #672, and it is structural rather than stylistic: a
// `KnownViolations` row is keyed by (assembly, rule, type), and the enforcer only
// iterates `FullLayerMatrixTests.PresentationLayerAssemblies()`. A row naming
// `Harbor.App.Avalonia.ViewModels.MainViewModel` would be a row against an
// assembly the probe never opens — a lie in the one table whose entire purpose is
// to be checkable. The rows for `CellForgeFileTreePanel` were DELETED by #667, and
// deleting them is what armed `DirectoryRule`; re-introducing the same capability
// in the app is invisible to that rule, which is exactly why this file exists.
//
// WHAT IS FORBIDDEN, AND WHAT IS NOT
// ---------------------------------
// The capability is the WALK — the syscall sequence that a file tree is built
// from. `Directory.Get*` / `Directory.Enumerate*` / `new DirectoryInfo` are the
// four spellings a walk can take, and all four are listed. Two neighbouring
// shapes are deliberately NOT forbidden, because forbidding them would either
// ban the fix or ban an unrelated, already-accepted capability:
//
//   * `Directory.CreateDirectory` — AppHost.cs:127-128 makes `~/.harbor` and
//     `~/.harbor/sessions` so a fresh install needs no manual setup. That is a
//     composition root creating its own config directory, it is a single bounded
//     call rather than an unbounded walk, and `PresentationCapabilityRules`
//     grandfathers the equivalent shape for the two config stores (#534/#535).
//   * `File.*` — `CodeEditorViewModel` reads and writes the file the user picked
//     in the tree this rule is about, and `ThemeService` reads a theme. Those are
//     two different defects, in two different types; a rule that swept them in
//     would fail on a red the owner of #492 cannot fix inside this PR, and the
//     cheap repair for a permanently-red rule is deletion.
//
// NON-VACUITY
// -----------
// A source guard that silently matches nothing is worse than no guard. Four
// defences, mirroring `AvaloniaTerminalPaneSpawnRules`:
//
//   * `Scanner_FindsTheGuardedProject` — the walk really finds the shell.
//   * `Detector_FiresOnAKnownWalk_AndStaysQuietOnTheSeamCall` — the detector, in
//     isolation, fires on the exact pre-#492 shape and is quiet on the post-#492
//     shape AND on the two neighbouring capabilities above.
//   * `Detector_IsNotDefeatedByRenamingTheLocal` — pinned on the walk CALL, not
//     on a variable name, so a rename cannot silently un-guard the line.
//   * `Detector_IgnoresTheExplanationInProse` — a file that documents the old
//     shape does not itself violate the rule.
//
// THE SECOND RULE
// ---------------
// The walk is one half of #492; the other half is that the IGNORE LIST and the
// EXTENSION→ICON MAP were private statics in the view-model, i.e. policy in the
// presentation layer. "Policy is not in the view-model" is not greppable without
// pinning literals, so it is pinned structurally instead: the policy is a Domain
// port, and the app may CONSUME it but may not DECLARE an implementer of it. That
// is the `ThemeStoreSeamRules` shape, and it is a red/green pair of its own —
// `FileTreePolicy_...Exists...` fails until the port is actually there.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     §ARCH guard (issue #492): the desktop shell's file-tree view-model does not
///     walk the filesystem, and the ignore/icon policy it used to hardcode is a
///     Domain port the app consumes rather than a set of private statics it owns.
/// </summary>
public class AvaloniaFileTreeWalkRules
{
    /// <summary>
    ///     Projects this rule polices. Each entry must correspond to a perimeter
    ///     that has actually been converted.
    /// </summary>
    private static readonly string[] GuardedProjects = ["apps/Harbor.App.Avalonia"];

    /// <summary>
    ///     The forbidden shapes: the four spellings of "enumerate a directory",
    ///     plus constructing a <c>DirectoryInfo</c> to enumerate through.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Keyed on the CALL rather than on the type name, so binding the
    ///         result to a differently-named local does not defeat it — the
    ///         <c>Detector_IsNotDefeatedByRenamingTheLocal</c> test below pins
    ///         that.
    ///     </para>
    ///     <para>
    ///         <c>Directory.CreateDirectory</c> is absent on purpose; see the file
    ///         remarks. <c>Path.*</c> is absent too: <c>Path.GetFileName</c> and
    ///         <c>Path.GetExtension</c> are pure string handling over a path the
    ///         port already handed over, and the #667 note in
    ///         docs/ARCHITECTURE_LAYERS.md §ARCH-5 makes the same call for
    ///         <c>Path.GetDirectoryName</c>.
    ///     </para>
    /// </remarks>
    private static readonly Regex[] ForbiddenPatterns =
    [
        new(@"Directory\s*\.\s*(Get|Enumerate)\w*\s*\(", RegexOptions.Compiled),
        new(@"new\s+DirectoryInfo\s*\(", RegexOptions.Compiled),
    ];

    /// <summary>
    ///     The Domain port that owns "what counts as source" — the directory
    ///     ignore list and the extension→icon map the view-model used to hardcode.
    /// </summary>
    private const string PolicyContractName = "IFileTreePolicy";

    /// <summary>
    ///     The Domain port that owns "how a directory is read". Named here so the
    ///     app cannot quietly reimplement the walk instead of going through it.
    /// </summary>
    private const string ListerContractName = "IDirectoryLister";

    /// <summary>
    ///     Strips whole-line <c>//</c> comments so a file that explains the rule
    ///     (including this guard's own failure text) is not itself a violation.
    /// </summary>
    private static string StripLineComments(string source) =>
        string.Join('\n', source.Split('\n')
            .Select(line =>
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                return idx < 0 ? line : line[..idx];
            }));

    /// <summary>Repo-relative <c>path:line</c> of every forbidden shape in a file.</summary>
    private static List<string> DetectIn(string relativePath, string source)
    {
        var hits = new List<string>();
        string[] lines = StripLineComments(source).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            // One report per line, even when both patterns match it: a single
            // `new DirectoryInfo(p).EnumerateFiles()` is one violation, and a
            // failure message that prints it twice reads as two bugs.
            foreach (Regex pattern in ForbiddenPatterns)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    hits.Add($"{relativePath}:{i + 1}: {lines[i].Trim()}");
                    break;
                }
            }
        }

        return hits;
    }

    [Test]
    public async Task AvaloniaShell_DoesNotWalkTheFilesystem()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because(
                "This guard walks the working tree. With no Harbor.slnx above AppContext.BaseDirectory "
                + "the scan yields nothing and the rule reports green while enforcing nothing.");

        if (root is null)
        {
            return;
        }

        var violations = new List<string>();
        foreach (string file in EnumerateGuardedFiles(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            violations.AddRange(DetectIn(relative, File.ReadAllText(file)));
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "§ARCH (#492). A shell view-model that walks the filesystem owns I/O, threading, "
                + "recursion depth, the entry budget and the ignore list at once, and none of it is "
                + "testable without a real temp directory: the root is a VM property and the policies "
                + "are private statics. #667 removed exactly this shape from CellForgeFileTreePanel and "
                + "left the seam behind — the Domain `IDirectoryLister` (Application: "
                + "`SystemDirectoryLister`, bounded at 4096 entries, 5s timeout, cancellable between "
                + "entries) and the Domain `IFileTreePolicy` for what a tree shows. Go through both, "
                + "and keep the scan in a named, testable service that owns its own cancellation. "
                + "docs/ARCHITECTURE_LAYERS.md §3 assigns directory enumeration to Infrastructure. "
                + string.Join("\n", violations));
    }

    // ── non-vacuity ───────────────────────────────────────────────────────

    [Test]
    public async Task Scanner_FindsTheGuardedProject()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because("The walk needs a repository root; without one this file guards nothing.");

        if (root is null)
        {
            return;
        }

        int files = EnumerateGuardedFiles(root).Count;
        await Assert.That(files).IsGreaterThan(50)
            .Because(
                $"The guarded Avalonia shell should hold well over 50 source files; found {files}. "
                + "A near-zero count means the path is stale and the rule enforces nothing.");
    }

    [Test]
    public async Task Detector_FiresOnAKnownWalk_AndStaysQuietOnTheSeamCall()
    {
        // The exact pre-#492 shape: the two enumeration calls and the DirectoryInfo.
        const string knownWalk = """
            public sealed partial class MainViewModel
            {
                private void LoadDirectory(FileTreeNode parent, string path, int depth)
                {
                    if (depth > 3) return;
                    foreach (var dir in Directory.GetDirectories(path).OrderBy(d => d))
                    {
                        parent.Children.Add(new FileTreeNode { FullPath = dir });
                    }
                    foreach (var file in Directory.GetFiles(path).OrderBy(f => f))
                    {
                        parent.Children.Add(new FileTreeNode { FullPath = file });
                    }
                }
            }
            """;

        // The post-#492 shape: the view-model only ever names the two ports, and
        // the walk itself is somebody else's problem.
        const string seamCall = """
            public sealed partial class MainViewModel
            {
                private readonly ProjectFileTreeScanner _scanner;

                public async Task RefreshFileTreeAsync()
                {
                    IReadOnlyList<FileTreeNode> nodes = await _scanner.ScanAsync(ProjectRootPath, token);
                    FileTree.Clear();
                    foreach (FileTreeNode node in nodes) { FileTree.Add(node); }
                }
            }
            """;

        // The two neighbouring capabilities the rule must NOT swallow. Both exist
        // in the app today (AppHost.cs, CodeEditorViewModel.cs); a rule that fires
        // on them is a rule whose only fix is deletion.
        const string unrelatedIo = """
            public void EnsureConfigDirs(string homeDir)
            {
                Directory.CreateDirectory(Path.Combine(homeDir, ".harbor"));
                Directory.CreateDirectory(Path.Combine(homeDir, ".harbor", "sessions"));
            }

            public async Task OpenAsync(string path)
            {
                if (!File.Exists(path)) return;
                string content = await File.ReadAllTextAsync(path);
            }
            """;

        // `Path.*` is pure string handling over a path the port already produced;
        // #667 made the same call for `Path.GetDirectoryName` in the TUI panel.
        const string pureStringPathWork = """
            public string Display(string fullPath)
            {
                string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                return Path.GetFileName(ext);
            }
            """;

        await Assert.That(DetectIn("Known.cs", knownWalk)).IsNotEmpty()
            .Because("the pre-#492 walk shape must be detected, or the rule guards nothing");
        await Assert.That(DetectIn("Seam.cs", seamCall)).IsEmpty()
            .Because(
                "the seam call is the shape this issue converts TO. If it is flagged the rule forbids the "
                + "fix as well as the bug, and the only way to make CI green would be to widen or delete it.");
        await Assert.That(DetectIn("Unrelated.cs", unrelatedIo)).IsEmpty()
            .Because(
                "creating the app's own config directory and reading the file the user picked are two other "
                + "capabilities, tracked elsewhere (#534, #535). Folding them in here would make the rule "
                + "permanently red and therefore deletable.");
        await Assert.That(DetectIn("Strings.cs", pureStringPathWork)).IsEmpty()
            .Because(
                "`Path.GetExtension` / `Path.GetFileName` are string operations, not syscalls. The icon "
                + "mapping keys on an extension, so forbidding this would forbid the fix.");
    }

    [Test]
    public async Task Detector_IsNotDefeatedByRenamingTheLocal()
    {
        // The old code could have been written with any local name, and the walk
        // could have been reached through a `DirectoryInfo` instead of the static
        // overloads. Keying the rule on a variable name would have been the easy
        // way to write this guard, and it would have been worthless.
        const string renamed = """
            public void LoadDirectory(FileTreeNode parent, string path, int depth)
            {
                var handle = new DirectoryInfo(path);
                var dirs = handle.EnumerateDirectories();
                var files = handle.EnumerateFiles();
                parent.Children.Add(new FileTreeNode { FullPath = dirs.First() });
            }
            """;

        await Assert.That(DetectIn("Renamed.cs", renamed)).IsNotEmpty()
            .Because(
                "the rule keys on the walk call and on the DirectoryInfo, not on a variable name — otherwise "
                + "a rename silently un-guards the very line it was written for");
    }

    [Test]
    public async Task Detector_IgnoresTheExplanationInProse()
    {
        // The refactor's own comments name the forbidden call. A guard that fails
        // on its own documentation is a guard nobody keeps.
        const string prose = """
            // #492: this used to call Directory.GetDirectories(path) and Directory.GetFiles(path).
            /// <summary>Scans through the port; never Directory.GetFiles here.</summary>
            public void Refresh() => _scanner.ScanAsync(root, token);
            """;

        await Assert.That(DetectIn("Prose.cs", prose)).IsEmpty()
            .Because(
                "line comments are stripped before matching, so documenting the old shape does not "
                + "reintroduce it");
    }

    // ── the policy half ───────────────────────────────────────────────────

    /// <summary>
    ///     The file-tree policy is a Domain port, and the app consumes it rather
    ///     than declaring an implementer of it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the second half of #492, and it is deliberately structural
    ///         rather than a grep for <c>"node_modules"</c>. A literal check would
    ///         pin today's vocabulary: rename the list, or spell the same idea
    ///         differently, and the rule either goes stale or cries wolf. The
    ///         decision worth enforcing is a SHAPE — the app does not define file-
    ///         tree policy, it is handed one — and that shape survives any rewrite
    ///         of the list itself.
    ///     </para>
    ///     <para>
    ///         Same precedent as <c>ThemeStoreSeamRules</c>, which fails when a
    ///         second implementer of a port appears. Here the risk runs the other
    ///         way too: an implementer declared inside the app would put the policy
    ///         back in the presentation layer with a friendlier name.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task FileTreePolicy_IsADomainPort_TheAppOnlyConsumes()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because("This rule reads the working tree; without a checkout it enforces nothing.");

        if (root is null)
        {
            return;
        }

        var failures = new List<string>();

        // (1) The contract must exist, in Domain, next to IDirectoryLister. A
        //     policy implemented inside the app is a policy in the view-model's
        //     layer wearing a class name.
        string contract = Path.Combine(root, "src", "Harbor.Abstractions", "Filesystem", PolicyContractName + ".cs");
        if (!File.Exists(contract))
        {
            failures.Add(
                $"src/Harbor.Abstractions/Filesystem/{PolicyContractName}.cs does not exist. The ignore "
                + "list and the extension→icon map are POLICY (what a tree shows), not presentation: "
                + $"{ListerContractName} answers how a directory is read, and this contract answers which "
                + "entries a view shows and what glyph a file gets. They are separable because the first "
                + "may depend on the second and not the other way round. #492.");
        }

        // (2) The app must not DECLARE an implementer of either port. Consuming is
        //     the whole point; implementing either one puts the walk or the policy
        //     back in the presentation layer.
        foreach (string file in EnumerateGuardedFiles(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string source = StripLineComments(File.ReadAllText(file));
            foreach (string line in source.Split('\n'))
            {
                if (Regex.IsMatch(line, $@":\s*({PolicyContractName}|{ListerContractName})\b", RegexOptions.Compiled)
                    || Regex.IsMatch(line, $@"new\s+System(DirectoryLister|FileTreePolicy)\s*\(", RegexOptions.Compiled))
                {
                    failures.Add(
                        $"{relative}: declares an implementer of a Domain port. The desktop shell consumes "
                        + $"{ListerContractName}/{PolicyContractName}; it does not provide them. {line.Trim()}");
                }
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because(
                "§ARCH (#492). A view-model that owns its own ignore list and icon map has no seam: the "
                + "policy is unreachable, so a project that puts its build output in a differently-named "
                + "directory silently grows a useless tree, and a new file type means editing a `switch` "
                + "inside a ViewModel. The port belongs in Domain beside "
                + $"{ListerContractName}; the implementation belongs in Harbor.Application beside "
                + "SystemDirectoryLister; the app wires them and asks. "
                + string.Join("\n", failures));
    }

    private static List<string> EnumerateGuardedFiles(string root)
    {
        List<string> found = [];
        foreach (string project in GuardedProjects)
        {
            string dir = Path.Combine(root, project);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}.worktrees{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                found.Add(file);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }
}
