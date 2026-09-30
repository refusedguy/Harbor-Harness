// AbstractionsNamespaceOwnershipRules.cs — GUARD for issue #452.
//
// THE DEFECT THIS GUARDS
// ----------------------
// The layers are declared but the NAMES do not reflect them. A type that lives
// in Harbor.Registries (an Application-layer assembly, see
// FullLayerMatrixTests.cs:404) was declared in a namespace that READS like a
// contract:
//
//   src/Harbor.Registries/Agents/AgentRegistry.cs     ->  Harbor.Abstractions.Agents
//   src/Harbor.Registries/Events/InMemoryEventBus.cs  ->  Harbor.Abstractions.Events
//   src/Harbor.Registries/Providers/ProviderRegistry.cs -> Harbor.Abstractions.Providers
//   src/Harbor.Registries/Tools/ToolRegistry.cs        ->  Harbor.Abstractions.Tools
//
// So `Harbor.Abstractions.Providers` was declared by TWO assemblies at once:
// `IAgentRegistry`/`IProviderRegistry`/`ITool`/`IEventBus` in src/Harbor.Abstractions
// (the Domain facade) AND the concrete `ProviderRegistry`/`ToolRegistry` in
// src/Harbor.Registries (Application). One namespace, two layers. A consumer
// writing `using Harbor.Abstractions.Providers;` binds BOTH the contract and
// the implementation and cannot tell, by reading, which side of the pyramid the
// type it just named came from — which is the whole job the namespace is
// supposed to do.
//
// This was already half-migrated when #452 was written. Nine files live in
// src/Harbor.Registries; FIVE had already moved to the owning root — the event
// middlewares to Harbor.Registries.Events, and CompositeToolRegistry /
// FrozenToolView / InMemoryMcpRegistry to Harbor.Registries.Tools — and FOUR
// had not. The audit recorded six; the nightly
// assembly moves resolved two of them before this guard landed. This rule
// finishes the wave the repo had already started rather than inventing a
// convention.
//
// THE RULE, IN FULL
// -----------------
//   R1  The `Harbor.Abstractions` namespace root is OWNED by the ADR-007
//       contract trio — Harbor.Abstractions, Harbor.Abstractions.Contracts and
//       Harbor.Extensions. No other assembly may DECLARE a namespace under it.
//       (ADR-007 deliberately keeps the pre-split namespaces so the split cost
//       consumers zero `using` changes; that exception is scoped to the trio
//       and is the ONLY exception.)
//
//   R2  src/Harbor.Registries declares every namespace under its own root. An
//       Application assembly must not borrow a namespace root that belongs to
//       another layer, and `Harbor.Registries` is the one project #452 is about.
//
//   R3  Every namespace declaration in the product tree is attributed to at
//       least one assembly. R1 and R2 are only as strong as the attribution
//       behind them: a declaration the walk cannot hand to an assembly is a
//       declaration no rule in this file judges, and a rule that silently
//       skips is indistinguishable from a rule that passes (#763).
//
// WHY THE RULE IS ABOUT ROOTS AND NOT ABOUT "namespace == assembly name"
// ----------------------------------------------------------------------
// The obvious stronger rule — "a namespace must be rooted at its assembly's own
// name" — is WRONG for this repository, and the difference is the whole design
// of this file. Measured over every csproj under src/ + apps/, that rule flags
// 36 namespaces across 15 assemblies, and every one of them is legitimate:
//
//   Harbor.Ipc.Client          -> Harbor.Ipc.Protocol      (one protocol
//   Harbor.Ipc.Server          -> Harbor.Ipc.Transport     namespace shared by
//   Harbor.Ipc.Abstractions    -> Harbor.Ipc               the IPC family)
//   Harbor.Ui.Framework.Projection -> Harbor.Ui.Framework.Rendering
//   Harbor.Ui.Framework.Services     -> Harbor.Ui.Framework.Overlays
//   Harbor.Tui.CellForge.Engine    -> Harbor.Tui.CellForge.Rendering
//   Harbor.Diagnostics.Abstractions -> Harbor.Diagnostics
//   … and so on.
//
// A family of assemblies sharing one namespace root is a normal .NET
// convention and this repo uses it deliberately. What is NOT normal is an
// assembly reaching OUT of its family into ANOTHER family's root — and doing
// so across a LAYER boundary. So the discriminator is root OWNERSHIP, which is
// what R1 and R2 actually say. `OwnershipDetector_StaysQuietOnTheFamilyNamespaces`
// pins that boundary so a future reader does not "helpfully" widen this rule
// into the 36-namespace rename that is not this issue's job.
//
// WHY A TEXT SCAN AND NOT AN ANALYZER
// ----------------------------------
// `Harbor.Architecture.Tests` must not take a reference edge to product
// assemblies to judge them (see UiConfigDefaultsRule.cs for the same reasoning,
// and PresentationCapabilityRules.cs for the one place Cecil IS used — there the
// question is about BCL capability in IL, which is a metadata question). Here
// the question is "what does this FILE DECLARE", which is answered by reading
// the file. A source scan needs no reference edge, needs no rebuild, and sees
// `contrib/` and `tests/` as out of perimeter via the shared SourceScan filter.
//
// WHY THE RULE IS ABOUT DECLARATIONS, NOT ABOUT USINGS
// ----------------------------------------------------
// A `using Harbor.Abstractions.Tools;` in a consumer is CORRECT and required —
// that is where `ITool` and `IToolRegistry` live. Nothing here forbids it. Only
// a `namespace` DECLARATION rooted outside the owner's family is a violation.
// A consumer therefore needs no edit to satisfy this rule, and a well-behaved
// consumer cannot trip it.
//
// PERIMETER
// ---------
// `src/` + `apps/` only, via `SourceScan.EnumerateProductCsFiles()`, which
// already drops build output, `contrib/` (outside CI by owner decision),
// `tests/` (a rule must not police its own fixtures) and `.worktrees/`.
//
// THOSE TWO TREES ARE NOT THE SAME SET AS "THE CODE THAT COMPILES" (#763)
// -----------------------------------------------------------------------
// `EnumerateProductCsFiles` walks the DIRECTORY tree, so it returns the six
// files under `src/Harbor.Providers.Shared` and `src/Harbor.Storage.Shared` —
// and the assembly walk that follows, which looks for the nearest ancestor
// directory holding a `*.csproj`, ran off the top of the repository and found
// none. So the files were enumerated and then dropped: the rule reported green
// having read none of them.
//
// This is the same structural fact #456 found, and #456's fix does not reach
// here. That fix taught `RepoPaths.EnumerateCsFiles(projectDir)` to resolve
// `<Compile Include>` items out of the csproj XML; this rule does not call it.
// It has its own project map (`MapProjectDirectoryToAssemblyName`) and its own
// walk-up, so it never learns that those six files are compiled into four
// provider assemblies and two storage assemblies. The files are not excluded —
// nothing in this file, in `SourceScan.IsBuildOutput`, or in any table rejects
// them by path. They are unattributable, which is a different failure with the
// same symptom.
//
// R3 is what makes that difference visible. A shared-source file has N
// consuming assemblies, not zero, so the honest reading of "which assembly
// declares this namespace?" is "all of them": the text is identical in every
// copy, so the verdict is too, and a rule that picks one is picking arbitrarily.
//
// KNOWN LIMITATIONS — stated, not hidden
// -------------------------------------
//   * A `.cs` file with no owning `.csproj` above it AND no project linking it
//     is now a hard failure (R3), not a silent skip. Nothing in the tree is in
//     that state today — `SharedSourceLinkRules` holds the two csproj-less
//     folders declared, and holds every file in them linked by someone — so R3
//     is the assertion that keeps it that way.
//   * The scan reads the NAMESPACE DECLARATION text. A type declared with an
//     explicit `namespace` attribute is not a C# shape, so this cannot miss one.
//   * R2 covers `Harbor.Registries` only. The other 14 assemblies that use a
//     family namespace are deliberately untouched — see the header.

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #452: the <c>Harbor.Abstractions</c> namespace root belongs to the
///     ADR-007 contract trio, and no Application assembly declares into it.
/// </summary>
public class AbstractionsNamespaceOwnershipRules
{
    /// <summary>
    ///     The contract namespace root. Owned by the ADR-007 trio; see the file
    ///     header.
    /// </summary>
    internal const string ContractRoot = "Harbor.Abstractions";

    /// <summary>
    ///     ADR-007 (<c>docs/adr/ADR-007-abstractions-contracts-split.md</c>)
    ///     kept the pre-split namespaces for <c>Harbor.Extensions</c> so that
    ///     splitting the contract assembly cost consumers zero <c>using</c>
    ///     changes. It is the one assembly outside the <see cref="ContractRoot" />
    ///     name prefix that may declare into the contract root, and it is the ONLY
    ///     such exception in this file.
    /// </summary>
    internal static readonly string[] Adr007NamespacePreservers = ["Harbor.Extensions"];

    /// <summary>
    ///     The assembly #452 is about, and the one that must own its own root.
    /// </summary>
    internal const string RegistriesAssembly = "Harbor.Registries";

    /// <summary>
    ///     One namespace DECLARATION. Comments are stripped before the match, so
    ///     prose that names a foreign namespace — of which this repository has a
    ///     great deal, e.g. the XML docs in <c>IToolRegistry.cs</c> that name
    ///     <c>ToolRegistry.Freeze</c> — can never trip the rule.
    /// </summary>
    private static readonly Regex NamespaceDeclaration = new(
        @"^[ \t]*namespace[ \t]+(?<ns>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)[ \t]*(;|\{|$)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    ///     The four files #452 is about. The discovery test asserts all four are
    ///     inside the scanned set, so the rule cannot pass by not looking.
    /// </summary>
    internal static readonly string[] FilesTheIssueNamed =
    [
        "src/Harbor.Registries/Agents/AgentRegistry.cs",
        "src/Harbor.Registries/Events/InMemoryEventBus.cs",
        "src/Harbor.Registries/Providers/ProviderRegistry.cs",
        "src/Harbor.Registries/Tools/ToolRegistry.cs",
    ];

    // ── the rules ─────────────────────────────────────────────────────────

    [Test]
    public async Task NoAssemblyOutsideTheContractTrio_DeclaresIntoTheAbstractionsRoot()
    {
        var squatters = new List<string>();

        foreach (Declaration declaration in ScanDeclarations().Declarations)
        {
            if (!IsSquattingOnContractRoot(declaration.Assembly, declaration.Declared))
            {
                continue;
            }

            squatters.Add(
                $"{declaration.File}:{declaration.Line} — assembly `{declaration.Assembly}` declares "
                + $"`namespace {declaration.Declared};`. "
                + $"`{ContractRoot}` is the ADR-007 contract root, owned by `{ContractRoot}` itself, "
                + $"`{ContractRoot}.Contracts` and `Harbor.Extensions` — the Domain facade, the pure "
                + "contract models and the extension pool. An Application/Infrastructure/Presentation "
                + "assembly declaring there makes one namespace span two layers: a consumer writing "
                + $"`using {declaration.Declared};` binds the contract AND the implementation and cannot see, by "
                + "reading, which side of the pyramid the type it just named came from. Declare it under "
                + $"the owning assembly's own root (`{declaration.Assembly}.…`). See #452.");
        }

        await Assert.That(squatters).IsEmpty()
            .Because(
                "Issue #452. The layers are declared in FullLayerMatrixTests but the names did not follow: "
                + "Harbor.Registries (Application) was declaring types under the Domain contract root. Five "
                + "of that project's nine files had already moved to Harbor.Registries.*; these are the "
                + "ones that had not, and they are what makes Harbor.Abstractions.Providers name both "
                + "IProviderRegistry (Domain) and ProviderRegistry (Application).");
    }

    [Test]
    public async Task RegistriesAssembly_DeclaresEveryNamespaceUnderItsOwnRoot()
    {
        var foreign = new List<string>();

        foreach (Declaration declaration in ScanDeclarations().Declarations)
        {
            if (!string.Equals(declaration.Assembly, RegistriesAssembly, StringComparison.Ordinal)
                || IsRootedAt(declaration.Declared, RegistriesAssembly))
            {
                continue;
            }

            foreign.Add(
                $"{declaration.File}:{declaration.Line} — `{RegistriesAssembly}` declares "
                + $"`namespace {declaration.Declared};`, which is not rooted at `{RegistriesAssembly}`. "
                + "The owning assembly's name is what tells a reader which layer a type came from; a "
                + "namespace borrowed from another layer takes that away. "
                + $"Declare it as `{RegistriesAssembly}.…`. See #452.");
        }

        await Assert.That(foreign).IsEmpty()
            .Because(
                "Issue #452, second half. Composites such as CompositeToolRegistry and "
                + "InMemoryMcpRegistry already live in Harbor.Registries.Tools, and the event middlewares "
                + "already live in Harbor.Registries.Events; the four registry implementations did not. "
                + "One project split half and half is the worst of both readings.");
    }

    /// <summary>
    ///     R3 — every namespace declaration in the product tree belongs to at least
    ///     one assembly. The two rules above are only as strong as the attribution
    ///     under them: a declaration handed to no assembly is a declaration neither
    ///     of them reads, and the result looks identical to a pass.
    /// </summary>
    /// <remarks>
    ///     #763. On the tree this rule was written against, the six files in
    ///     <c>src/Harbor.Providers.Shared</c> and <c>src/Harbor.Storage.Shared</c> were
    ///     enumerated by <see cref="SourceScan.EnumerateProductCsFiles" /> and then
    ///     dropped, because the walk-up for an owning <c>*.csproj</c> left the tree
    ///     and came back empty. They compile into four provider assemblies and two
    ///     storage assemblies, so there was never a question of judging them — only
    ///     of knowing which assembly to judge them as.
    /// </remarks>
    [Test]
    public async Task No_Namespace_Declaration_Is_Left_Without_An_Assembly_To_Judge_It()
    {
        string[] unattributed = ScanDeclarations().Unattributed;

        await Assert.That(unattributed).IsEmpty()
            .Because(string.Join("\n", unattributed));
    }

    // ── non-vacuity ───────────────────────────────────────────────────────

    [Test]
    public async Task Discovery_FindsTheProductTree_AndAllFourFilesTheIssueNamed()
    {
        // Without a repository root every rule in this file passes vacuously.
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because(
                "This guard walks the working tree. With no Harbor.slnx above AppContext.BaseDirectory the "
                + "scan yields nothing and both rules report green while enforcing nothing.");

        if (root is null)
        {
            return;
        }

        IReadOnlyList<string> files = SourceScan.EnumerateProductCsFiles();
        var relative = files.Select(SourceScan.Relative).ToList();

        await Assert.That(relative.Count).IsGreaterThan(500)
            .Because(
                $"src/ + apps/ hold far more than 500 source files; found {relative.Count}. A near-zero "
                + "count means the scan rooted itself somewhere wrong and the rules guard nothing.");

        foreach (string named in FilesTheIssueNamed)
        {
            await Assert.That(relative.Contains(named)).IsTrue()
                .Because(named + " must be inside the scanned set — it is one of the four files #452 is about.");
        }
    }

    [Test]
    public async Task OwnershipDetector_FiresOnAllFourDeclarationsTheIssueNamed()
    {
        // The exact four namespace lines #452 reported, paired with the assembly
        // each file actually lives in. If the detector cannot recognise the
        // defect it was written for, it recognises nothing.
        (string Assembly, string Declared)[] mustFail =
        [
            ("Harbor.Registries", "Harbor.Abstractions.Agents"),
            ("Harbor.Registries", "Harbor.Abstractions.Events"),
            ("Harbor.Registries", "Harbor.Abstractions.Providers"),
            ("Harbor.Registries", "Harbor.Abstractions.Tools"),
            // The same shape from any other layer is equally wrong.
            ("Harbor.Application", "Harbor.Abstractions.Sessions"),
            ("Harbor.Storage.Jsonl", "Harbor.Abstractions.Tools"),
            // #763: a shared-source file is judged as each assembly that compiles it,
            // so the consumer side of that judgement has to be this same detector. A
            // provider assembly declaring into the contract root is no less wrong for
            // having arrived there through a `<Compile Include>` than by living in it.
            ("Harbor.Providers.OpenAI", "Harbor.Abstractions.Internal"),
            ("Harbor.Providers.Anthropic", "Harbor.Abstractions.Providers"),
            ("Harbor.Storage.Sqlite", "Harbor.Abstractions.Sessions"),
        ];

        foreach ((string assembly, string declared) in mustFail)
        {
            await Assert.That(IsSquattingOnContractRoot(assembly, declared)).IsTrue()
                .Because($"{assembly} declaring `{declared}` is exactly the #452 shape and must be flagged.");
        }
    }

    [Test]
    public async Task OwnershipDetector_StaysQuietOnTheAdr007Trio()
    {
        // ADR-007 preserved these namespaces on purpose so that splitting the
        // contract assembly cost consumers zero `using` changes. Red-flagging
        // them would be re-litigating a recorded decision.
        (string Assembly, string Declared)[] mustPass =
        [
            ("Harbor.Abstractions", "Harbor.Abstractions.Tools"),
            ("Harbor.Abstractions", "Harbor.Abstractions"),
            ("Harbor.Abstractions.Contracts", "Harbor.Abstractions.Models"),
            ("Harbor.Abstractions.Contracts", "Harbor.Abstractions.Events"),
            ("Harbor.Abstractions.Contracts", "Harbor.Abstractions.Permissions"),
            ("Harbor.Abstractions.Contracts", "Harbor.Abstractions.Contracts"),
            ("Harbor.Extensions", "Harbor.Abstractions.Extensions"),
        ];

        foreach ((string assembly, string declared) in mustPass)
        {
            await Assert.That(IsSquattingOnContractRoot(assembly, declared)).IsFalse()
                .Because(
                    $"{assembly} declaring `{declared}` is the ADR-007 namespace preservation, recorded in "
                    + "docs/adr/ADR-007-abstractions-contracts-split.md. Flagging it would make this rule cry "
                    + "wolf on the repository's own recorded decision.");
        }
    }

    [Test]
    public async Task OwnershipDetector_StaysQuietOnTheFamilyNamespaces()
    {
        // Measured over every csproj in src/ + apps/, these are real. The
        // stronger "namespace == assembly name" rule flags 36 namespaces across
        // 15 assemblies, all of them this shape. This test is the boundary:
        // root ownership is the discriminator, not spelling equality.
        (string Assembly, string Declared)[] mustPass =
        [
            ("Harbor.Ipc.Client", "Harbor.Ipc.Protocol"),
            ("Harbor.Ipc.Server", "Harbor.Ipc.Transport"),
            ("Harbor.Ipc.Abstractions", "Harbor.Ipc"),
            ("Harbor.Ui.Framework.Projection", "Harbor.Ui.Framework.Rendering"),
            ("Harbor.Ui.Framework.Services", "Harbor.Ui.Framework.Overlays"),
            ("Harbor.Tui.CellForge.Engine", "Harbor.Tui.CellForge.Rendering"),
            ("Harbor.Diagnostics.Abstractions", "Harbor.Diagnostics"),
            ("Harbor.Telemetry.Core", "Harbor.Telemetry"),
            // And the shape #452 produced as the FIX.
            ("Harbor.Registries", "Harbor.Registries.Tools"),
            ("Harbor.Registries", "Harbor.Registries.Events"),
        ];

        foreach ((string assembly, string declared) in mustPass)
        {
            await Assert.That(IsSquattingOnContractRoot(assembly, declared)).IsFalse()
                .Because(
                    $"{assembly} declaring `{declared}` is a family namespace: one root shared by one family "
                    + "of assemblies. Widening this rule to require namespace == assembly name would flag 36 "
                    + "namespaces across 15 assemblies, which is a mass rename for tidiness and NOT #452. "
                    + "If you are reading this because such a rule was proposed, this test is why not.");
        }
    }

    /// <summary>
    ///     The #763 defect, stated as a measurement: every file the csprojs
    ///     <c>&lt;Compile Include&gt;</c>-link must come back from the namespace scan
    ///     attributed to every assembly that compiles it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the shape of the hole rather than a planted violation. The six
    ///         shared-source files declare <c>Harbor.Providers.Internal</c> and
    ///         <c>Harbor.Storage.Shared</c>, neither under the contract root, so no
    ///         <em>rule</em> in this file was wrong about anything — the walk simply
    ///         never reached them. A guard asserting the verdicts alone would have stayed
    ///         green through that, which is why this asserts the attribution instead.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task The_Namespace_Scan_Attributes_Every_Shared_Source_File_To_Its_Consumers()
    {
        Declaration[] declarations = ScanDeclarations().Declarations;
        var missing = new List<string>();

        foreach ((string file, IReadOnlyList<string> consumers) in
                 LinkedConsumers.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            string relative = SourceScan.Relative(file);

            var attributed = declarations
                .Where(d => string.Equals(d.File, relative, StringComparison.Ordinal))
                .Select(d => d.Assembly)
                .ToHashSet(StringComparer.Ordinal);

            foreach (string consumer in consumers)
            {
                if (attributed.Contains(consumer))
                {
                    continue;
                }

                missing.Add(
                    $"{relative}: compiled into `{consumer}`, but the #452 ownership scan attributes "
                    + "no namespace declaration to it. The file has no `*.csproj` above it, so the "
                    + "walk-up for an owning project leaves the tree and returns nothing, and a "
                    + "declaration no assembly owns is a declaration this rule does not judge — which "
                    + "reads exactly like a pass. See #763.");
            }
        }

        await Assert.That(missing).IsEmpty()
            .Because(string.Join("\n", missing));
    }

    /// <summary>
    ///     Non-vacuity for the rule above — the linked-file map is real, its key set
    ///     is the set of files the declared shared-source folders hold, and a consumer
    ///     is a set rather than a choice.
    /// </summary>
    [Test]
    public async Task The_Linked_File_Map_Covers_Every_Declared_Shared_Source_File()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> consumers = LinkedConsumers;

        await Assert.That(consumers.Count).IsGreaterThan(0)
            .Because(
                "#763's mechanism is a csproj-less folder whose source is compiled into other "
                + "assemblies by <Compile Include>. An empty map means every shared file in the "
                + "product tree is unattributable — so R3 above is then failing for the right reason "
                + "rather than passing for the wrong one.");

        string[] declared =
        [
            .. FullLayerMatrixTests.SharedSourceFolders.Keys
                .Select(RepoPaths.FindProjectDir)
                .Where(dir => dir is not null)
                .SelectMany(dir => Directory.GetFiles(dir!, "*.cs", SearchOption.TopDirectoryOnly))
                .Select(Path.GetFullPath)
                .OrderBy(p => p, StringComparer.Ordinal)
        ];

        await Assert.That(consumers.Keys.OrderBy(p => p, StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(declared)
            .Because(
                "Two independent statements of one population: the `.cs` files the declared "
                + "shared-source folders hold, and the files the csprojs actually link. A file in a "
                + "declared folder that nobody links is the #456 graveyard case; a linked file outside "
                + "every declared folder is the #763 hole wearing a different name.");

        int shared = consumers.Count(entry => entry.Value.Count > 1);

        await Assert.That(shared).IsGreaterThan(0)
            .Because(
                "The discriminating property of this map is that a consumer is a SET. SsePump.cs is "
                + "linked into four provider assemblies, so a resolution that picked one owner per file "
                + "would satisfy every other assertion in this file while re-opening #763 in the shape "
                + "'pick an assembly and judge the copy'. The invariant is deliberately 'at least one "
                + "file', not 'every file': SessionStatsAggregator reaches Harbor.Storage.Jsonl alone, "
                + "so a blanket 'every shared file has many consumers' would be false and would make "
                + "this control lie rather than discriminate.");
    }

    [Test]
    public async Task CommentProse_NamingAForeignNamespace_CannotTripTheRule()
    {
        // The repository names foreign namespaces in XML docs constantly — e.g.
        // IToolRegistry.cs says "The default ToolRegistry in Harbor.Registries"
        // while sitting in the Domain facade that cannot reference it. Comments
        // are stripped before matching, and this test fails if that ever stops
        // being true.
        const string prose =
            """
            // namespace Harbor.Abstractions.Tools;
            /// <see cref="Harbor.Abstractions.Providers.ProviderRegistry.Freeze" />
            /* namespace Harbor.Abstractions.Events; */
            namespace Harbor.Registries.Tools;
            """;

        string[] matches = NamespaceDeclaration
            .Matches(SourceScan.StripComments(prose))
            .Select(m => m.Groups["ns"].Value)
            .ToArray();

        await Assert.That(matches).IsEquivalentTo(new[] { "Harbor.Registries.Tools" })
            .Because(
                "Only the real declaration may survive comment stripping. A rule that fires on prose "
                + "documenting a foreign namespace gets deleted within a week, and a deleted rule protects "
                + "nothing.");
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>
    ///     Whether <paramref name="assemblyName" /> declaring
    ///     <paramref name="declaredNamespace" /> is squatting on the ADR-007
    ///     contract root. The whole rule, as one pure predicate so the tests
    ///     above can pin it without walking the tree.
    /// </summary>
    internal static bool IsSquattingOnContractRoot(string assemblyName, string declaredNamespace)
    {
        if (!IsRootedAt(declaredNamespace, ContractRoot))
        {
            return false;
        }

        // An assembly named under the contract root (the facade itself, the
        // .Contracts split, and any future split of it) owns that root.
        if (IsRootedAt(assemblyName, ContractRoot))
        {
            return false;
        }

        return !Adr007NamespacePreservers.Contains(assemblyName, StringComparer.Ordinal);
    }

    /// <summary>
    ///     Whether <paramref name="value" /> is <paramref name="root" /> itself
    ///     or lies beneath it. Segment-aware, so <c>Harbor.AbstractionsExtra</c>
    ///     is not treated as living under <c>Harbor.Abstractions</c>.
    /// </summary>
    private static bool IsRootedAt(string value, string root)
        => string.Equals(value, root, StringComparison.Ordinal)
           || value.StartsWith(root + ".", StringComparison.Ordinal);

    /// <summary>
    ///     One namespace DECLARATION paired with one assembly that compiles the file
    ///     declaring it.
    /// </summary>
    /// <remarks>
    ///     A shared-source file compiled into N assemblies yields N of these, carrying
    ///     identical text. That repetition is the point (#763): the namespace is one
    ///     string declared by several assemblies at once, and a verdict that depends on
    ///     which copy the walk happened to reach is a verdict about the walk.
    /// </remarks>
    /// <param name="File">Repository-relative path of the declaring file.</param>
    /// <param name="Assembly">Simple name of one assembly that compiles that file.</param>
    /// <param name="Line">One-based line of the declaration.</param>
    /// <param name="Declared">The declared namespace.</param>
    private sealed record Declaration(string File, string Assembly, int Line, string Declared);

    /// <summary>
    ///     One pass over the product tree: the declarations it could attribute to an
    ///     assembly, and the ones it could not.
    /// </summary>
    /// <param name="Declarations">Every declaration, paired with each owning assembly.</param>
    /// <param name="Unattributed">
    ///     One readable message per declaration no assembly owns. Empty exactly when R3
    ///     holds; non-empty means part of the tree is being read by nobody.
    /// </param>
    private sealed record ScanResult(Declaration[] Declarations, string[] Unattributed);

    /// <summary>
    ///     The assemblies that compile a <c>*.cs</c> file through a
    ///     <c>&lt;Compile Include&gt;</c> link item, keyed by the file's absolute path.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The same resolution <see cref="RepoPaths.EnumerateCsFiles" /> performs for
    ///         #456, walked the other way: #456 asks "which files does this project
    ///         compile?" and this asks "which projects compile this file?". Both read
    ///         <see cref="RepoPaths.ReadCompileIncludes" /> — the compiler's own input,
    ///         not a declared list — so a link item added to a csproj moves both answers
    ///         together and neither can drift from the build.
    ///     </para>
    ///     <para>
    ///         Memoised behind <see cref="LinkedConsumers" /> because R3 and the attribution
    ///         check each ask for it on every run, and the underlying per-csproj parse is
    ///         already memoised by <see cref="RepoPaths" />.
    ///     </para>
    /// </remarks>
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> CachedLinkedConsumers =
        new(MapLinkedFilesToConsumingAssemblies);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> LinkedConsumers => CachedLinkedConsumers.Value;

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> MapLinkedFilesToConsumingAssemblies()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach ((string projectDir, string assembly) in Owners)
        {
            string[] csprojs = Directory.GetFiles(projectDir, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojs.Length is not 1)
            {
                continue;
            }

            foreach (string include in RepoPaths.ReadCompileIncludes(csprojs[0]))
            {
                string resolved = Path.GetFullPath(
                    Path.Combine(projectDir, include.Replace('\\', Path.DirectorySeparatorChar)));

                // A link item naming a file the project already owns is redundant, and
                // counting it would let a rule pass on a file that is really the project's
                // own — the same filter RepoPaths.EnumerateLinkedSourceFiles applies.
                if (resolved.StartsWith(projectDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    continue;
                }

                // MSBuild rejects a missing include, so a resolved-but-absent path is not
                // evidence about the file set. SharedSourceLinkRules R2 is where a link
                // with nothing behind it IS a defect.
                if (!File.Exists(resolved))
                {
                    continue;
                }

                if (!map.TryGetValue(resolved, out List<string>? consumers))
                {
                    consumers = [];
                    map[resolved] = consumers;
                }

                if (!consumers.Contains(assembly, StringComparer.Ordinal))
                {
                    consumers.Add(assembly);
                }
            }
        }

        return map.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value,
            StringComparer.Ordinal);
    }

    /// <summary>
    ///     Every namespace declaration in the product tree, attributed to the
    ///     assemblies that compile the declaring file, plus the declarations that could
    ///     not be attributed to any.
    /// </summary>
    private static ScanResult ScanDeclarations()
    {
        var declarations = new List<Declaration>();
        var unattributed = new List<string>();

        foreach (string file in SourceScan.EnumerateProductCsFiles())
        {
            if (SourceScan.TryReadAllText(file) is not { } raw)
            {
                continue;
            }

            string[] lines = SourceScan.StripComments(raw).Split('\n');
            string relative = SourceScan.Relative(file);

            for (int i = 0; i < lines.Length; i++)
            {
                Match match = NamespaceDeclaration.Match(lines[i]);
                if (!match.Success)
                {
                    continue;
                }

                string declared = match.Groups["ns"].Value;
                int line = i + 1;

                if (FindOwningAssembly(file) is not { } assembly)
                {
                    unattributed.Add(
                        $"{relative}:{line} — declares `namespace {declared};` and no assembly owns it. "
                        + "The nearest ancestor directory holding a `*.csproj` does not exist, so the "
                        + "ownership walk ends in nothing and neither R1 nor R2 reads this declaration: "
                        + "the gate reports green having never looked at it. A `<Compile Include>` link "
                        + "from a consumer gives the file an owner; if nothing links it, no compiler "
                        + "reads it either. See #763.");
                    continue;
                }

                declarations.Add(new Declaration(relative, assembly, line, declared));
            }
        }

        return new ScanResult([.. declarations], [.. unattributed]);
    }

    /// <summary>
    ///     The simple assembly name of the project that owns <paramref name="file" /> —
    ///     the nearest ancestor directory that holds a <c>*.csproj</c> — or
    ///     <c>null</c> when the file has no owning project.
    /// </summary>
    private static string? FindOwningAssembly(string file)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (dir is not null)
        {
            if (Owners.TryGetValue(dir.FullName, out string? assembly))
            {
                return assembly;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    ///     Cache for <see cref="MapProjectDirectoryToAssemblyName" />.
    /// </summary>
    /// <remarks>
    ///     Memoised for the same reason <see cref="RepoPaths.EnumerateSrcProjects" /> is:
    ///     the walk parses one csproj per project, and four rules here reach for the
    ///     result independently. The tree does not change while the gate runs.
    /// </remarks>
    private static readonly Lazy<IReadOnlyDictionary<string, string>> CachedOwners = new(MapProjectDirectoryToAssemblyName);

    private static IReadOnlyDictionary<string, string> Owners => CachedOwners.Value;

    /// <summary>
    ///     Absolute project directory to produced assembly simple name, for every
    ///     project under <c>src/</c> and <c>apps/</c>.
    /// </summary>
    private static IReadOnlyDictionary<string, string> MapProjectDirectoryToAssemblyName()
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        if (RepoPaths.RepoRoot is not { } root)
        {
            return owners;
        }

        foreach (string tree in SourceScan.ProductTrees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string csproj in Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories))
            {
                if (SourceScan.IsBuildOutput(csproj))
                {
                    continue;
                }

                string projectDir = Path.GetDirectoryName(csproj)!;
                owners[projectDir] = ReadAssemblyName(csproj, Path.GetFileName(projectDir));
            }
        }

        return owners;
    }

    /// <summary>
    ///     The <c>&lt;AssemblyName&gt;</c> a csproj declares, defaulting to its
    ///     directory name — the same convention the SDK and
    ///     <see cref="RepoPaths" /> use.
    /// </summary>
    private static string ReadAssemblyName(string csprojPath, string projectDir)
    {
        try
        {
            XDocument document = XDocument.Load(csprojPath, LoadOptions.None);
            foreach (XElement element in document.Descendants())
            {
                if (element.Name.LocalName == "AssemblyName" && element.Value.Length > 0)
                {
                    return element.Value.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            // Fall through to the directory-name default.
        }

        return projectDir;
    }
}
