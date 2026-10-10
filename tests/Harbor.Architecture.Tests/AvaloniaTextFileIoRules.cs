// AvaloniaTextFileIoRules.cs — source-level guard for issue #934:
// `CodeEditorViewModel` read and wrote the file the user opened straight from the
// view-model, with no seam: `File.Exists` :109, `File.ReadAllTextAsync` :114,
// `File.WriteAllTextAsync` :138.
//
// WHY A SEPARATE FILE AND NOT A WIDENING OF AvaloniaFileTreeWalkRules
// -------------------------------------------------------------------
// The walk rule's own header says why it does not forbid `File.*`: folding the
// file-I/O capability in would make it permanently red, and "the cheap repair for
// a permanently-red rule is deletion" is written there as a principle. That
// principle is about a rule that is red *because the sites are still wrong*. This
// issue converted the sites, so the widening is green-able — which means the
// honest move was to convert them and then widen somewhere, not to leave the
// defect standing in order to keep a guard quiet. Two capabilities, two rules,
// one file each.
//
// THE CARVE-OUT WAS FALSE, AND THAT MATTERED MORE THAN THE DEFECT
// ----------------------------------------------------------------
// The walk rule excused `File.*` by saying the sites were "tracked elsewhere
// (#534, #535)". Both are closed; both were about `JsonCommonConfigStore` /
// `JsonAppConfigStore` / `RecentItemsService` in `Harbor.Desktop.Abstractions` and
// `Harbor.Desktop.Shared` — different projects, different types — and
// `git log -S CodeEditorViewModel -- .../PresentationCapabilityRules.cs` is empty,
// so neither type was ever in that table. A carve-out citing a closed issue that
// does not cover the code it excuses reads as "handled" to the next person who
// checks it, which is the worse failure: an untracked defect that nobody owns.
// #941 corrected the citation; this file is what the citation now points at.
//
// WHY A SOURCE SCAN AND NOT PresentationCapabilityRules
// -----------------------------------------------------
// Same structural reason as the walk rule, and worth restating because the reason
// a rule CANNOT see a project is a property of the rule, not of the tree.
// `FullLayerMatrixTests.Matrix` and `AllSrcAssemblies` are both `src/`-only — the
// csproj that builds this test project says so in as many words ("App entry
// points (apps/) are composition roots and are unrestricted by design"), and the
// project references no `apps/` assembly at all. So the IL probe in
// `PresentationCapabilityRules` never opens `Harbor.App.Avalonia`, and a
// `KnownViolations` row naming this view-model would be a row against an assembly
// the enforcer never scans. A source scan needs no reference edge, which is the
// only way a rule can reach a composition root the test assembly must not
// reference. `SourceScan.EnumerateCsFiles` is the shared walk: it is path-based,
// not csproj-based, so it sees the app whether or not anything references it.
//
// WHAT IS FORBIDDEN, AND WHAT IS NOT
// ----------------------------------
// The capability is "touch a file": any static member of `System.IO.File`, plus
// the three handles that are the same capability by another spelling
// (`FileInfo`, `FileStream`, `StreamReader`/`StreamWriter`). Keyed on the CALL, not
// on a variable or a member name, so renaming the local cannot un-guard the line.
//
// The perimeter is the TYPES, not the folder and not the file name: a file is
// policed when it declares a type whose name ends in — or contains — `ViewModel`.
// #941 counted view-models by declaration for the same reason, and a rule keyed on
// the path `ViewModels/` would be defeated by moving the file while keeping the
// behaviour.
//
// Deliberately NOT forbidden, each for a stated reason:
//   * `Path.*` — pure string handling over a path the picker or the tree already
//     produced. `Path.GetFileName` / `GetExtension` are how the tab is labelled;
//     forbidding them would forbid the fix. Same call as the walk rule.
//   * `Directory.*` — the walk, and it is `AvaloniaFileTreeWalkRules`' job. Two
//     rules on one directory is a duplicate that will drift.
//   * `Environment.GetFolderPath` and the rest of `Environment.*` — a path STRING,
//     not file I/O, and out of this perimeter.
//   * NON-view-model types in the same project. `ThemeService.LoadJson` is a
//     different defect with a different owner: `src/Harbor.DesignSystem/
//     DesignSystem/IThemeStore.cs` already NAMES it, in its own remarks, as a site
//     that did not adopt that port. Re-deciding it here would put two owners on
//     one line. `App.axaml.cs`'s first-launch `File.Exists` is the composition
//     root asking whether a config file is there, which is the same shape the walk
//     rule grandfathers for `Directory.CreateDirectory`.
//
// NON-VACUITY
// -----------
// A source guard that silently matches nothing is worse than no guard, and a
// source guard whose PERIMETER silently emptied is worse still. Four defences,
// mirroring `AvaloniaFileTreeWalkRules`, plus one this shape needs and that one
// does not:
//   * `Scanner_FindsTheGuardedProject` — the walk finds the app AND finds a
//     non-trivial number of view-model types in it. A perimeter of zero passes
//     every violation check, so the perimeter has its own floor.
//   * `Detector_FiresOnThePreFixShape_AndStaysQuietOnTheSeamCall`
//   * `Detector_IsNotDefeatedByRenamingTheLocal`
//   * `Detector_IgnoresTheExplanationInProse`
//   * `Detector_IgnoresAServiceInTheSameProject` — the perimeter is the TYPE, so
//     this is the control that proves the rule is not simply "no `File` in the app".
//   * `TextFileIo_IsADomainPort_WiredAndConsumedByAViewModel` — the seam half,
//     below.
//
// THE SEAM HALF, AND WHY IT CHECKS WIRING
// ---------------------------------------
// A port that exists and is never registered compiles, passes every gate, and
// substitutes nothing — the shape that `IGitQuery` was in. So this rule asserts
// four separate facts, and the second pair is the ones a mere "does the interface
// exist" check would miss:
//   1. the contract is in Domain, beside `IDirectoryLister`;
//   2. the app does not DECLARE an implementer (consuming is the point);
//   3. the app REGISTERS one — an `Add*<ITextFileStore>` line, which is the only
//      place the composition root is stated, and which a registration added
//      nowhere would simply not have;
//   4. a view-model CONSUMES one — the contract named in a `*ViewModel*` type, so
//      the port is on a live path and not decoration.
//
// MECHANISM (#1086, step 2, conveyor)
// -----------------------------------
// The two forbidden-shape halves below are ScanRules: the view-model file-I/O
// ban (a CustomParse, because the perimeter is the declaring TYPE — a per-file
// gate the shared line scan cannot express) and the no-implementer ban (a plain
// line scan). Enumeration, stripping, matching and the control/discovery verdicts
// are ScanRunner's; this file keeps the issue prose and the test names.
//
// The per-file gate is exactly equivalent to the old app-wide collection, not a
// re-decision: the old `IsViewModel` tested a file's declaration matches against
// the set of ALL matches across the project, and every match of this file is in
// that set by construction — so a file was inside the perimeter iff the
// declaration pattern matches it. The second part of a `partial` split repeats
// the type name and therefore still matches, which the Partial controls prove.
// The registration/consumption existence checks (facts 3–4) and the contract
// existence (fact 1) are not forbidden-shape scans and stay handwritten.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     §ARCH guard (issue #934): the desktop shell's view-models do not read or
///     write files, and the text-file capability they need is a Domain port the
///     app consumes, registers and holds — not a set of <c>File.*</c> calls and not
///     a port that exists only on paper.
/// </summary>
public class AvaloniaTextFileIoRules
{
    private const string SubId = "VIEWMODEL-FILE-IO";
    private const string NoImplementerSubId = "DOMAIN-PORT-IMPLEMENTER";

    /// <summary>
    ///     Projects this rule polices. A composition root is the only kind of
    ///     project a reference-free scan can reach, so this list is expected to
    ///     stay short and each entry to be an <c>apps/</c> project.
    /// </summary>
    private static readonly string[] GuardedProjects = ["apps/Harbor.App.Avalonia"];

    /// <summary>
    ///     The Domain port that owns "read and write one text file". Named so the
    ///     app cannot quietly reintroduce <c>File.*</c> behind a private helper of
    ///     its own, and so the failure text can say where the contract belongs.
    /// </summary>
    private const string PortContractName = "ITextFileStore";

    /// <summary>
    ///     The <c>System.IO</c> implementation, in <c>Harbor.Application</c> beside
    ///     <c>SystemDirectoryLister</c>. Named so an implementer declared in the app
    ///     is a visible edit rather than a differently-spelled one.
    /// </summary>
    private const string PortImplementationName = "SystemTextFileStore";

    /// <summary>
    ///     The forbidden shapes: any static member of <c>System.IO.File</c>, and the
    ///     three handle types that are the same capability spelled differently.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The lookbehind on the first pattern is what makes <c>FileTreeNode</c>
    ///         and <c>SomeFile.Read()</c> safe to have in the file: a preceding
    ///         identifier character disqualifies the match. The cost is stated
    ///         rather than hidden — a call reached through an instance property
    ///         whose name ENDS in <c>File</c> is not matched, and the same limitation
    ///         the walk rule records for its own patterns.
    ///     </para>
    ///     <para>
    ///         <c>File.Move</c> / <c>Copy</c> / <c>Delete</c> are matched by the same
    ///         pattern as <c>ReadAllText</c>: the capability is touching a file, not
    ///         one member of it, and a rule that listed today's three spellings would
    ///         be bypassed by using the fourth.
    ///     </para>
    /// </remarks>
    private static readonly Regex[] ForbiddenPatterns =
    [
        new(@"(?<![A-Za-z0-9_])File\s*\.\s*[A-Za-z_][A-Za-z0-9_]*\s*\(", RegexOptions.Compiled),
        new(@"new\s+FileInfo\s*\(", RegexOptions.Compiled),
        new(@"new\s+(?:FileStream|StreamReader|StreamWriter)\s*\(", RegexOptions.Compiled),
    ];

    /// <summary>
    ///     The three forbidden shapes as one alternation, composed from the same
    ///     fields the parser grades — not a copy — so this row cannot drift from
    ///     them. The runner does not execute it (the rule grades through
    ///     <see cref="ParseViewModelFileIo" />); it documents the shape the rule
    ///     bans and carries the failure text. One report per line, as before: a
    ///     shape spelled `new FileStream(p)` on a line that also calls
    ///     `File.Exists(p)` is one violation.
    /// </summary>
    private static readonly Regex ForbiddenShape = new(
        string.Join("|", ForbiddenPatterns.Select(static p => "(?:" + p.ToString() + ")")),
        RegexOptions.Compiled);

    /// <summary>
    ///     Declaring an implementer of the Domain port in the app — the shape the
    ///     seam half forbids. Consuming the port (a constructor parameter, a
    ///     registration line) does not match: the colon must directly precede the
    ///     contract name, and the construction must name the implementation.
    /// </summary>
    private static readonly Regex ImplementerShape = new(
        @":\s*" + PortContractName + @"\b"
        + @"|new\s+" + PortImplementationName + @"\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     Matches a class/record declaration whose type name contains
    ///     <c>ViewModel</c>. Over-inclusive on purpose: a type called
    ///     <c>NotAViewModel</c> joins the perimeter, and the cost of that is one
    ///     file reviewed rather than one defect shipped.
    /// </summary>
    private static readonly Regex ViewModelDeclaration =
        new(@"\b(?:class|record(?:\s+(?:class|struct))?)\s+[A-Za-z_][A-Za-z0-9_]*ViewModel[A-Za-z0-9_]*\b",
            RegexOptions.Compiled);

    /// <summary>The file-I/O ban as data: one documented shape, a per-file type-gated parser, controls, a floor.</summary>
    private static readonly ScanRule FileIoRule = new()
    {
        Id = "AvaloniaTextFileIo",
        Trees = ["apps/Harbor.App.Avalonia"],
        Forbidden =
        [
            new ScanForbidden(
                SubId,
                ForbiddenShape,
                "go through the Domain ITextFileStore (Harbor.Abstractions, beside IDirectoryLister), "
                + "implemented in Harbor.Application beside SystemDirectoryLister, registered in "
                + "ServiceRegistration.RegisterAppServices, and injected into the view-model. "
                + "Path strings are still the view-model's business."),
        ],
        Controls =
        [
            // The exact pre-#934 shape, from CodeEditorViewModel: three offending
            // lines, each its own control — each graded with the declaration that
            // puts the file inside the perimeter.
            new ScanControl("Pre/Exists.cs", """
                public sealed partial class CodeEditorViewModel : ObservableObject
                {
                    public async Task LoadFileAsync(string path)
                    {
                        if (!File.Exists(path)) return;
                    }
                }
                """, SubId),
            new ScanControl("Pre/Read.cs", """
                public sealed partial class CodeEditorViewModel : ObservableObject
                {
                    public async Task LoadFileAsync(string path)
                    {
                        string content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                    }
                }
                """, SubId),
            new ScanControl("Pre/Write.cs", """
                public sealed partial class CodeEditorViewModel : ObservableObject
                {
                    [RelayCommand]
                    private async Task SaveAsync()
                    {
                        await File.WriteAllTextAsync(ActiveTab.FilePath, ActiveTab.Content).ConfigureAwait(false);
                    }
                }
                """, SubId),
            // The post-#934 shape: the view-model names the port and never `File`.
            new ScanControl("Seam.cs", """
                public sealed partial class CodeEditorViewModel : ObservableObject
                {
                    private readonly ITextFileStore _files;

                    public async Task LoadFileAsync(string path)
                    {
                        Result<bool> present = await _files.ExistsAsync(path).ConfigureAwait(false);
                        if (!present.Value) return;
                        string content = (await _files.ReadAsync(path).ConfigureAwait(false)).Value;
                    }

                    [RelayCommand]
                    private async Task SaveAsync()
                    {
                        await _files.WriteAsync(ActiveTab.FilePath, ActiveTab.Content).ConfigureAwait(false);
                    }
                }
                """, null),
            // Labelling a tab is string handling over a path the picker produced.
            new ScanControl("Strings.cs", """
                public sealed partial class CodeEditorViewModel : ObservableObject
                {
                    public void Label(string path)
                    {
                        TabName = Path.GetFileName(path);
                        TabSyntax = Path.GetExtension(path).TrimStart('.');
                    }
                }
                """, null),
            // The same capability, one type over, outside the perimeter: a service
            // that touches a file is a different defect with a different owner.
            // No ViewModel declaration — so the gate stays shut. This is the
            // control that proves the rule is not merely "no `File` in the app".
            new ScanControl("Service.cs", """
                public sealed class ThemeService : IThemeService
                {
                    public Result<string> LoadJson(string path)
                    {
                        if (!File.Exists(path)) return Result.Failure<string>("theme file not found");
                        return Result.Success(File.ReadAllText(path));
                    }
                }
                """, null),
            // The read could have been reached through a FileStream instead of the
            // static overloads, with any local name.
            new ScanControl("Renamed/Info.cs", """
                public sealed partial class CodeEditorViewModel : ObservableObject
                {
                    public async Task LoadFileAsync(string path)
                    {
                        var handle = new FileInfo(path);
                        TabContent = handle.FullName;
                    }
                }
                """, SubId),
            new ScanControl("Renamed/Reader.cs", """
                public sealed partial class CodeEditorViewModel : ObservableObject
                {
                    public async Task LoadFileAsync(string path)
                    {
                        using var reader = new StreamReader(path);
                        TabContent = await reader.ReadToEndAsync().ConfigureAwait(false);
                    }
                }
                """, SubId),
            // The fix's own comments name the forbidden call. A guard that fails on
            // its own documentation is a guard nobody keeps.
            new ScanControl("Prose.cs", """
                // #934: this used to call File.Exists(path) and File.ReadAllTextAsync(path).
                /// <summary>Reads through the port; never File.WriteAllTextAsync here.</summary>
                public sealed partial class CodeEditorViewModel : ObservableObject
                {
                    public Task SaveAsync(string path, string text) => _files.WriteAsync(path, text);
                }
                """, null),
            // A `partial` split is the cheapest way to move the calls out of a
            // perimeter: the second file names the type and declares nothing new.
            // The declaring half is clean; the naming half still matches the gate.
            new ScanControl("Partial/First.cs", """
                public sealed partial class CodeEditorViewModel : ObservableObject
                {
                    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();
                }
                """, null),
            new ScanControl("Partial/Second.cs", """
                public sealed partial class CodeEditorViewModel
                {
                    public async Task LoadFileAsync(string path)
                    {
                        string content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                    }
                }
                """, SubId),
        ],
        MinHits = 50,
        CustomParse = ParseViewModelFileIo,
    };

    /// <summary>The no-implementer ban as data: one shape, planted controls, the same floor.</summary>
    private static readonly ScanRule NoImplementerRule = new()
    {
        Id = "AvaloniaTextFileIo.NoImplementer",
        Trees = ["apps/Harbor.App.Avalonia"],
        Forbidden =
        [
            new ScanForbidden(
                NoImplementerSubId,
                ImplementerShape,
                "the desktop shell consumes ITextFileStore; it does not provide it. The System.IO "
                + "implementation belongs in Harbor.Application beside SystemDirectoryLister."),
        ],
        Controls =
        [
            new ScanControl("Impl/Declares.cs", "public sealed class AppTextFileStore : ITextFileStore", NoImplementerSubId),
            new ScanControl("Impl/Builds.cs", "ITextFileStore store = new SystemTextFileStore(root);", NoImplementerSubId),
            // Consuming the port — a constructor parameter — is the point, not a violation.
            new ScanControl("Impl/Consumes.cs", "public CodeEditorViewModel(ITextFileStore files)", null),
            // Registering the port is the point, not a violation.
            new ScanControl("Impl/Registers.cs", "services.AddSingleton<ITextFileStore, SystemTextFileStore>();", null),
        ],
        MinHits = 50,
    };

    /// <summary>
    ///     The custom parser: the file-I/O verdict over one file's raw source. The
    ///     file is policed only when it matches the view-model declaration — the
    ///     per-file form of the old app-wide collection, exactly equivalent (every
    ///     match of this file is in the app-wide set by construction) — and then
    ///     graded line by line with one report per line, as before.
    /// </summary>
    private static IEnumerable<ScanHit> ParseViewModelFileIo(string displayPath, string rawSource)
    {
        string stripped = SourceScan.StripComments(rawSource);
        if (!ViewModelDeclaration.IsMatch(stripped))
        {
            yield break;
        }

        string[] lines = stripped.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Regex pattern in ForbiddenPatterns)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    yield return new ScanHit(SubId, displayPath, i + 1, lines[i].Trim());
                    break;
                }
            }
        }
    }

    /// <summary>
    ///     The type names the app declares that contain <c>ViewModel</c>, collected
    ///     app-wide before any file is judged.
    /// </summary>
    private static HashSet<string> CollectViewModelTypeNames(IEnumerable<(string Path, string Source)> files)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach ((_, string source) in files)
        {
            foreach (Match match in ViewModelDeclaration.Matches(SourceScan.StripComments(source)))
            {
                names.Add(match.Value[(match.Value.LastIndexOf(' ') + 1)..]);
            }
        }

        return names;
    }

    /// <summary>
    ///     Whether <paramref name="source" /> declares — or is a further part of —
    ///     a view-model type.
    /// </summary>
    /// <remarks>
    ///     "Further part of" is what the second declaration in a
    ///     <c>partial</c> split looks like: the type name is repeated, nothing new is
    ///     declared. Judging each file only on what it declares would let a partial
    ///     split move the calls out of the perimeter, so the declared names are
    ///     collected across the whole project first and a file only has to NAME one.
    /// </remarks>
    private static bool IsViewModel(string source, HashSet<string> viewModelTypeNames)
    {
        string stripped = SourceScan.StripComments(source);
        foreach (Match match in ViewModelDeclaration.Matches(stripped))
        {
            if (viewModelTypeNames.Contains(match.Value[(match.Value.LastIndexOf(' ') + 1)..]))
            {
                return true;
            }
        }

        return false;
    }

    [Test]
    public async Task AvaloniaShell_ViewModelsDoNotTouchTheFilesystem()
    {
        List<string> violations = ScanRunner.Evaluate(FileIoRule);

        await Assert.That(violations).IsEmpty()
            .Because(
                "§ARCH (#934). A view-model that reads and writes the file the user opened owns I/O, "
                + "encoding, threading and failure handling at once, and none of it is reachable "
                + "without a real file: the class has no seam, so a test of 'open shows the content' has "
                + "to put bytes on disk to run. It is also the one syscall left on the UI thread — the "
                + "entry paths are a TreeView selection change and a toolbar button, so the `File.Exists` "
                + "probe runs on the dispatcher the user is looking at. Go through the Domain "
                + $"{PortContractName} (Harbor.Abstractions, beside `IDirectoryLister`), implemented in "
                + $"Harbor.Application beside `SystemDirectoryLister`, registered in "
                + "ServiceRegistration.RegisterAppServices (which AppHost.cs:78 calls), and injected into "
                + "the view-model. Path strings are still the view-model's business: `Path.GetFileName` "
                + "is how the tab is labelled. " + string.Join("\n", violations));
    }

    /// <summary>
    ///     Reads the guarded projects, treating a file that cannot be read as a
    ///     FAILURE rather than skipping it.
    /// </summary>
    /// <remarks>
    ///     A skipped file is a hole in the perimeter that reports green, which is
    ///     the failure mode this whole file is about. The walk rule's counterpart
    ///     silently excludes unreadable files; here the count is reported instead,
    ///     so a permissions or encoding problem shows up as a red rather than as a
    ///     narrower scan nobody notices.
    /// </remarks>
    private static List<(string Path, string Source)> ReadGuardedFiles(out List<string> unreadable)
    {
        var files = new List<(string Path, string Source)>();
        unreadable = [];
        foreach (string path in EnumerateGuardedFiles())
        {
            string? source = SourceScan.TryReadAllText(path);
            if (source is null)
            {
                unreadable.Add(
                    $"{SourceScan.Relative(path)}: could not be read. A file the scan cannot read is not "
                    + "a file without violations — skipping it would report green over an unpoliced perimeter.");
                continue;
            }

            files.Add((path, source));
        }

        return files;
    }

    // ── non-vacuity ───────────────────────────────────────────────────────

    [Test]
    public async Task Scanner_FindsTheGuardedProject_AndItsViewModels()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because("The walk needs a repository root; without one this file guards nothing.");

        if (root is null)
        {
            return;
        }

        List<string> discovery = ScanRunner.CheckDiscovery(FileIoRule);

        await Assert.That(discovery).IsEmpty()
            .Because(
                "The guarded Avalonia shell holds well over 50 source files. "
                + "A near-zero count means the path is stale and the rule enforces nothing. "
                + string.Join("; ", discovery));

        // The perimeter needs its own floor. An empty view-model set makes every
        // violation check pass with nothing judged, which is the same green as
        // "the app has no view-models" — a state in which the rule is worthless and
        // reports so.
        List<(string Path, string Source)> files = ReadGuardedFiles(out _);
        HashSet<string> perimeter = CollectViewModelTypeNames(files);
        int viewModelFiles = files.Count(f => IsViewModel(f.Source, perimeter));
        await Assert.That(viewModelFiles).IsGreaterThan(5)
            .Because(
                $"The guarded shell declares view-models in a good many files; found {viewModelFiles}. "
                + "A near-zero perimeter means the type-name matcher stopped recognising view-models, and "
                + "every violation check in this file would then be passing over an empty set.");

        // Each guarded project must exist, checked here rather than assumed by the
        // walk. RepoPaths.FindProjectDir is a path CONCATENATOR rather than an
        // existence check, and a guard that used it as a filter widened its
        // perimeter silently instead of reporting; asserting the directory is here
        // is what keeps a renamed or moved project a red rather than a green.
        foreach (string project in GuardedProjects)
        {
            await Assert.That(Directory.Exists(Path.Combine(root, project))).IsTrue()
                .Because(
                    $"the guarded project '{project}' must exist under the repository root. A path that "
                    + "concatenates to nothing is the shape that lets a guard report green over an empty "
                    + "perimeter — the file-count floor above is the backstop, and this is the cause.");
        }
    }

    [Test]
    public async Task Detector_FiresOnThePreFixShape_AndStaysQuietOnTheSeamCall()
    {
        // The exact pre-#934 shape, from CodeEditorViewModel, must fire; the
        // post-#934 port shape and the tab-labelling string work must stay quiet.
        // The snippets live on FileIoRule.Controls, so the control drives the REAL
        // parser rather than a second implementation of it.
        List<string> failures = ScanRunner.CheckControls(FileIoRule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the pre-#934 shape is what this rule exists for — if it is not detected the rule guards "
                + "nothing — and going through the port is the shape the issue converts TO, so flagging it "
                + "would forbid the fix. "
                + string.Join("; ", failures));
    }

    [Test]
    public async Task Detector_IgnoresAServiceInTheSameProject()
    {
        // The perimeter is the view-model TYPE — proved by the Service.cs control
        // on the rule, which touches files from a service and must stay quiet. A
        // rule phrased as "no `File` in the app" would be red for `ThemeService`
        // on day one, which is the permanently-red rule the walk rule's header
        // warns about.
        List<string> failures = ScanRunner.CheckControls(FileIoRule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the perimeter is the view-model TYPE. A service that touches a file is a different defect "
                + "with a different owner. "
                + string.Join("; ", failures));
    }

    [Test]
    public async Task Detector_IsNotDefeatedByRenamingTheLocal()
    {
        // The rule keys on the `File.*` call and on the handle types, not on a
        // variable name — proved by the Renamed controls on the rule, driven here
        // through the same verdict.
        List<string> failures = ScanRunner.CheckControls(FileIoRule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the rule keys on the `File.*` call and on the handle types, not on a variable name — "
                + "otherwise a rename silently un-guards the very line it was written for. "
                + string.Join("; ", failures));
    }

    [Test]
    public async Task Detector_IgnoresTheExplanationInProse()
    {
        // Comments are stripped before matching, so documenting the old shape does
        // not reintroduce it — proved by the Prose.cs control on the rule.
        List<string> failures = ScanRunner.CheckControls(FileIoRule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "comments are stripped before matching, so documenting the old shape does not "
                + "reintroduce it. "
                + string.Join("; ", failures));
    }

    [Test]
    public async Task Detector_CoversTheSecondPartOfAPartialSplit()
    {
        // A `partial` split is the cheapest way to move the calls out of a
        // perimeter keyed on declarations alone: the second file names the type and
        // declares nothing new. Proved by the Partial controls on the rule — the
        // declaring half is clean and the naming half still fires.
        List<string> failures = ScanRunner.CheckControls(FileIoRule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the second part of a partial repeats the type name and nothing else, and it must still "
                + "be judged — otherwise splitting the class empties the perimeter with no diff in "
                + "behaviour. "
                + string.Join("; ", failures));
    }

    /// <summary>
    ///     Every baseline row states why it is tolerated, in the row itself. Vacuous
    ///     while the tables are empty, and deliberately so: wired from the first row
    ///     so the first row cannot skip the argument.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_AllHaveReasons()
    {
        List<string> failures =
        [
            .. ScanRunner.CheckReasons(FileIoRule),
            .. ScanRunner.CheckReasons(NoImplementerRule),
        ];

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every baseline row must still correspond to a real hit, so the tables
    ///     cannot rot into blanket permissions: fix the code without deleting the
    ///     row and this fails.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_Are_Not_Stale()
    {
        List<(string DisplayPath, string Source)> sources =
            ScanRunner.ReadSources(ScanRunner.ScopeFiles(FileIoRule));

        List<string> stale =
        [
            .. ScanRunner.StaleBaselineKeys(FileIoRule, sources),
            .. ScanRunner.StaleBaselineKeys(NoImplementerRule, sources),
        ];

        await Assert.That(stale).IsEmpty()
            .Because("a baseline row with no violation behind it is a permission for a "
                + "problem that no longer exists: " + string.Join(", ", stale));
    }

    // ── the seam half ─────────────────────────────────────────────────────

    /// <summary>
    ///     The text-file capability is a Domain port, and it is REGISTERED and
    ///     CONSUMED — not merely declared.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the same red/green pair as
    ///         <c>AvaloniaFileTreeWalkRules.FileTreePolicy_IsADomainPort_TheAppOnlyConsumes</c>,
    ///         and the fourth assertion is the one that is easy to leave out. A port
    ///         that exists and is registered nowhere compiles, satisfies every other
    ///         gate, and substitutes nothing — the shape <c>IGitQuery</c> was in, and
    ///         a registration added next to an existing one would have passed every
    ///         check here too if the check were only "the interface is there".
    ///     </para>
    ///     <para>
    ///         Registration and consumption are checked as source shapes rather than
    ///         by resolving the container, because this project references no
    ///         <c>apps/</c> assembly and cannot. The runtime check exists and does
    ///         hold the line: <c>AppHostDiTests.BuildAsync_Registers_MainViewModel</c>
    ///         resolves <c>MainViewModel</c> → <c>IContentHost</c> →
    ///         <c>AvaloniaContentHost</c> → <c>CodeEditorViewModel</c>, so a port
    ///         that is not registered fails there with a DI exception. This rule is
    ///         the one that names the missing line.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task TextFileIo_IsADomainPort_WiredAndConsumedByAViewModel()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because("This rule reads the working tree; without a checkout it enforces nothing.");

        if (root is null)
        {
            return;
        }

        var failures = new List<string>();

        // (1) The contract must exist, in Domain, beside IDirectoryLister. A
        //     capability implemented inside the app is a capability in the
        //     view-model's layer wearing a class name.
        string contract = Path.Combine(root, "src", "Harbor.Abstractions", "Filesystem", PortContractName + ".cs");
        if (!File.Exists(contract))
        {
            failures.Add(
                $"src/Harbor.Abstractions/Filesystem/{PortContractName}.cs does not exist. Reading and writing "
                + $"one text file is a CAPABILITY, not presentation: `IDirectoryLister` answers how a directory "
                + "is read, and this answers how a file is. A view-model that calls `File.ReadAllTextAsync` "
                + "cannot be tested without putting bytes on disk. The contract belongs here; the "
                + $"`System.IO` implementation belongs in Harbor.Application beside SystemDirectoryLister. #934.");
        }

        // (2) The app may CONSUME the port and may not DECLARE an implementer of
        //     it. An implementer here puts the bytes back in the app with a
        //     friendlier name. Graded by the no-implementer rule, over the same
        //     scope and the same stripper the violation check runs.
        failures.AddRange(ScanRunner.Evaluate(NoImplementerRule));

        List<(string Path, string Source)> files = ReadGuardedFiles(out _);
        HashSet<string> viewModelTypeNames = CollectViewModelTypeNames(files);
        bool registered = false;
        bool consumedByViewModel = false;

        foreach ((string path, string source) in files)
        {
            string stripped = SourceScan.StripComments(source);

            // (3) REGISTERED. The composition root is stated in exactly one place,
            //     and this is it.
            if (Regex.IsMatch(
                    stripped,
                    $@"Add(?:Singleton|Scoped|Transient)\s*<\s*{PortContractName}\b",
                    RegexOptions.Compiled))
            {
                registered = true;
            }

            // (4) CONSUMED by a view-model, so the port is on a live path.
            if (IsViewModel(source, viewModelTypeNames)
                && stripped.Contains(PortContractName, StringComparison.Ordinal))
            {
                consumedByViewModel = true;
            }
        }

        if (!registered)
        {
            failures.Add(
                $"no `Add*<{PortContractName}>` registration anywhere in {string.Join(", ", GuardedProjects)}. "
                + $"A port that exists and is registered nowhere compiles, passes every other gate and "
                + "substitutes nothing — `IGitQuery` was in exactly that state and the palette went on "
                + "speaking to no one. Register it in ServiceRegistration.RegisterAppServices, the method "
                + "AppHost.cs:78 calls at startup, next to the IDirectoryLister line #492 added. #934.");
        }

        if (!consumedByViewModel)
        {
            failures.Add(
                $"no view-model in {string.Join(", ", GuardedProjects)} names {PortContractName}. A registered "
                + "port with no consumer is a seam that substitutes nothing, and the view-model is still the "
                + "one holding the `File.*` calls. Inject it into the constructor. #934.");
        }

        await Assert.That(failures).IsEmpty()
            .Because(
                "§ARCH (#934). The desktop shell reads and writes the file the user opened. A view-model that "
                + "owns that I/O also owns its encoding, its threading and its failure handling, and none of it "
                + "is reachable without a real file on disk. The seam is one small contract in "
                + $"Harbor.Abstractions/Filesystem beside IDirectoryLister, one `System.IO` class in "
                + "Harbor.Application beside SystemDirectoryLister, one registration, one constructor parameter. "
                + string.Join("\n", failures));
    }

    /// <summary>
    ///     The guarded projects' <c>*.cs</c> files, sorted for a stable failure
    ///     message.
    /// </summary>
    /// <remarks>
    ///     The walk itself is <see cref="SourceScan" />'s, shared with the other
    ///     source gates: it is path-based rather than csproj-based, which is why it
    ///     sees an <c>apps/</c> project at all. Note what that costs — a project with
    ///     no <c>.csproj</c> is invisible to a csproj-driven scan, and a csproj-driven
    ///     scan would also miss shared-source files compiled in from elsewhere — so
    ///     this gate reads the tree, not the build graph. A project directory that
    ///     does not exist contributes nothing here; the file-count and
    ///     directory-existence floors in
    ///     <see cref="Scanner_FindsTheGuardedProject_AndItsViewModels" /> are what
    ///     turn that into a red instead of a silent green.
    /// </remarks>
    private static List<string> EnumerateGuardedFiles()
    {
        var found = new List<string>();
        foreach (string project in GuardedProjects)
        {
            found.AddRange(SourceScan.EnumerateCsFiles(project));
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }
}
