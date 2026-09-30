// ProjectKeyedScanRule.cs — a MEASUREMENT, not a fix: which directories under
// `src/` hold compiled product code and are not a project, because they have no
// `.csproj` to be found by.
//
// THE SECOND "CANNOT SEE", AND WHY IT IS A DIFFERENT THING
// -------------------------------------------------------
// #877 is about a measurement that cannot see its subject. `ScanVisibilityRule.cs`
// covers one form of that: a path that IS walked and then rejected by
// `SourceScan.IsBuildOutput`. This file covers the other form, and the two are
// deliberately not merged, because they fail in opposite directions and no single
// edit fixes both.
//
//   * A PATH FILTER is a verdict. The file exists, the walk reaches it, and a
//     predicate says "not product code" and drops it. The name is still knowable —
//     you had to have it to ask. `ScanVisibilityRule.cs` measures those.
//   * AN ABSENT PROJECT FILE is a key that is never generated. `RepoPaths`
//     discovers projects by globbing `src/**/*.csproj`. A directory with no csproj
//     is not a project to that glob, not a degraded one, not a special case — it is
//     simply not in the set of things the set is made of. Nothing enumerates it, so
//     no rule that iterates projects ever visits it, and the only way to name it is
//     to already know its name.
//
// `src/Harbor.Storage.Shared` and `src/Harbor.Providers.Shared` are the live
// instances, and the measurement is two folders and six files: three compiled into
// the four provider clients, three into the two storage stores. Neither is in
// `RepoPaths.EnumerateSrcProjects()`. Neither has an entry in
// `RepoPaths.EnumerateRepoAssemblyNames()`. Both are reachable by exactly one
// route: as linked source inside a real project, via `<Compile Include>` link items
// (#456, #763) — which is how `RepoPaths.EnumerateCsFiles` finds them, and how
// `SharedSourceLinkRules` holds them.
//
// WHY THIS IS NOT A DUPLICATE OF `SharedSourceLinkRules`
// -----------------------------------------------------
// `SharedSourceLinkRules` owns the MANIFEST side: that a csproj-less folder is
// declared, that every link item resolves into a declared folder, that
// `Harbor.Storage.Memory` links nothing. That is thorough and it is not what this
// file measures. This file measures the ENUMERATION side: the fact that these
// directories are not keys. A folder can pass every manifest rule and still be
// invisible to every project-keyed walk, because the manifest says what the folder
// CONTAINS while the enumeration says what the folder IS — and "is" is answered by a
// file that is not there. Add a fourth shared-source folder with a correct
// declaration and zero link items: `SharedSourceLinkRules` has nothing to say
// (nothing resolves, nothing dangles), and this rule's ratchet is what notices that
// the new directory's code is now in no assembly at all.
//
// The consequence worth stating: for a csproj-less folder, `EnumerateCsFiles(dir)`
// still returns its files, because that method takes a NAME and does not consult
// the project set. So the folder is visible to a caller who already knows its name
// and invisible to every caller who is looking for something. That asymmetry is the
// defect. An honest API would say which of the two it is doing.
//
// WHAT THIS DOES NOT CLAIM
// ------------------------
// Not that the two folders are wrong. Both are deliberate: `Harbor.Storage.Shared`'s
// own README gives the reason (a real project reference between storage assemblies
// would violate `Storage_ReferencesOnlyAbstractions`), and the linked-source shape
// is load-bearing for `Harbor.Providers.Shared`. This rule condemns nothing and
// demands nothing. What it can demand is that the set does not grow, and that every
// member of it is reachable from a real project — because a folder that is neither a
// project nor compiled into one is DEAD CODE, and that is a defect loud enough to
// deserve its own issue rather than a ratchet row.
//
// NON-VACUITY
// -----------
//   1. TheProjectSetIsNotEmpty — `EnumerateSrcProjects()` returned something. If the
//      project glob broke, every folder would classify as csproj-less and this rule
//      would report a hole that does not exist.
//   2. AProjectWithACsproj_IsInTheProjectSet — the control. A directory that has a
//      csproj must appear, so a classifier that answered "csproj-less" for everything
//      fails here instead of quietly inflating the measurement.
//   3. EveryCsprojLessFolder_IsCompiledIntoSomeProject — the dead-code check. An
//      empty linked-by list is not a row in a ratchet, it is a defect, and the
//      message says so in those words rather than reporting it as a stable state.
//
// The baseline ships EMPTY on purpose. The red run is the measurement.

namespace Harbor.Architecture.Tests;

/// <summary>One <c>src/</c> directory holding <c>.cs</c> files and no <c>.csproj</c>.</summary>
/// <param name="Name">Directory name, relative to <c>src/</c>.</param>
/// <param name="FileCount">How many <c>*.cs</c> files a neutral walk found in it.</param>
/// <param name="CompiledInto">Projects whose csproj links a file from it, sorted.</param>
internal sealed record CsprojLessFolder(string Name, int FileCount, IReadOnlyList<string> CompiledInto);

/// <summary>Finds the <c>src/</c> directories that are not projects.</summary>
internal static class ProjectKeyedScanProbe
{
    /// <summary>
    ///     Every <c>src/&lt;dir&gt;</c> holding at least one <c>*.cs</c> file and no
    ///     <c>*.csproj</c>, with the projects that compile it.
    /// </summary>
    /// <remarks>
    ///     The <c>.cs</c> count comes from <see cref="ScanVisibilityProbe.CsFiles" />, the
    ///     neutral walk, deliberately: <c>RepoPaths.EnumerateCsFiles</c> is not used to decide
    ///     whether a folder exists, because using the project-keyed helper to classify the
    ///     project-keyed holes would make the measurement agree with whatever it is auditing.
    /// </remarks>
    internal static IReadOnlyList<CsprojLessFolder> Measure(string root)
    {
        string src = Path.Combine(root, "src");
        if (!Directory.Exists(src))
        {
            return [];
        }

        // Which real projects link into which shared folder, computed once.
        var linkedBy = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (string csproj in RepoPaths.EnumerateSrcProjects())
        {
            string projectDir = Path.GetFileName(Path.GetDirectoryName(csproj)!);
            foreach (string linked in RepoPaths.EnumerateLinkedSourceFiles(projectDir))
            {
                string? shared = SharedFolderOf(root, linked);
                if (shared is null)
                {
                    continue;
                }

                if (!linkedBy.TryGetValue(shared, out SortedSet<string>? projects))
                {
                    projects = new SortedSet<string>(StringComparer.Ordinal);
                    linkedBy[shared] = projects;
                }

                projects.Add(projectDir);
            }
        }

        var found = new List<CsprojLessFolder>();
        foreach (string directory in Directory.GetDirectories(src))
        {
            string name = Path.GetFileName(directory);
            if (Directory.EnumerateFiles(directory, "*.csproj").Any())
            {
                continue;
            }

            int fileCount = ScanVisibilityProbe.CsFiles(directory).Count;
            if (fileCount == 0)
            {
                continue;
            }

            string[] consumers = linkedBy.TryGetValue(name, out SortedSet<string>? projects)
                ? [.. projects]
                : [];

            found.Add(new CsprojLessFolder(name, fileCount, consumers));
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return found;
    }

    /// <summary>
    ///     The <c>src/&lt;folder&gt;</c> a linked file lives in, or <c>null</c> if it is not
    ///     one of the shared-source folders.
    /// </summary>
    /// <remarks>
    ///     Matched on the full path prefix rather than on the first segment. #863 had to key
    ///     its boundary on the first path segment because <c>ProjectOf</c> returns "apps" for
    ///     <c>contrib/apps/...</c> and would file every contrib file under the product tree.
    ///     That ambiguity does not arise here — every folder under test is directly under
    ///     <c>src/</c>, so there is no nested prefix to confuse. The precaution is noted
    ///     because it is cheap and its absence is silent.
    /// </remarks>
    private static string? SharedFolderOf(string root, string linkedFile)
    {
        string srcPrefix = Path.GetFullPath(Path.Combine(root, "src")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(linkedFile).StartsWith(srcPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string relative = Path.GetFullPath(linkedFile)[srcPrefix.Length..];
        int slash = relative.IndexOf(Path.DirectorySeparatorChar);
        return slash < 0 ? null : relative[..slash];
    }
}

/// <summary>
///     Pins the <c>src/</c> directories that hold compiled product code without being a
///     project. The blind spot here is a missing key, not a rejected path — see the header.
/// </summary>
public sealed class ProjectKeyedScanRule
{
    private static readonly Lazy<IReadOnlyList<CsprojLessFolder>> Report = new(() =>
        RepoPaths.RepoRoot is { } root ? ProjectKeyedScanProbe.Measure(root) : []);

    /// <summary>
    ///     The csproj-less <c>src/</c> directories. MEASURED, not assumed: two folders, six
    ///     files. See the red run quoted in the PR body (run 36734067195, job 109951166971).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A set of names, not a count, for the reason <c>ScanVisibilityRule</c> gives: a count
    ///         can hold steady while the hole changes shape, and only a name has to be deleted on
    ///         purpose.
    ///     </para>
    ///     <para>
    ///         The measured rows, verbatim from that run:
    ///         <c>src/Harbor.Providers.Shared</c> — 3 files, compiled into
    ///         <c>Harbor.Providers.Anthropic + Ollama + OpenAI + OpenAiCompatible</c>;
    ///         <c>src/Harbor.Storage.Shared</c> — 3 files, compiled into
    ///         <c>Harbor.Storage.Jsonl + Sqlite</c>.
    ///     </para>
    ///     <para>
    ///         The row count is the point, not a detail. <c>Harbor.Storage.Shared</c> holds three
    ///         files and two of its three consumers take two of them: <c>SessionStatsAggregator.cs</c>
    ///         is linked into <c>Jsonl</c> and not into <c>Sqlite</c>. So "which files does this
    ///         shared folder contribute" is not one question with one answer, it is one question
    ///         per consumer, and <c>EveryCsprojLessFolder_IsCompiledIntoSomeProject</c> is
    ///         deliberately the weaker assertion it is — non-empty, not equal. Deciding whether
    ///         that asymmetry is intended is a judgement about the two stores, and it belongs to
    ///         whoever owns them; this rule only refuses to let a folder become an orphan.
    ///     </para>
    /// </remarks>
    private static readonly string[] MeasuredCsprojLessFolders =
        ["Harbor.Providers.Shared", "Harbor.Storage.Shared"];

    /// <summary>A directory that certainly has a csproj, for the positive control.</summary>
    private const string ControlProjectWithACsproj = "Harbor.Abstractions";

    // =====================================================================
    // 1. The measurement.
    // =====================================================================

    /// <summary>
    ///     The csproj-less <c>src/</c> directories must equal
    ///     <see cref="MeasuredCsprojLessFolders" />.
    /// </summary>
    [Test]
    public async Task CsprojLessFolders_MatchTheMeasuredBaseline()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("this rule measures which src/ directories are not projects, and without a "
                   + "checkout there are none — an empty report would satisfy the baseline for "
                   + "the wrong reason");

        var live = Report.Value.Select(f => f.Name).ToArray();

        await Assert.That(string.Join(" | ", live))
            .IsEqualTo(string.Join(" | ", MeasuredCsprojLessFolders))
            .Because(
                "RepoPaths discovers projects by globbing src/**/*.csproj, so a src/ directory "
                + "with no csproj is not a project to that glob in any sense — it is not in the "
                + "set the set is made of, and no rule that iterates projects ever visits it. That "
                + "is a different blindness from ScanVisibilityRule's, which measures a path that "
                + "IS walked and then rejected: here nothing is walked, because nothing yields the "
                + "name. The honest invariant is that this set does not grow, since a new "
                + "csproj-less directory with no link items would be code in no assembly at all. "
                + "Live folders (" + live.Length + "): " + Describe()
                + " | baseline held " + MeasuredCsprojLessFolders.Length + " name(s): "
                + (MeasuredCsprojLessFolders.Length == 0 ? "(empty)" : string.Join(" | ", MeasuredCsprojLessFolders)));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The project set is non-empty, so "csproj-less" means something. If the project glob
    ///     broke, every folder would classify as csproj-less and this rule would be reporting a
    ///     hole that is not there.
    /// </summary>
    [Test]
    public async Task TheProjectSetIsNotEmpty()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the project set is what this rule measures against, and there is none "
                   + "outside a checkout");

        await Assert.That(RepoPaths.EnumerateSrcProjects().Count).IsGreaterThan(0)
            .Because(
                "EnumerateSrcProjects globs src/**/*.csproj and is the key set for every "
                + "project-keyed walk in this project. If it returns nothing, every directory "
                + "under src/ looks csproj-less, the ratchet above would be holding a baseline "
                + "against a broken glob rather than against a real hole, and the measurement "
                + "would be reporting a defect in the measuring instrument. Measured: "
                + Report.Value.Count + " csproj-less folder(s): " + Describe());
    }

    /// <summary>
    ///     The positive control: a directory that HAS a csproj is in the project set, and is
    ///     not classified as csproj-less.
    /// </summary>
    /// <remarks>
    ///     Without this, a classifier that answered "csproj-less" for everything would satisfy
    ///     both the ratchet and the glob check above, and report a repository in which no
    ///     project has a project file. The control is what makes the measurement's two
    ///     categories distinguishable rather than merely different labels for one answer.
    /// </remarks>
    [Test]
    public async Task AProjectWithACsproj_IsInTheProjectSet()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the control needs a checkout to have a project set at all");

        string? csproj = RepoPaths.FindSrcProject(ControlProjectWithACsproj);
        await Assert.That(csproj).IsNotNull()
            .Because(
                "src/" + ControlProjectWithACsproj + " is the control: a directory that certainly "
                + "has a csproj. If it is not in the project set, then FindSrcProject and "
                + "EnumerateSrcProjects disagree with the filesystem, and every assertion in this "
                + "file that depends on 'has a csproj' means nothing");

        await Assert.That(Report.Value.Any(f => f.Name == ControlProjectWithACsproj)).IsFalse()
            .Because(
                ControlProjectWithACsproj + " has a csproj, so classifying it as csproj-less means "
                + "the classifier is not reading csproj files at all — and a rule that audits the "
                + "project-keyed holes while miscounting the projects is worse than no rule. "
                + "Classified as csproj-less: " + Describe());
    }

    /// <summary>
    ///     Every csproj-less folder is compiled into at least one real project. One with no
    ///     consumer is not a ratchet row — it is dead code.
    /// </summary>
    [Test]
    public async Task EveryCsprojLessFolder_IsCompiledIntoSomeProject()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("this assertion is about which projects compile which files, and there are "
                   + "no projects outside a checkout");

        var orphans = Report.Value
            .Where(f => f.CompiledInto.Count == 0)
            .Select(f => f.Name + " (" + f.FileCount + " .cs file(s))")
            .ToArray();

        await Assert.That(string.Join(" | ", orphans))
            .IsEqualTo(string.Empty)
            .Because(
                "a src/ directory with no csproj is only reachable through <Compile Include> link "
                + "items in a real project. If nothing links it, its files are not in any assembly: "
                + "not dead to the compiler, which never sees them, and not dead to a reader, who "
                + "finds them under src/ looking exactly like product code. That is DEAD CODE, not "
                + "a stable measurement, and it wants its own issue rather than a row in this "
                + "ratchet. Orphans: " + (orphans.Length == 0 ? "(none)" : string.Join(" | ", orphans))
                + " | every folder: " + Describe());
    }

    private static string Describe() =>
        Report.Value.Count == 0
            ? "(none)"
            : string.Join(
                " | ",
                Report.Value.Select(f => "src/" + f.Name + ": " + f.FileCount + " .cs file(s), compiled into "
                                        + (f.CompiledInto.Count == 0 ? "NOTHING" : string.Join("+", f.CompiledInto))));
}
