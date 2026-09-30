// SharedSourceLinkRules.cs — GUARD for issue #456.
//
// WHAT #456 NAMED
// ---------------
// Two `src/` folders hold shared source and produce no assembly: their files are
// `<Compile Include>`-linked into consumer projects instead of being referenced.
//
//   src/Harbor.Providers.Shared/  SsePump.cs, OpenAiWire.cs, OpenAiImageContent.cs
//   src/Harbor.Storage.Shared/     SessionLockStrip.cs, SessionStoreErrors.cs,
//                                  SessionStatsAggregator.cs
//
// `FullLayerMatrixTests.SharedSourceFolders` listed only the first, and the
// comment above it claimed the list "can no longer grow silently" because
// `EnforcerIntegrityTests.SrcProjects_AreAllClassified` fails on an unlisted
// project. That claim was half true, and the false half is the defect:
//
//   * the coverage check iterates `RepoPaths.EnumerateSrcProjects()`, which
//     returns only directories that HAVE a `*.csproj`. A csproj-less folder is
//     not an entry, so it is never examined — not for a matrix row, not for the
//     out-of-scope list, not for anything. `Harbor.Storage.Shared` was already
//     sitting there, unlisted, and the gate was green.
//   * a NEW shared-source folder, or a new file added to a listed one, is the
//     same story: nothing reads the folder, so nothing notices.
//
// WHY THAT IS A BYPASS AND NOT A COVERAGE GAP
// --------------------------------------------
// The tempting reading is "the enforcer does not check this code". That is
// wrong, and the distinction decides the fix. Linked source is COMPILED INTO the
// consumer, so its `using` directives become real AssemblyRefs of that
// consumer's assembly. `FullLayerMatrixTests.EverySrcAssembly_ReferenceSet_MatchesMatrix`
// reads the compiled reference set, so a forbidden dependency written into a
// shared file surfaces as an unexpected edge on the consumer and IS caught. The
// layering rules see the code.
//
// What the rules could NOT see is anything keyed on the FILE SET of one project.
// `RepoPaths.EnumerateCsFiles(projectDir)` walked `src/<projectDir>/**` only, so
// for a linked file it reported "this project has no such file" — and a rule that
// asks "which files bind the forbidden target?" got an answer missing the shared
// ones. The worst consequence is `EnforcerIntegrityTests.DocumentExceptions_AreScopedToNamedFiles`:
// an exception is file-scoped precisely so it cannot absorb new violations, and a
// violating file added to the shared folder would be invisible to it. Green there
// meant unchecked, which is worse than no check because it looks like a pass.
//
// So the fix is the narrow one: make the per-project file walk resolve the link
// items, declare BOTH folders with a reason, and hold that declaration to the
// csprojs in both directions. No file moves, no assembly is invented, and the
// build graph is untouched.
//
// THE ANSWER TO "ENFORCE, OR STOP LINKING?"
// -------------------------------------------
// Enforce. The alternative is a real `Harbor.Providers.Shared` /
// `Harbor.Storage.Shared` assembly, and that is not a mechanical follow-up to
// #456: it is a change to the layer model. These folders exist BECAUSE the matrix
// forbids Infrastructure→Infrastructure project references
// (`NetArchLayerRules.ForbiddenForInfrastructure` names all four providers and all
// three storage backends). Promoting a shared folder to an assembly means an
// Infrastructure assembly referenced by other Infrastructure assemblies, i.e.
// widening the very rule the linked-source mechanism works around — and the
// types are `internal` in every consumer today, so a real assembly would also
// force a public-surface decision on code nobody outside the family can name.
// That is a separate decision with its own trade-offs, and it belongs on its own
// issue rather than being smuggled in as the "thorough" half of a docs fix.
//
// WHAT IS RULED
// -------------
//   R1  Every csproj-less directory under a product tree that contains `*.cs`
//       MUST be declared in `FullLayerMatrixTests.SharedSourceFolders`. This is
//       the rule the old comment asserted and the old check did not perform.
//   R2  Every `<Compile Include>` link item must resolve into a DECLARED folder,
//       at a file that exists. A link into an undeclared folder re-opens the
//       bypass; a link to a missing file is dead configuration.
//   R3  Every declared folder has at least one consumer, and every file in it is
//       linked by at least one project. A folder nobody compiles is not shared
//       source but a second copy of nothing; a file no project links is code the
//       compiler never reads — the same "green = unchecked" shape one level down.
//   R4  Every declared folder states a reason, through `ExemptionReason`, so these
//       rows are held to the same bar as every other permission table here.
//   R5  `RepoPaths.EnumerateCsFiles` actually returns the linked files. This is
//       the bypass itself, asserted directly, because a fix nothing tests is a
//       fix that can be undone silently.
//
// NON-VACUITY
// -----------
// R1–R3 cross-check the real csproj XML, so they cannot pass by reading nothing.
// `The_Shared_Folder_Discovery_Is_Not_Empty` and `The_Link_Inventory_Is_Not_Empty`
// pin the discovered sets to the two folders and thirteen link items this issue is
// about, so a rename, a moved folder or an emptied set fails rather than quietly
// reducing the rules to nothing. R5 is paired with a negative control: the same
// walk must NOT return a shared file for a project that does not link it, so R5
// cannot be satisfied by enumerating every shared file unconditionally.

namespace Harbor.Architecture.Tests;

/// <summary>
///     Makes shared source — code compiled into a project from a csproj-less folder via
///     <c>&lt;Compile Include&gt;</c> — visible to the rules that key on a project's file
///     set, and keeps the declaration of such folders honest (#456).
/// </summary>
public sealed class SharedSourceLinkRules
{
    /// <summary>
    ///     The source trees scanned for shared-source folders. Only <c>src/</c> holds them
    ///     today; <c>apps/</c> is included because a composition root may legitimately need
    ///     the same mechanism, and a rule that only knew <c>src/</c> would be blind to the
    ///     first one that appeared there.
    /// </summary>
    private static readonly string[] SharedSourceTrees = ["src", "apps"];

    // ---- R1: the declaration is total ---------------------------------------

    /// <summary>
    ///     R1 — every csproj-less source directory is declared, with a reason.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the check <c>SharedSourceFolders</c> claimed to have and did not. The
    ///         existing coverage guard iterates the projects that HAVE a csproj, so this
    ///         population was never enumerated: <c>Harbor.Storage.Shared</c> was undeclared
    ///         and the gate was green. Nothing about the layer matrix changes here — these
    ///         folders still produce no assembly and still have no layer edge. What changes is
    ///         that the "no csproj" case is an explicit, checked statement, as the comment
    ///         claimed it already was.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task Every_Csproj_Less_Source_Folder_Is_Declared()
    {
        var undeclared = DiscoverSharedSourceFolders()
            .Where(folder => !FullLayerMatrixTests.SharedSourceFolders.ContainsKey(folder))
            .Select(folder =>
                $"src/{folder}: holds .cs files but no .csproj, and is in neither "
                + "FullLayerMatrixTests.Matrix nor OutOfScopeAssemblies nor SharedSourceFolders. "
                + "Nothing in the enforcer reads such a folder: the coverage guard iterates "
                + "projects that HAVE a csproj, so this directory is not an entry it can check, "
                + "and a rule that walks a project's file set cannot see the code that compiles "
                + "into it. Declare it in SharedSourceFolders with a reason, or give it a csproj "
                + "and a matrix row.")
            .ToList();

        await Assert.That(undeclared).IsEmpty()
            .Because(string.Join("\n", undeclared));
    }

    /// <summary>
    ///     R1 non-vacuity — the discovery found the folders this rule is about. A walker
    ///     rooted wrong yields an empty set and R1 passes over nothing.
    /// </summary>
    [Test]
    public async Task The_Shared_Folder_Discovery_Is_Not_Empty()
    {
        string[] discovered = DiscoverSharedSourceFolders();

        await Assert.That(discovered).IsEquivalentTo(new[]
        {
            "Harbor.Providers.Shared", "Harbor.Storage.Shared",
        }).Because(
            "Those are the only two csproj-less source folders, and both hold linked source. A "
            + "third entry means a folder was added — which R1 must then judge — and a shorter "
            + "list means one was renamed or removed, so every R1 verdict was computed over the "
            + "wrong set. Found: " + string.Join(", ", discovered));
    }

    // ---- R2: links resolve into declared folders ---------------------------

    /// <summary>
    ///     R2 — every link item points into a declared folder, at a file that exists.
    /// </summary>
    [Test]
    public async Task Every_Link_Item_Resolves_Into_A_Declared_Folder()
    {
        var failures = new List<string>();

        foreach (LinkItem link in EnumerateLinkItems())
        {
            if (!FullLayerMatrixTests.SharedSourceFolders.ContainsKey(link.SharedFolder))
            {
                failures.Add(
                    $"{link.Consumer}.csproj links '{link.Include}' -> src/{link.SharedFolder}, "
                    + "which is not a declared shared-source folder. The code will compile into "
                    + "this project, and the rules that key on the project's own file set cannot "
                    + "see it — the #456 bypass, re-opened one csproj edit later. Declare the "
                    + "folder with a reason, or drop the link.");
                continue;
            }

            if (!File.Exists(link.Resolved))
            {
                failures.Add(
                    $"{link.Consumer}.csproj links '{link.Include}' -> {link.Resolved}, which does "
                    + "not exist. MSBuild rejects the build, but the declaration would otherwise "
                    + "claim a consumer for a file that is not there.");
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     R2 non-vacuity — the link inventory is real, and its shape is the one #456 measured.
    ///     Counts are pinned rather than merely "greater than zero" so an emptied set, a renamed
    ///     file and a dropped consumer all fail here instead of quietly emptying the rule.
    /// </summary>
    [Test]
    public async Task The_Link_Inventory_Is_Not_Empty()
    {
        LinkItem[] links = EnumerateLinkItems();

        await Assert.That(links.Length).IsEqualTo(13)
            .Because(
                "13 `<Compile Include>` link items: SsePump into four providers, OpenAiWire and "
                + "OpenAiImageContent into OpenAI + OpenAiCompatible, and three files out of "
                + "Harbor.Storage.Shared into the Jsonl and Sqlite stores. A different count means "
                + "a link was added or removed and R2 is judging a set that is not the build's.");

        string[] consumers = [.. links.Select(l => l.Consumer).Distinct(StringComparer.Ordinal)];

        await Assert.That(consumers).IsEquivalentTo(new[]
        {
            "Harbor.Providers.Anthropic", "Harbor.Providers.Ollama",
            "Harbor.Providers.OpenAI", "Harbor.Providers.OpenAiCompatible",
            "Harbor.Storage.Jsonl", "Harbor.Storage.Sqlite",
        })
            .Because(
                "Four providers and two storage backends link shared source. Harbor.Storage.Memory "
                + "does not — it has no Compile item at all, which is why it hand-writes the "
                + "SessionStoreErrors literals eleven times instead of calling them (nine "
                + "SessionNotFound, two MessageNotFound — #764 inventoried only the first nine). A "
                + "new or removed consumer must be a deliberate edit to the declaration in "
                + "FullLayerMatrixTests, not a silent csproj change.");
    }

    // ---- R3: the declaration matches the build -----------------------------

    /// <summary>
    ///     R3 — every file in a declared folder is linked by at least one project. A file no
    ///     project links is code the compiler never reads: it looks reviewed, it is in the tree,
    ///     and it enforces nothing.
    /// </summary>
    [Test]
    public async Task Every_Declared_Shared_File_Is_Linked_By_At_Least_One_Project()
    {
        var linked = EnumerateLinkItems()
            .Select(l => l.Resolved)
            .ToHashSet(StringComparer.Ordinal);

        var failures = new List<string>();

        foreach (string folder in FullLayerMatrixTests.SharedSourceFolders.Keys)
        {
            string? dir = RepoPaths.FindProjectDir(folder);
            if (dir is null)
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.TopDirectoryOnly)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!linked.Contains(Path.GetFullPath(file)))
                {
                    failures.Add(
                        $"src/{folder}/{Path.GetFileName(file)}: no project's csproj links it, so no "
                        + "compiler reads it and no rule can judge it — a file that exists, looks "
                        + "maintained, and is dead. Link it from its consumers or delete it.");
                }
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     R3b — a declared folder is not a graveyard: at least one project links something
    ///     from it. A csproj whose Compile item was deleted still compiles, so nothing else
    ///     would notice the folder had become dead.
    /// </summary>
    [Test]
    public async Task Every_Declared_Folder_Has_At_Least_One_Consumer()
    {
        var consumed = EnumerateLinkItems()
            .Select(l => l.SharedFolder)
            .ToHashSet(StringComparer.Ordinal);

        List<string> failures =
        [
            .. FullLayerMatrixTests.SharedSourceFolders.Keys
                .Where(folder => !consumed.Contains(folder))
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(folder =>
                    $"src/{folder}: declared as shared source but no project links any file from "
                    + "it. Either it is dead (delete the folder and its declaration) or its "
                    + "consumers lost their Compile item, which the build does not catch — a csproj "
                    + "without one still compiles.")
        ];

        await Assert.That(failures).IsEmpty()
            .Because(string.Join("\n", failures));
    }

    // ---- R4: the reason ----------------------------------------------------

    /// <summary>
    ///     R4 — the reasons meet the same bar as every other permission table. Routed through
    ///     <see cref="ExemptionReason" /> rather than a fourth copy of "is this blank?".
    /// </summary>
    [Test]
    public async Task Every_Declared_Folder_States_Why_It_Exists()
    {
        var failures = ExemptionReason.RowsWithoutAReason(
            "FullLayerMatrixTests.SharedSourceFolders",
            FullLayerMatrixTests.SharedSourceFolders.Select(
                static entry => (Key: entry.Key, Row: new ExemptionReason.Row(entry.Value, null))));

        await Assert.That(failures).IsEmpty()
            .Because(string.Join("\n", failures));
    }

    // ---- R5: the walk itself sees the linked files --------------------------

    /// <summary>
    ///     R5 — <see cref="RepoPaths.EnumerateCsFiles" /> returns the linked files, which is
    ///     the bypass this issue is about, asserted directly against real files.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A fix nothing tests is a fix that can be undone silently: someone tidying the
    ///         walker back to a plain <c>Directory.GetFiles(projectDir)</c> would restore the
    ///         bypass with no other test going red. This pins the observable behaviour the
    ///         file-scoped rules depend on.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task The_Per_Project_File_Walk_Returns_The_Linked_Shared_Files()
    {
        var missing = new List<string>();

        foreach (LinkItem link in EnumerateLinkItems())
        {
            var files = RepoPaths.EnumerateCsFiles(link.Consumer);
            if (!files.Contains(link.Resolved, StringComparer.Ordinal))
            {
                missing.Add(
                    $"src/{link.SharedFolder}/{Path.GetFileName(link.Resolved)}: linked into "
                    + $"{link.Consumer}, but RepoPaths.EnumerateCsFiles(\"{link.Consumer}\") does not "
                    + "return it. Every rule that asks which files of a project bind something is "
                    + "reading an incomplete set — the #456 bypass, in the form it actually took.");
            }
        }

        await Assert.That(missing).IsEmpty()
            .Because(string.Join("\n", missing));
    }

    /// <summary>
    ///     R5 negative control — the walk must not hand a shared file to a project that does
    ///     not link it, so R5 above cannot be satisfied by enumerating every shared file
    ///     unconditionally.
    /// </summary>
    [Test]
    public async Task The_Per_Project_File_Walk_Does_Not_Invent_Links()
    {
        // Harbor.Storage.Memory is the control precisely because it links NEITHER shared
        // storage file: a walk that resolved every shared file for every storage project
        // would pass R5 above while quietly giving Memory code it does not compile.
        string[] memory = [.. RepoPaths.EnumerateCsFiles("Harbor.Storage.Memory")];

        await Assert.That(memory.Length).IsGreaterThan(0)
            .Because(
                "Harbor.Storage.Memory has its own sources; an empty set means the walk is not "
                + "reading this project at all and the negative control below proves nothing.");

        var shared = EnumerateLinkItems()
            .Where(l => l.SharedFolder == "Harbor.Storage.Shared")
            .Select(l => l.Resolved)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var invented = memory.Where(shared.Contains).ToList();

        await Assert.That(invented).IsEmpty()
            .Because(
                "Harbor.Storage.Memory declares no Compile item, so no file from "
                + "src/Harbor.Storage.Shared is compiled into it. A walk that reported one anyway "
                + "would make every file-set rule judge Memory on code it does not contain: "
                + string.Join(", ", invented.Select(Path.GetFileName)));
    }

    // ---- discovery ---------------------------------------------------------

    /// <summary>
    ///     One <c>&lt;Compile Include&gt;</c> link item that points outside its project.
    /// </summary>
    /// <param name="Consumer">The linking project directory.</param>
    /// <param name="Include">The include path, verbatim from the csproj.</param>
    /// <param name="Resolved">The absolute path the include resolves to.</param>
    /// <param name="SharedFolder">The project-relative folder the resolved path lands in.</param>
    private sealed record LinkItem(string Consumer, string Include, string Resolved, string SharedFolder);

    /// <summary>
    ///     Every directory under a product tree that holds <c>*.cs</c> and no <c>*.csproj</c> —
    ///     the population <c>EnumerateSrcProjects</c> cannot see, which is the whole point.
    /// </summary>
    private static string[] DiscoverSharedSourceFolders()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var result = new List<string>();

        foreach (string tree in SharedSourceTrees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            // Direct children only. A nested folder inside a project
            // (src/Harbor.Tui.CellForge/Chat, src/Harbor.Storage.Shared/whatever) inherits its
            // ancestor's project, and treating each as a shared-source folder would report ~100
            // phantom violations on the first run.
            foreach (string child in Directory.GetDirectories(dir))
            {
                if (Directory.GetFiles(child, "*.csproj").Length > 0)
                {
                    continue;
                }

                bool hasSource = Directory
                    .EnumerateFiles(child, "*.cs", SearchOption.AllDirectories)
                    .Any(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                              && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

                if (hasSource)
                {
                    result.Add(Path.GetFileName(child));
                }
            }
        }

        return [.. result.OrderBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>
    ///     Every <c>&lt;Compile Include&gt;</c> item in a src csproj that points outside the
    ///     project directory — the compiler's own view of what travels where.
    /// </summary>
    private static LinkItem[] EnumerateLinkItems()
    {
        if (RepoPaths.RepoRoot is null)
        {
            return [];
        }

        var result = new List<LinkItem>();

        foreach (string csproj in RepoPaths.EnumerateSrcProjects())
        {
            string consumer = Path.GetFileName(Path.GetDirectoryName(csproj)!);
            string projectRoot = Path.GetFullPath(Path.Combine(RepoPaths.RepoRoot, "src", consumer));

            foreach (string include in RepoPaths.ReadCompileIncludes(csproj))
            {
                string resolved = Path.GetFullPath(
                    Path.Combine(projectRoot, include.Replace('\\', Path.DirectorySeparatorChar)));

                if (!resolved.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    result.Add(new LinkItem(consumer, include, resolved, Path.GetFileName(Path.GetDirectoryName(resolved)!)));
                }
            }
        }

        return [.. result];
    }
}
