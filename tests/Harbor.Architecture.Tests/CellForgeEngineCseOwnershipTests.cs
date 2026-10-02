// CellForgeEngineCseOwnershipTests.cs — #789: the engine's CSharpFunctionalExtensions
// reachability has no owner yet, and no prose keeps it that way.
//
// WHAT THIS FILE GUARDS
// ---------------------
// `Harbor.Tui.CellForge.Engine` uses ZERO of the CSE API surface. Measured over the
// project's own sources at the commit this guard landed on: no `using
// CSharpFunctionalExtensions`, no `Maybe`/`Maybe<>`, no `Result`/`Result<>`, no
// `IResult`, no `MaybeExtensions`/`ResultExtensions`. The engine does not need the
// package for anything it writes today — `BufferSwapChain.TryTake` returns
// `BufferPair?`, a plain `Nullable<T>`, and its single caller
// (src/Harbor.Tui.CellForge/Chat/Streaming/ScreenSession.cs) reads it as one.
//
// So the dependency is PURELY STRUCTURAL: the engine's csproj declares zero
// `PackageReference` entries, and CSE reaches the compiler only because two
// referenced projects hand it down. That makes "who owns the engine's CSE
// dependency?" a question with no answer yet — and it is #435's and #436's to
// answer, not this wave's. #591 hit exactly that wall and stopped; its
// `MaybeAbsenceTests.NullableTryReturnExemptions` entry for BufferSwapChain.cs
// records the block in prose. Prose rots. This file is the same claim in a form
// that fails the build when it stops being true.
//
// THE MEASUREMENT THAT CORRECTS #789
// ----------------------------------
// #789 (and #591) record the engine as reaching CSE "through Harbor.Abstractions
// and Harbor.Ui.Framework.State — the exact two references #435 deletes". That is
// true of the two DIRECT references and wrong about the closure. The engine also
// references `Harbor.Ui.Framework.Rendering`, which references
// `Harbor.Abstractions.Contracts`, which ALSO carries
// `<PackageReference Include="CSharpFunctionalExtensions"/>`. The full set of
// projects that hand CSE to the engine is THREE:
//
//   Harbor.Abstractions             (direct edge; also -> Abstractions.Contracts)
//   Harbor.Abstractions.Contracts   (via Ui.Framework.Rendering, and via Abstractions)
//   Harbor.Ui.Framework.State       (direct edge)
//
// Which makes the ladder 3 -> 1 -> 0, not 2 -> 0:
//
//   on dev       3 carriers
//   after #435   1  (LANDED — Abstractions.Contracts survives on the Rendering edge)
//   after #436   0  (Rendering goes too, and the reference list is empty)
//
// #435 alone therefore does NOT make the engine CSE-free, and the csproj now
// looking nearly empty is not evidence that it did: the surviving carrier is
// transitive, so nothing in the engine's own file lists it. An author landing #435
// can see the engine still resolving CSE, conclude the package is somehow needed,
// and add a direct `PackageReference` — picking a dependency owner (#789's open
// question) as a side effect of a slice that was only supposed to drop two
// references. `CseCarrierClosure_IsPinned` fires on that, and names the stage.
//
// WHY EACH TEST EXISTS
// --------------------
//   1. EngineSources_UseNoCseSurface — the finding above, pinned. If the engine
//      ever writes `Maybe<T>`, the owner question is live and must be answered
//      before, not after, the code lands.
//   2. EngineDeclaresNoDirectCsePackageReference — the owner is #435's call
//      (direct package vs vendored Maybe<T>). Adding one today compiles fine and
//      collides with #435/#436's BCL-only / standalone-leaf goals.
//   3. CseCarrierClosure_IsPinned — the blocking itself. The set is the claim
//      "#789 got the count wrong" in executable form; the count is the tripwire
//      for the day #435 or #436 lands.
//   4. TheScannerIsNotVacuous — guards the guard. Every rule above reads the
//      working tree, and a rule that silently matches nothing is worse than no
//      rule (see the vacuity-trap section in CfeValueBaselineTests.cs).

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Source- and csproj-level guard: the CellForge.Engine reaches
///     CSharpFunctionalExtensions only through references <c>#435</c>/<c>#436</c>
///     delete, and until one of them lands nobody owns the dependency (#789).
/// </summary>
public class CellForgeEngineCseOwnershipTests
{
    private const string EngineProject = "Harbor.Tui.CellForge.Engine";
    private const string CsePackage = "CSharpFunctionalExtensions";

    /// <summary>
    ///     The projects in the engine's reference closure that declare a direct
    ///     <c>&lt;PackageReference Include="CSharpFunctionalExtensions" /&gt;</c>,
    ///     sorted. Pinned, not derived — see the header for the 3 -> 1 -> 0 ladder.
    /// </summary>
    /// <remarks>
    ///     <c>internal</c> because <c>MaybeAbsenceTests</c> grades the OTHER copy of this
    ///     same claim. The <c>NullableTryReturnExemptions</c> reason for
    ///     <c>BufferSwapChain.cs</c> names the carriers as well, and it is the copy printed
    ///     into a failure message — so
    ///     <c>MaybeAbsenceTests.BufferSwapChainExemptionReason_NamesEveryMeasuredCseCarrier</c>
    ///     reads this array to check the two agree. They already drifted once: #809 measured
    ///     three carriers and corrected this file, leaving the reason still saying two;
    ///     #435 then dropped two of the three from both sides in the same commit, which
    ///     is the only reason they agree now.
    /// </remarks>
    internal static readonly string[] PinnedCseCarriers =
    [
        "Harbor.Abstractions.Contracts",
    ];

    /// <summary>
    ///     The engine's own <c>ProjectReference</c> list, pinned for the same reason
    ///     the carrier set is: <c>#435</c> drops two of these and <c>#436</c> drops the
    ///     rest, and a silent third reference would re-open the CSE path the carrier
    ///     set is measuring.
    /// </summary>
    private static readonly string[] PinnedEngineReferences =
    [
        "Harbor.DesignSystem",
        "Harbor.Ui.Framework.Rendering"
    ];

    /// <summary>
    ///     CSE's public surface as it would appear in engine code. Scoped to names
    ///     that cannot occur in this codebase for unrelated reasons:
    ///     <list type="bullet">
    ///         <item><c>CSharpFunctionalExtensions</c> — the namespace itself.</item>
    ///         <item><c>Maybe</c> / <c>Result</c> / <c>IResult</c> — the three types
    ///         #589/#591 are about. Case-sensitive on purpose: a local
    ///         <c>var result = ...</c> is not a type reference.</item>
    ///         <item><c>MaybeExtensions</c> / <c>ResultExtensions</c> — CSE's own
    ///         extension holders, which reach a project through its transitive
    ///         references without a using directive ever naming the namespace.</item>
    ///     </list>
    ///     Deliberately NOT listed: <c>Unit</c>, <c>Error</c>, <c>Ensure</c>,
    ///     <c>Bind</c>, <c>Tap</c> — all of them ordinary words in a terminal engine,
    ///     and a false positive on a bare-word scan is how a guard gets deleted.
    /// </summary>
    private static readonly Regex CseSurface = new(
        @"\b(?:CSharpFunctionalExtensions|Maybe|Result|IResult|MaybeExtensions|ResultExtensions)\b",
        RegexOptions.Compiled);

    [Test]
    public async Task EngineSources_UseNoCseSurface()
    {
        string? resolved = RepoPaths.FindProjectDir(EngineProject);
        if (resolved is not { } dir || !Directory.Exists(dir))
        {
            // Deliberately not a silent pass — the non-vacuity test fails loudly
            // with the reason, and this one reports the same missing path.
            await Assert.That(resolved).IsNotNull()
                .Because($"src/{EngineProject} must exist for its sources to be scanned for CSE usage.");

            return;
        }

        var hits = new List<string>();
        foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                // Comments are excluded for the reason MaybeAbsenceTests documents:
                // the converted sites explain themselves in prose, and TerminalCapabilities.cs
                // opens with `/// Result of terminal capability probing` — a word, not a type.
                string trimmed = lines[i].TrimStart();
                if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (CseSurface.IsMatch(lines[i]))
                {
                    hits.Add($"{Relative(file)}:{i + 1} — {lines[i].Trim()}");
                }
            }
        }

        await Assert.That(hits).IsEmpty()
            .Because(
                "The engine uses no CSharpFunctionalExtensions API today, and that is what makes the "
                + "dependency unowned rather than merely undecided (#789). The moment a Maybe<T>, a "
                + "Result<T> or the namespace itself appears here, the engine has a real requirement, so "
                + "the owner question — direct PackageReference, or a vendored Maybe<T>? — has to be "
                + "answered by #435 in the same PR, along with the csproj's \"BCL-only\" Description "
                + "claim. Do not add the reference first and the rationale later.");
    }

    [Test]
    public async Task EngineDeclaresNoDirectCsePackageReference()
    {
        string? csproj = RepoPaths.FindSrcProject(EngineProject);
        await Assert.That(csproj).IsNotNull()
            .Because($"src/{EngineProject}/{EngineProject}.csproj must exist to be read.");

        if (csproj is null)
        {
            return;
        }

        var declared = ReadPackageReferences(csproj)
            .Where(id => string.Equals(id, CsePackage, StringComparison.OrdinalIgnoreCase))
            .ToList();

        await Assert.That(declared).IsEmpty()
            .Because(
                "The engine declares zero PackageReference entries and its Description claims "
                + "\"BCL-only\"; a direct CSharpFunctionalExtensions reference contradicts both and is "
                + "not this file's decision to make. #789 leaves it open as (a) direct PackageReference "
                + "on CSE or (b) a vendored Maybe<T>, and #435 owns the reference list. Note that #436's "
                + "literal criterion — zero ProjectReference to *Harbor* assemblies — is satisfied BY a "
                + "CSE PackageReference, so that criterion alone will not catch this; the owner has to "
                + "be named in the csproj, as #789's first checkbox asks.");
    }

    [Test]
    public async Task CseCarrierClosure_IsPinned()
    {
        string[] reachable = ReachableProjects(EngineProject);
        var carriers = reachable.Where(p => DeclaresCse(p)).OrderBy(p => p, StringComparer.Ordinal).ToArray();

        await Assert.That(string.Join(", ", carriers)).IsEqualTo(string.Join(", ", PinnedCseCarriers))
            .Because(
                "The engine's reference closure reaches CSharpFunctionalExtensions through exactly one "
                + "project. #789 recorded three; #435 removed two of them — Harbor.Abstractions and "
                + "Harbor.Ui.Framework.State — so the ladder the file predicted (3 -> 1 after #435 -> 0 "
                + "after #436) is now two-thirds taken. What remains is Harbor.Abstractions.Contracts, "
                + "reached via Harbor.Ui.Framework.Rendering, which only #436 removes. So #435 alone does "
                + "NOT make the engine CSE-free, and a reader must not conclude that it did from the "
                + "engine's csproj now looking nearly empty. If this fired because a carrier was ADDED, a "
                + "new path to the package opened that neither slice accounts for. If it fired because one "
                + "was REMOVED, the slice that removed it landed: update the pin and record which one.");
    }

    [Test]
    public async Task TheScannerIsNotVacuous()
    {
        string? csproj = RepoPaths.FindSrcProject(EngineProject);
        await Assert.That(csproj).IsNotNull()
            .Because(
                "Three rules above read the working tree by path. If src/Harbor.Tui.CellForge.Engine "
                + "moves or is renamed, every one of them returns an empty result and passes — the exact "
                + "vacuity this repository has already been bitten by (NetArchTest treating a "
                + "non-existent assembly as a satisfied constraint; see PresentationCapabilityRules.cs).");

        if (csproj is null)
        {
            return;
        }

        var references = RepoPaths.ReadProjectReferences(csproj).References;
        await Assert.That(string.Join(", ", references.OrderBy(r => r, StringComparer.Ordinal)))
            .IsEqualTo(string.Join(", ", PinnedEngineReferences))
            .Because(
                "The engine's ProjectReference list is what the carrier closure is measured over. A new "
                + "reference can open a path to CSharpFunctionalExtensions that the pinned carrier set "
                + "does not name, and a dropped one can close a path that #435/#436 were supposed to "
                + "close — either way the pin above has to be re-derived, deliberately.");

        // The source scan has to be looking at a real project, not an empty folder.
        string? dir = RepoPaths.FindProjectDir(EngineProject);
        int files = 0;
        if (dir is not null && Directory.Exists(dir))
        {
            files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Count(f => !IsBuildOutput(f));
        }

        await Assert.That(files).IsGreaterThan(40)
            .Because(
                $"src/{EngineProject} holds ~60 source files; found {files}. A path that does not resolve "
                + "makes EngineSources_UseNoCseSurface pass on an empty scan.");

        // And the closure walk has to reach past the direct edges, or the third carrier
        // (Abstractions.Contracts, two hops away) is only ever "correct" by accident.
        string[] reachable = ReachableProjects(EngineProject);
        await Assert.That(reachable.Length).IsGreaterThan(PinnedEngineReferences.Length)
            .Because(
                "The carrier set includes Harbor.Abstractions.Contracts, which the engine reaches only "
                + "through Harbor.Ui.Framework.Rendering. If the walk stopped at the direct references, "
                + "that carrier would silently vanish from the closure and the pin would stop measuring "
                + "the third path it exists to catch.");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>Build output is never source and never a project.</summary>
    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>
    ///     Every project reachable from <paramref name="startProject"/> through
    ///     <c>&lt;ProjectReference&gt;</c> edges, breadth-first, excluding the start
    ///     project itself. The walk is what turns "the two references #435 deletes" into
    ///     the closure those two references actually reach.
    /// </summary>
    private static string[] ReachableProjects(string startProject)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { startProject };
        var queue = new Queue<string>();
        queue.Enqueue(startProject);

        while (queue.Count > 0)
        {
            string? csproj = RepoPaths.FindSrcProject(queue.Dequeue());
            if (csproj is null)
            {
                continue;
            }

            foreach (string reference in RepoPaths.ReadProjectReferences(csproj).References)
            {
                if (seen.Add(reference))
                {
                    queue.Enqueue(reference);
                }
            }
        }

        seen.Remove(startProject);
        return [.. seen];
    }

    /// <summary>Does this project's csproj declare a direct CSE <c>PackageReference</c>?</summary>
    private static bool DeclaresCse(string project) =>
        RepoPaths.FindSrcProject(project) is { } csproj
        && ReadPackageReferences(csproj).Contains(CsePackage, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     The <c>Include</c> id of every <c>&lt;PackageReference&gt;</c> a csproj declares,
    ///     version stripped (versions live in Directory.Packages.props under CPM).
    /// </summary>
    private static string[] ReadPackageReferences(string csprojPath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(csprojPath, LoadOptions.None);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return [];
        }

        return [.. document.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)];
    }

    /// <summary>Repo-relative, forward-slashed path for stable failure messages.</summary>
    private static string Relative(string absolutePath) =>
        (RepoPaths.RepoRoot is null ? absolutePath : Path.GetRelativePath(RepoPaths.RepoRoot, absolutePath))
        .Replace('\\', '/');
}
