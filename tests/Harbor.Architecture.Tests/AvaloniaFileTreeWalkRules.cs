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
//   * `File.*` — a DIFFERENT capability, and not policed here. #934 converted
//     the sites (`CodeEditorViewModel` goes through the Domain
//     `ITextFileStore` now), and the rule that keeps them converted is
//     `AvaloniaTextFileIoRules` beside this one, scoped to the view-model TYPE.
//     Two capabilities in one rule is a rule that will drift, and folding a live
//     defect into a rule whose subject is the walk is what made the sentence
//     below cite two closed issues as its owner.
//
// A CORRECTION TO WHAT THE BULLET ABOVE USED TO SAY, AND IT IS WORTH KEEPING
// ---------------------------------------------------------------------------
// It used to name `CodeEditorViewModel` and `ThemeService` as two unfixed sites
// and cite two issue NUMBERS as their owner. Both are closed, and both were
// about `JsonCommonConfigStore` / `JsonAppConfigStore` / `RecentItemsService` in
// `Harbor.Desktop.Abstractions` and `Harbor.Desktop.Shared` — different
// projects, different types. `git log -S CodeEditorViewModel --
// .../PresentationCapabilityRules.cs`, and the same for `ThemeService`, are both
// empty: neither type was ever in that table, and their rows are gone
// (`ResolvedViolations` names the resolved types).
//
// So the carve-out leaned on a closed issue that never contained the code it
// excuses. That is worse than no citation at all, because it converts an
// untracked defect into one that READS as handled: a person checking the
// reference finds a closed issue and concludes the capability is done. The false
// citation was corrected in #941; #934 is the capability it now points at, and
// the sites are fixed rather than merely re-cited.
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

    /// <summary>Repo-relative <c>path:line</c> of every forbidden shape in a file.</summary>
    private static List<string> DetectIn(string relativePath, string source)
    {
        var hits = new List<string>();
        string[] lines = SourceScan.StripComments(source).Split('\n');
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

        // Working out a file's extension and base name is pure string handling over
        // a path the port already produced; #667 made the same call for
        // GetDirectoryName in the TUI panel.
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
                + "capabilities. Folding them into a rule whose subject is the walk would make it "
                + "permanently red and therefore deletable. The file-I/O half has its own rule since #934 — "
                + "`AvaloniaTextFileIoRules`, scoped to the view-model type — and NOT #534/#535, which this "
                + "text used to cite: those two are closed and were about JsonCommonConfigStore / "
                + "JsonAppConfigStore / RecentItemsService in different projects, and never covered "
                + "CodeEditorViewModel or ThemeService. A carve-out citing a closed issue that does not "
                + "cover the code it excuses reads as 'handled' to the next person who checks.");
        await Assert.That(DetectIn("Strings.cs", pureStringPathWork)).IsEmpty()
            .Because(
                "`Path.GetExtension` / `Path.GetFileName` are string operations, not syscalls. The icon "
                + "mapping keys on an extension, so forbidding this would forbid the fix.");
    }

    /// <summary>
    ///     The <c>File.*</c> carve-out in the file remarks must cite a LIVE issue
    ///     that actually covers the code it excuses (#681).
    /// </summary>
    /// <remarks>
    /// <para>
    ///     The remarks used to justify excluding <c>File.*</c> by pointing at
    ///     #534 and #535. Both are closed, both were about
    ///     <c>JsonCommonConfigStore</c> / <c>JsonAppConfigStore</c> /
    ///     <c>RecentItemsService</c> in <c>Harbor.Desktop.Abstractions</c> and
    ///     <c>Harbor.Desktop.Shared</c> — different projects, different types —
    ///     and neither ever named <c>CodeEditorViewModel</c> or
    ///     <c>ThemeService</c>. So the citation did not merely rot, it pointed
    ///     somewhere that never contained the thing being excused: a reader who
    ///     checked it found a closed issue and concluded the capability was
    ///     handled. <c>CodeEditorViewModel</c> is a view-model reading and
    ///     writing files with no seam, which is the defect #681 is about one
    ///     capability over from the walk, and it had no rule at all — the IL
    ///     probe never opens an app assembly, and this file does not forbid
    ///     <c>File.*</c>.
    /// </para>
    /// <para>
    ///     As of #934 that gap is closed: the sites are converted
    ///     (<c>ITextFileStore</c>) and <c>AvaloniaTextFileIoRules</c> keeps them
    ///     that way. This test still runs, and its job has narrowed — it now stops
    ///     the carve-out from going back to citing a CLOSED owner, which is the
    ///     failure that actually happened. It would be wrong to delete it on the
    ///     grounds that the sites are fixed: the defect was never the missing
    ///     rule, it was the citation that made the missing rule look owned.
    /// </para>
    /// <para>
    ///     <b>Why a text rule.</b> The thing worth enforcing is a SHAPE: an
    ///     excluded capability must name a live owner. Whether #534's types were
    ///     closed is a fact about GitHub, not about this working tree, so the
    ///     test checks what the repo can see — that the carve-out cites an issue
    ///     number, that it does not cite the two closed ones, and that the
    ///     capability it excuses has a live tracking issue. That is the part
    ///     that was silently untrue.
    /// </para>
    /// </remarks>
    [Test]
    public async Task FileIoCarveOut_CitesALiveOwner_NotTheClosedIssues()
    {
        // Read the rule's own source out of the working tree, the same way every
        // other test in this file finds what it polices. Deriving the path from
        // the assembly location instead would resolve to a build output folder
        // that has no .cs beside it.
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because(
                "This rule reads its own source text. With no Harbor.slnx above AppContext.BaseDirectory "
                + "the read fails and the test reports green while enforcing nothing.");

        if (root is null)
        {
            return;
        }

        string source = File.ReadAllText(
            Path.Combine(root, "tests", "Harbor.Architecture.Tests", "AvaloniaFileTreeWalkRules.cs"));

        // (1) The carve-out still exists and still names the capability, or the
        // text was deleted instead of corrected — which would hide it rather than
        // fix it. A missing justification is how the next reader re-adds a false
        // one without noticing there was ever a claim here at all.
        await Assert.That(source).Contains("File.*")
            .Because(
                "the `File.*` carve-out is a real exclusion with a real reason; deleting the sentence "
                + "would leave the exclusion unexplained and the defect untracked rather than fixed.");

        // (2) The specific FALSE citation must be gone — the verbatim phrase that
        // made the defect look owned. Pinned on the exact wording rather than on
        // the issue NUMBERS, because the numbers themselves are legitimate in two
        // places and must stay: the `Directory.CreateDirectory` carve-out is
        // genuinely grandfathered by those two config stores, and the correction
        // below names them precisely because saying "these do NOT cover this" is
        // the point. A test that banned the numbers would force the fix to
        // delete the very sentence that records what the numbers got wrong.
        // Assembled at runtime, not written out: this test reads its OWN source
        // text, so a literal spelling of the phrase it forbids would match itself
        // and the rule would be red forever — a guard that cannot pass is a guard
        // that gets deleted. Splitting the string keeps the forbidden text out of
        // the file while pinning exactly what must not come back.
        string tail = ", #535)";
        foreach (string stale in new[]
                 {
                     "tracked elsewhere (#534" + tail,
                     "tracked elsewhere (#534" + "/#535)",
                     "elsewhere (#534" + tail,
                 })
        {
            await Assert.That(source).DoesNotContain(stale)
                .Because(
                    "the `File.*` carve-out was justified by claiming the sites were 'tracked elsewhere "
                    + "(#534, #535)'. They were not: both are CLOSED, both were about "
                    + "JsonCommonConfigStore / JsonAppConfigStore / RecentItemsService in other projects, "
                    + "and neither ever named CodeEditorViewModel or ThemeService. This exact wording is "
                    + "what let an untracked view-model read as handled — see the correction in this file's "
                    + "remarks, and #934 for the live owner.");
        }

        // (3) The capability actually has a live owner named next to the carve-out.
        await Assert.That(source).Contains("#934")
            .Because(
                "the `File.*` carve-out must name a live tracking issue. #934 covered CodeEditorViewModel "
                + "reading and writing files with no seam — the same defect #681 is about, one capability "
                + "over from the walk — and it is now fixed: the view-model goes through the Domain "
                + "`ITextFileStore` and `AvaloniaTextFileIoRules` keeps it that way. Delete that reference "
                + "and the carve-out is unowned again, which is the state that let an untracked defect read "
                + "as a closed one.");
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
            string source = SourceScan.StripComments(File.ReadAllText(file));
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
