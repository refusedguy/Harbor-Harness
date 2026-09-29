// AbstractionsNamespaceOwnershipRules.cs — GUARD for issue #452.
//
// THE DEFECT THIS GUARDS
// ----------------------
// The layers are declared but the NAMES do not reflect them. A type that lives
// in Harbor.Registries (an Application-layer assembly, see
// FullLayerMatrixTests.cs:404) was declared in a namespace that READS like a
// contract:
//
//   src/Harbor.Registries/Agents/AgentRegistry.cs     namespace Harbor.Abstractions.Agents;
//   src/Harbor.Registries/Events/InMemoryEventBus.cs namespace Harbor.Abstractions.Events;
//   src/Harbor.Registries/Providers/ProviderRegistry.cs
//                                                     namespace Harbor.Abstractions.Providers;
//   src/Harbor.Registries/Tools/ToolRegistry.cs       namespace Harbor.Abstractions.Tools;
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
// src/Harbor.Registries; FIVE had already moved to the owning root
// (`Harbor.Registries.Events` — SamplingMiddleware, TypeFilterMiddleware;
// `Harbor.Registries.Tools` — CompositeToolRegistry, FrozenToolView,
// InMemoryMcpRegistry) and FOUR had not. The audit recorded six; the nightly
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
//   Harbor.Ui.Framework.Reducers   -> Harbor.Ui.Framework.State
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
// KNOWN LIMITATIONS — stated, not hidden
// -------------------------------------
//   * A `.cs` file with no owning `.csproj` above it is skipped rather than
//     guessed at. `src/Harbor.Providers.Shared` is linked source compiled into
//     each provider project and has no csproj of its own; there is no assembly
//     to own its namespace, so the rule has nothing to say about it.
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
        IReadOnlyDictionary<string, string> owners = MapProjectDirectoryToAssemblyName();

        foreach ((string file, string assembly, int line, string declared) in ScanDeclarations(owners))
        {
            if (!IsSquattingOnContractRoot(assembly, declared))
            {
                continue;
            }

            squatters.Add(
                $"{file}:{line} — assembly `{assembly}` declares `namespace {declared};`. "
                + $"`{ContractRoot}` is the ADR-007 contract root, owned by `{ContractRoot}` itself, "
                + $"`{ContractRoot}.Contracts` and `Harbor.Extensions` — the Domain facade, the pure "
                + "contract models and the extension pool. An Application/Infrastructure/Presentation "
                + "assembly declaring there makes one namespace span two layers: a consumer writing "
                + $"`using {declared};` binds the contract AND the implementation and cannot see, by "
                + "reading, which side of the pyramid the type it just named came from. Declare it under "
                + $"the owning assembly's own root (`{assembly}.…`). See #452.");
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
        IReadOnlyDictionary<string, string> owners = MapProjectDirectoryToAssemblyName();

        foreach ((string file, string assembly, int line, string declared) in ScanDeclarations(owners))
        {
            if (!string.Equals(assembly, RegistriesAssembly, StringComparison.Ordinal)
                || IsRootedAt(declared, RegistriesAssembly))
            {
                continue;
            }

            foreign.Add(
                $"{file}:{line} — `{RegistriesAssembly}` declares `namespace {declared};`, which is not "
                + $"rooted at `{RegistriesAssembly}`. The owning assembly's name is what tells a reader "
                + "which layer a type came from; a namespace borrowed from another layer takes that away. "
                + $"Declare it as `{RegistriesAssembly}.…`. See #452.");
        }

        await Assert.That(foreign).IsEmpty()
            .Because(
                "Issue #452, second half. Composites such as CompositeToolRegistry and "
                + "InMemoryMcpRegistry already live in Harbor.Registries.Tools, and the event middlewares "
                + "already live in Harbor.Registries.Events; the four registry implementations did not. "
                + "One project split half and half is the worst of both readings.");
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
            ("Harbor.Ui.Framework.Reducers", "Harbor.Ui.Framework.State"),
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

        await Assert.That(matches).IsEqualTo(new[] { "Harbor.Registries.Tools" })
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
    ///     Every namespace declaration in the product tree, paired with the
    ///     assembly that owns the file it was found in.
    /// </summary>
    private static IEnumerable<(string File, string Assembly, int Line, string Declared)> ScanDeclarations(
        IReadOnlyDictionary<string, string> owners)
    {
        foreach (string file in SourceScan.EnumerateProductCsFiles())
        {
            if (FindOwningAssembly(file, owners) is not { } assembly)
            {
                continue;
            }

            if (SourceScan.TryReadAllText(file) is not { } raw)
            {
                continue;
            }

            string[] lines = SourceScan.StripComments(raw).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = NamespaceDeclaration.Match(lines[i]);
                if (match.Success)
                {
                    yield return (SourceScan.Relative(file), assembly, i + 1, match.Groups["ns"].Value);
                }
            }
        }
    }

    /// <summary>
    ///     The simple assembly name of the project that owns <paramref name="file" />
    ///     — the nearest ancestor directory that holds a <c>*.csproj</c> — or
    ///     <c>null</c> when the file has no owning project.
    /// </summary>
    private static string? FindOwningAssembly(string file, IReadOnlyDictionary<string, string> owners)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (dir is not null)
        {
            if (owners.TryGetValue(dir.FullName, out string? assembly))
            {
                return assembly;
            }

            dir = dir.Parent;
        }

        return null;
    }

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
