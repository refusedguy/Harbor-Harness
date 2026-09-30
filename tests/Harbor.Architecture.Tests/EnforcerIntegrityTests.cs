// EnforcerIntegrityTests.cs — rules about the RULES (#450).
//
// The layer rules in LayerDependencyTests / NetArchLayerRules /
// AbstractionsSplitLayerRules / FullLayerMatrixTests have a failure mode the
// layering checks themselves cannot see: a rule that names something that does
// not exist, permits more than intended, or covers nothing at all is GREEN
// forever. Two concrete traps:
//
//   1. Vacuous targets. NetArchTest's NotHaveDependencyOn("X") and the
//      reflection FindForbiddenReferences(asm, "X") both treat a non-existent
//      assembly name as a satisfied constraint. So a typo, a rename, or a
//      deleted project leaves the rule permanently green while enforcing
//      nothing. Live example on dev before this file: 15 rule positions named
//      "Harbor.Scripting" (a contrib project *family*, never an assembly) or
//      "Harbor.Domain" (deleted in the F1 split).
//
//   2. Vacuous coverage. An assembly absent from the inventory, the matrix and
//      the out-of-scope list has UNBOUNDED reach — nothing constrains it. Live
//      example: Harbor.Tui.NickConsoleEx (referenced by Harbor.Hosting, in no
//      list at all).
//
// And two silent-degradation paths:
//   - allowed-set rot: the matrix checked only actual ⊆ allowed, so the allowed
//     side could accumulate phantom edges forever (Plugins.Hosting "declares 5
//     targets, uses 1");
//   - csproj-level drift: ProjectReferences that bind no type emit no IL, so
//     every IL-based check is blind to them (Roslyn drops unused references).
//
// The rules below close all four. Each is written so that it CAN fail: they
// resolve names against the real project tree, read the real csproj XML, and
// compare against the real IL reference set. None of them accepts a hard-coded
// list without cross-checking it against reality.

namespace Harbor.Architecture.Tests;

using System.Text.RegularExpressions;

public sealed class EnforcerIntegrityTests
{
    /// <summary>
    ///     A declared <c>&lt;ProjectReference&gt;</c> that binds no type, so
    ///     Roslyn emits no AssemblyRef for it. Tracked per site with a reason;
    ///     each one is verified to be really unbound by
    ///     <see cref="DeclaredButUnboundProjectReferences_AreReallyUnbound" />, so
    ///     the list cannot grow into a blanket permission.
    /// </summary>
    /// <param name="From">The depending project directory.</param>
    /// <param name="To">The referenced project directory.</param>
    /// <param name="Reason">Why the reference is still declared.</param>
    internal sealed record UnboundReference(string From, string To, string Reason);

    /// <summary>
    ///     <c>&lt;ProjectReference&gt;</c> edges that produce no IL reference
    ///     (#450 gap 2 — invisible to every AssemblyRef-based rule).
    /// </summary>
    internal static readonly UnboundReference[] DeclaredButUnboundProjectReferences =
    [
        new("Harbor.Telemetry.Otlp", "Harbor.Telemetry.Core",
            "#450: the IL gate proved Harbor.Telemetry.Otlp emits no AssemblyRef for Telemetry.Core, so the declared reference is dead — remove it."),
        new("Harbor.Transport.Remote", "Harbor.Abstractions",
            "#450: the assembly binds Harbor.Abstractions.Contracts (the contract types it actually uses), not the facade, so the declared facade reference produces no IL."),
        new("Harbor.Ui.Framework", "Harbor.Ui.Framework.Abstractions",
            "#450/#542: the aggregator project has no code of its own, so none of its six ProjectReferences bind a type. See issue #542."),
        new("Harbor.Hosting", "Harbor.Providers.Anthropic",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Hosting", "Harbor.Providers.OpenAI",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Hosting", "Harbor.Storage.Sqlite",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Hosting", "Harbor.Tui.RazorConsole",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Hosting", "Harbor.Tui.Spectre",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Hosting", "Harbor.Tui.Spectre.Fullscreen",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Hosting", "Harbor.Tui.SpectreTui",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Hosting", "Harbor.Tui.Termina",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Hosting", "Harbor.Tui.TerminalGui",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Tui.CellForge", "Harbor.Desktop.Animations",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Tui.CellForge.Engine", "Harbor.Abstractions",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Tui.NickConsoleEx", "Harbor.Abstractions",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        // #665: the (Notifications -> Harbor.Abstractions) exemption is GONE.
        // It was a #450 dead edge until NotificationTuiRenderer took an
        // INotificationProcessRunner from the facade, which bound a real type
        // in IL. DeclaredButUnboundProjectReferences_AreReallyUnbound fails the
        // moment an exempt edge turns out to be live, so the exemption was
        // removed and the edge justified in the matrix instead (the
        // Harbor.Tui.Notifications row in FullLayerMatrixTests.Matrix) — same
        // shape as #567 for ViewModels and #663 for Sessions.
        new("Harbor.Ui.Framework", "Harbor.Ui.Framework.Projection",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Ui.Framework", "Harbor.Ui.Framework.Services",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Ui.Framework", "Harbor.Ui.Framework.Sessions",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Ui.Framework", "Harbor.Ui.Framework.State",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Ui.Framework", "Harbor.Ui.Framework.ViewModels",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Ui.Framework.Projection", "Harbor.Abstractions",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Ui.Framework.Rendering", "Harbor.Desktop.Animations",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        new("Harbor.Ui.Framework.Services", "Harbor.Ui.Framework.Abstractions",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        // #663: the (Sessions → ViewModels) exemption is GONE. It was a #450
        // dead edge until SessionContext.StatusText began calling
        // StatusMappers.SessionStatusToText rather than carrying its own
        // SessionStatus→label switch, which made the edge bind a real type in
        // IL. DeclaredButUnboundProjectReferences_AreReallyUnbound fails the
        // moment an exempt edge turns out to be live, so the exemption was
        // removed and the edge justified in the matrix instead (the Sessions
        // row in FullLayerMatrixTests.Matrix). Same shape as the #567 entry for
        // (ViewModels → Abstractions) a few lines below.
        new("Harbor.Ui.Framework.ViewModels", "Harbor.Abstractions",
            "#450: permitted by the matrix but the compiled assembly emits no AssemblyRef for it (the IL gate proved the edge is dead). The ProjectReference stays declared until the tracked cleanup removes it."),
        // #567: the (ViewModels → Abstractions) exemption that sat here is GONE —
        // ToolCallViewModel.Status is a ToolCallState (the single tool-call
        // lifecycle enum, Ui.Framework.Abstractions), so the edge is now bound in
        // IL and is a real dependency. DeclaredButUnboundProjectReferences_AreReallyUnbound
        // fails the moment an exempt edge turns out to be live, so the exemption
        // was removed and the edge justified in the matrix instead (the ViewModels
        // row in FullLayerMatrixTests.Matrix).
        new("Harbor.Ui.Framework.Abstractions", "Harbor.Abstractions",
            "#450: declared but binds no type — the assembly's contracts are BCL-only, so the edge produces no IL."),
        new("Harbor.Terminal.Abstractions", "Harbor.Ui.Framework",
            "#450/#542: declared but binds no type — Harbor.Ui.Framework is an empty assembly (see issue #542)."),
        new("Harbor.Plugins.Hosting", "Harbor.Plugins.Storage",
            "#450/#541: pre-existing vestigial edge — Hosting composes the family via Plugins.Abstractions only. Remove the reference; see issue #541."),
        new("Harbor.Plugins.Hosting", "Harbor.Plugins.Compilation",
            "#450/#541: pre-existing vestigial edge — Hosting composes the family via Plugins.Abstractions only. Remove the reference; see issue #541."),
        new("Harbor.Plugins.Hosting", "Harbor.Plugins.Instantiation",
            "#450/#541: pre-existing vestigial edge — Hosting composes the family via Plugins.Abstractions only. Remove the reference; see issue #541."),
        new("Harbor.Plugins.Hosting", "Harbor.Plugins.Registration",
            "#450/#541: pre-existing vestigial edge — Hosting composes the family via Plugins.Abstractions only. Remove the reference; see issue #541."),
        new("Harbor.Hosting", "Harbor.Tui.CellForge.Engine",
            "#450/#546: pre-existing vestigial edge — DI wires the CellForge renderer, not the engine assembly directly. Remove the reference; see issue #546."),
        new("Harbor.Desktop.Abstractions", "Harbor.Terminal.Abstractions",
            "#450/#543: pre-existing vestigial edge — no TUI vocabulary is bound from this assembly. Remove the reference; see issue #543."),
        new("Harbor.Desktop.Abstractions", "Harbor.Ui.Framework",
            "#450/#542: pre-existing vestigial edge — the leaf Ui.Framework.* modules are referenced directly; Harbor.Ui.Framework itself is empty. See issue #542."),
        new("Harbor.Desktop.Shared", "Harbor.Ui.Framework",
            "#450/#544: pre-existing vestigial edge — the leaf Ui.Framework.* modules are referenced directly; Harbor.Ui.Framework itself is empty. See issue #544."),
    ];

    /// <summary>
    ///     The repository root must be discoverable, otherwise every
    ///     csproj-walking rule below degrades to "nothing to check" and passes
    ///     vacuously. The rules in this file are the only guard against that.
    /// </summary>
    [Test]
    public async Task RepositoryInventory_IsDiscoverable()
    {
        await Assert.That(RepoPaths.RepoRoot)
            .IsNotNull()
            .Because("the csproj-walking architecture rules silently pass when the repository root cannot be located");

        var srcProjects = RepoPaths.EnumerateSrcProjects();
        await Assert.That(srcProjects.Count)
            .IsGreaterThan(0)
            .Because("no src/**/*.csproj were found — the layering rules would be vacuous");
    }

    /// <summary>
    ///     Every assembly name named by a layer rule must be an assembly the repo
    ///     actually produces (#450 trap 1). This is what makes the rules
    ///     non-vacuous: a typo, a rename or a deletion now fails here instead of
    ///     silently satisfying the constraint forever.
    /// </summary>
    [Test]
    public async Task RuleTargetAssemblyNames_AllExist()
    {
        var real = RepoPaths.EnumerateRepoAssemblyNames();
        await Assert.That(real.Count)
            .IsGreaterThan(0)
            .Because("no project assembly names could be read from the repository");

        var failures = new List<string>();
        foreach (var (table, names) in NamedRuleTargets())
        {
            foreach (string name in names.Where(n => n.Contains('.', StringComparison.Ordinal)))
            {
                if (real.ContainsKey(name))
                {
                    continue;
                }

                // A project-directory name is acceptable too: the rules may name
                // either the assembly or the project that produces it.
                // (Values, not ContainsValue: TUnit ships an assertion extension
                // of that name on IReadOnlyDictionary.)
                if (real.Values.Contains(name))
                {
                    continue;
                }

                failures.Add(
                    $"{table}: '{name}' is not an assembly this repo produces — the constraint naming it can never fail. " +
                    "Remove it or point it at the real assembly.");
            }
        }

        await Assert.That(failures.Count).IsEqualTo(0).Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every src project is either on the layer matrix or explicitly listed as
    ///     out of scope with a reason (#450 gap 4). This is the coverage guard: a
    ///     new src project cannot join the repo with unbounded reach.
    /// </summary>
    [Test]
    public async Task SrcProjects_AreAllClassified()
    {
        var matrix = FullLayerMatrixTests.Matrix.Keys.ToHashSet(StringComparer.Ordinal);
        var inventory = FullLayerMatrixTests.AllSrcAssemblies.ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (string csproj in RepoPaths.EnumerateSrcProjects())
        {
            string projectDir = Path.GetFileName(Path.GetDirectoryName(csproj)!);
            if (FullLayerMatrixTests.OutOfScopeAssemblies.ContainsKey(projectDir))
            {
                continue;
            }

            if (!matrix.Contains(projectDir) && !inventory.Contains(projectDir))
            {
                failures.Add(
                    $"src/{projectDir}: not on the layer matrix and not in OutOfScopeAssemblies — " +
                    "its dependencies are unconstrained. Add a matrix row or an out-of-scope entry with a reason.");
            }
        }

        foreach (var (projectDir, reason) in FullLayerMatrixTests.OutOfScopeAssemblies)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                failures.Add($"OutOfScopeAssemblies['{projectDir}']: an out-of-scope entry must state a reason");
            }

            if (RepoPaths.FindProjectDir(projectDir) is null)
            {
                failures.Add($"OutOfScopeAssemblies['{projectDir}']: no such src project — remove the stale entry");
            }
        }

        foreach (string folder in FullLayerMatrixTests.SharedSourceFolders.Keys)
        {
            if (RepoPaths.FindProjectDir(folder) is null)
            {
                failures.Add($"SharedSourceFolders['{folder}']: no such src folder — remove the stale entry");
            }
        }

        await Assert.That(failures.Count).IsEqualTo(0).Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Allowed-set liveness (#450 gap 1): every entry the matrix permits must
    ///     be a reference the assembly really has. The previous rule checked only
    ///     actual ⊆ allowed, so the allowed side rotted silently — a permitted
    ///     edge nobody uses is a hole waiting for the next contributor.
    /// </summary>
    [Test]
    public async Task Matrix_AllowedEntries_AreLive()
    {
        var loaded = ArchitectureTestHelpers.LoadHarborAssemblies();
        var failures = new List<string>();

        foreach (string name in FullLayerMatrixTests.AllSrcAssemblies)
        {
            if (!loaded.TryGetValue(name, out var asm))
            {
                // Reported by EverySrcAssembly_ReferenceSet_MatchesMatrix; skipping
                // here would let a matrix row rot while the assembly is missing.
                failures.Add($"{name}: assembly not loaded — cannot verify its Allowed entries");
                continue;
            }

            if (!FullLayerMatrixTests.Matrix.TryGetValue(name, out var row))
            {
                continue;
            }

            var actual = ArchitectureTestHelpers.GetReferencedAssemblyNames(asm);
            var permitted = FullLayerMatrixTests.ExpandAllowed(name, row);

            // The facade→Contracts edge is implicit, not a declared permission:
            // see FacadeAssembly. Judging it here would flag every consumer that
            // binds contract types without binding the facade assembly itself.
            bool permitsFacade = permitted.Contains(FacadeAssembly);
            if (permitsFacade)
            {
                permitted.Remove(ContractAssembly);
            }

            foreach (string edge in permitted.OrderBy(n => n, StringComparer.Ordinal))
            {
                if (actual.Contains(edge))
                {
                    continue;
                }

                failures.Add(
                    $"{name} -> {edge}: permitted by the matrix but the assembly does not reference it — " +
                    "stale allowed-set entry, remove it (or add a DocumentedException if the edge is real)");
            }
        }

        await Assert.That(failures.Count).IsEqualTo(0).Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every non-analyzer <c>&lt;ProjectReference&gt;</c> declared by an
    ///     enforced src project must be justified by the matrix (#450 gap 2).
    ///     The IL-based rules are blind to a declared-but-unbound reference, so
    ///     without this an Infrastructure project could add a Presentation
    ///     <c>&lt;ProjectReference&gt;</c> and stay green as long as no type is
    ///     bound from it.
    /// </summary>
    [Test]
    public async Task DeclaredProjectReferences_AreJustifiedByTheMatrix()
    {
        var matrix = FullLayerMatrixTests.Matrix;
        var assemblyOf = SrcAssemblyNames();
        var failures = new List<string>();

        foreach (string csproj in RepoPaths.EnumerateSrcProjects())
        {
            string projectDir = Path.GetFileName(Path.GetDirectoryName(csproj)!);
            if (!matrix.TryGetValue(projectDir, out var row))
            {
                continue;
            }

            var allowed = FullLayerMatrixTests.ExpandAllowed(projectDir, row);
            var (references, analyzers) = RepoPaths.ReadProjectReferences(csproj);

            foreach (string reference in references)
            {
                if (allowed.Contains(reference))
                {
                    continue;
                }

                if (DeclaredButUnboundProjectReferences.Any(
                        u => u.From == projectDir && u.To == reference))
                {
                    continue;
                }

                // A reference to something outside the enforced src set is not a
                // layer edge. Test the PROJECT DIRECTORY: the SharpConsoleUI
                // project lives in external/, so its assembly name is in the
                // repo-wide inventory even though no src project produces it.
                if (!assemblyOf.TryGetValue(reference, out string? targetAssembly))
                {
                    continue;
                }

                // Guard against the same-name trap in reverse: only a project that
                // actually produces this assembly under src/ is in scope.
                if (!FullLayerMatrixTests.AllSrcAssemblies.Contains(targetAssembly, StringComparer.Ordinal))
                {
                    continue;
                }

                failures.Add(
                    $"src/{projectDir} -> {reference}: ProjectReference is not permitted by the matrix row " +
                    $"(layer {row.Layer}). Add it to Allowed with a reason, or to DocumentedExceptions, " +
                    "or drop the reference if it is vestigial.");
            }

            foreach (string analyzer in analyzers)
            {
                if (FullLayerMatrixTests.SourceGeneratorProjects.Contains(analyzer, StringComparer.Ordinal))
                {
                    continue;
                }

                failures.Add(
                    $"src/{projectDir} -> {analyzer}: analyzer ProjectReference is not listed in " +
                    "FullLayerMatrixTests.SourceGeneratorProjects — declare it so new generators are reviewed");
            }
        }

        await Assert.That(failures.Count).IsEqualTo(0).Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     The mirror of the rule above: a declared <c>&lt;ProjectReference&gt;</c>
    ///     that is <b>not</b> in the matrix must really be dead. If such a
    ///     reference turns out to be bound in IL it is a live, unjustified
    ///     dependency wearing a "vestigial" label — so the exemption cannot
    ///     launder a real edge.
    /// </summary>
    [Test]
    public async Task DeclaredButUnboundProjectReferences_AreReallyUnbound()
    {
        var loaded = ArchitectureTestHelpers.LoadHarborAssemblies();
        var assemblyOf = SrcAssemblyNames();
        var failures = new List<string>();

        foreach (var unbound in DeclaredButUnboundProjectReferences)
        {
            if (string.IsNullOrWhiteSpace(unbound.Reason))
            {
                failures.Add($"{unbound.From} -> {unbound.To}: exemption must state a reason");
            }

            if (!assemblyOf.TryGetValue(unbound.From, out string? fromAssembly))
            {
                failures.Add($"{unbound.From}: no such src project — remove the stale exemption");
                continue;
            }

            if (RepoPaths.FindProjectDir(unbound.To) is null)
            {
                failures.Add($"{unbound.From} -> {unbound.To}: no such src project — remove the stale exemption");
                continue;
            }

            if (!loaded.TryGetValue(fromAssembly, out var asm))
            {
                failures.Add($"{fromAssembly}: assembly not loaded — cannot verify the exemption");
                continue;
            }

            string toAssembly = assemblyOf[unbound.To];
            if (ArchitectureTestHelpers.GetReferencedAssemblyNames(asm).Contains(toAssembly))
            {
                failures.Add(
                    $"{unbound.From} -> {unbound.To}: this reference IS bound in IL — it is a live dependency, " +
                    "not a vestigial one. Remove the exemption and justify the edge in the matrix instead.");
            }
        }

        await Assert.That(failures.Count).IsEqualTo(0).Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Exceptions are file-scoped, not project-granular (#450 gap 3). Only the
    ///     files an exception names may bind a type from the offending assembly —
    ///     so an assembly-wide excuse can no longer absorb unrelated new
    ///     violations.
    /// </summary>
    [Test]
    public async Task DocumentedExceptions_AreScopedToNamedFiles()
    {
        var namespaceOwners = NamespaceOwners(SourceNamespacesByProject());
        var failures = new List<string>();

        foreach (var (from, exceptions) in FullLayerMatrixTests.DocumentedExceptions)
        {
            if (!FullLayerMatrixTests.Matrix.ContainsKey(from))
            {
                failures.Add($"{from}: has exception entries but no matrix row");
                continue;
            }

            if (RepoPaths.FindProjectDir(from) is null)
            {
                failures.Add($"{from}: has exception entries but no src project directory");
                continue;
            }

            foreach (var exception in exceptions)
            {
                var expected = new HashSet<string>(exception.Sites, StringComparer.Ordinal);
                if (expected.Count == 0)
                {
                    failures.Add($"{from} -> {exception.Target}: exception names no files — it is project-granular, which is what #450 removes");
                    continue;
                }

                var actual = FilesBindingTarget(from, exception.Target, namespaceOwners);

                var extra = actual.Except(expected, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
                var missing = expected.Except(actual, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();

                foreach (string file in extra)
                {
                    failures.Add(
                        $"src/{from}/{file}: reaches into {exception.Target}, which is only legal in " +
                        $"[{string.Join(", ", expected.OrderBy(p => p, StringComparer.Ordinal))}]");
                }

                foreach (string file in missing)
                {
                    failures.Add(
                        $"src/{from}/{file}: listed as a legal site for {exception.Target} but binds nothing from it — drop it from the exception");
                }
            }
        }

        await Assert.That(failures.Count).IsEqualTo(0).Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every document exception must carry a reason. A reasonless exception is
    ///     indistinguishable from an accident.
    /// </summary>
    /// <remarks>
    ///     #626: the check moved to <see cref="ExemptionReason" /> rather than
    ///     being re-implemented here. Four tables in this project grant a
    ///     permission, and this one used to be the only place that asked the
    ///     question — with a bare <c>IsNullOrWhiteSpace</c>, while the table that
    ///     most needs the answer (the tracked-violation baseline) was not asking
    ///     at all. The check is now written once and every table routes through
    ///     it, so a fourth copy cannot appear beside the third.
    /// </remarks>
    [Test]
    public async Task DocumentedExceptions_AllHaveReasons()
    {
        var failures = ExemptionReason.RowsWithoutAReason(
            "FullLayerMatrixTests.DocumentedExceptions",
            FullLayerMatrixTests.DocumentedExceptions.SelectMany(
                static entry => entry.Value.Select(
                    e => (Key: $"{entry.Key} -> {e.Target}", Row: new ExemptionReason.Row(e.Reason, null)))));

        await Assert.That(failures.Count).IsEqualTo(0).Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     A declared-but-unbound <c>&lt;ProjectReference&gt;</c> is a permission
    ///     nothing checked, for as long as the table existed: it carried a
    ///     <c>Reason</c> and no test ever read it. The edge is invisible to every
    ///     IL rule by construction, so the reason is the only thing standing
    ///     between "this reference is declared and will be removed" and "this
    ///     reference is declared" — with the same #450 answer copied into every
    ///     row, which is how a table stops being per-site and starts being a form
    ///     letter.
    /// </summary>
    [Test]
    public async Task DeclaredButUnboundProjectReferences_AllHaveReasons()
    {
        var failures = ExemptionReason.RowsWithoutAReason(
            "EnforcerIntegrityTests.DeclaredButUnboundProjectReferences",
            DeclaredButUnboundProjectReferences.Select(
                static r => (Key: $"{r.From} -> {r.To}", Row: new ExemptionReason.Row(r.Reason, null))));

        await Assert.That(failures.Count).IsEqualTo(0).Because(string.Join("\n", failures));
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>
    ///     Every assembly-name list the layer rules assert against, keyed by the
    ///     table it came from. Read from the rule classes themselves so a new
    ///     forbidden list cannot escape the existence check.
    /// </summary>
    private static IEnumerable<(string Table, string[] Names)> NamedRuleTargets()
    {
        foreach (var (from, exceptions) in FullLayerMatrixTests.DocumentedExceptions)
        {
            yield return ($"FullLayerMatrixTests.DocumentedExceptions['{from}']",
                [.. exceptions.Select(e => e.Target)]);
        }

        foreach (var unbound in DeclaredButUnboundProjectReferences)
        {
            yield return ("EnforcerIntegrityTests.DeclaredButUnboundProjectReferences", [unbound.To]);
        }

        foreach (string name in FullLayerMatrixTests.SourceGeneratorProjects)
        {
            yield return ("FullLayerMatrixTests.SourceGeneratorProjects", [name]);
        }

        foreach (string name in FullLayerMatrixTests.OutOfScopeAssemblies.Keys)
        {
            yield return ("FullLayerMatrixTests.OutOfScopeAssemblies", [name]);
        }
        foreach (var (name, row) in FullLayerMatrixTests.Matrix)
        {
            yield return ($"FullLayerMatrixTests.Matrix['{name}']", row.Allowed);
        }

        yield return ("LayerDependencyTests.NonDomainHarborAssemblies", LayerDependencyTests.NonDomainHarborAssemblies);
        yield return ("LayerDependencyTests.NonDomainNonSelfHarborAssemblies", LayerDependencyTests.NonDomainNonSelfHarborAssemblies);
        yield return ("NetArchLayerRules.NonDomainHarborAssemblies", NetArchLayerRules.NonDomainHarborAssemblies);
        yield return ("NetArchLayerRules.ForbiddenForInfrastructure", NetArchLayerRules.ForbiddenForInfrastructure);
        yield return ("NetArchLayerRules.ForbiddenForPresentation", NetArchLayerRules.ForbiddenForPresentation);
        yield return ("AbstractionsSplitLayerRules.NoHarborProjectRefs", AbstractionsSplitLayerRules.NoHarborProjectRefs);
        yield return ("AbstractionsSplitLayerRules.ExtensionsForbiddenRefs", AbstractionsSplitLayerRules.ExtensionsForbiddenRefs);
        yield return ("AbstractionsSplitLayerRules.AbstractionsForbiddenRefs", AbstractionsSplitLayerRules.AbstractionsForbiddenRefs);
    }

    /// <summary>src project directory → the assembly name it produces.</summary>
    private static IReadOnlyDictionary<string, string> SrcAssemblyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (assembly, projectDir) in RepoPaths.EnumerateRepoAssemblyNames())
        {
            // Only src projects participate in the layer matrix.
            if (RepoPaths.FindProjectDir(projectDir) is not null)
            {
                result[projectDir] = assembly;
            }
        }

        return result;
    }

    /// <summary>Project directory → the namespaces it declares.</summary>
    private static IReadOnlyDictionary<string, HashSet<string>> SourceNamespacesByProject()
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string csproj in RepoPaths.EnumerateSrcProjects())
        {
            string projectDir = Path.GetFileName(Path.GetDirectoryName(csproj)!);
            var namespaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in RepoPaths.EnumerateCsFiles(projectDir))
            {
                foreach (string ns in DeclaredNamespaces(ReadSource(file)))
                {
                    namespaces.Add(ns);
                }
            }

            result[projectDir] = namespaces;
        }

        return result;
    }

    /// <summary>Namespace → the projects that declare a type in it.</summary>
    private static IReadOnlyDictionary<string, HashSet<string>> NamespaceOwners(
        IReadOnlyDictionary<string, HashSet<string>> namespacesByProject)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (projectDir, namespaces) in namespacesByProject)
        {
            foreach (string ns in namespaces)
            {
                if (!result.TryGetValue(ns, out var owners))
                {
                    owners = new HashSet<string>(StringComparer.Ordinal);
                    result[ns] = owners;
                }

                owners.Add(projectDir);
            }
        }

        return result;
    }

    /// <summary>
    ///     The <c>src/&lt;from&gt;</c> files that bind a type from
    ///     <c>target</c> — the real, measured extent of a document exception.
    /// </summary>
    private static HashSet<string> FilesBindingTarget(
        string from,
        string target,
        IReadOnlyDictionary<string, HashSet<string>> namespaceOwners)
    {
        // The projects that own a namespace under the target's namespace prefix,
        // e.g. target "Harbor.Ui.Framework.State" owns "Harbor.Ui.Framework.Panels"
        // once a project declares a file in it.
        var targetProjects = new HashSet<string>(
            namespaceOwners
                .Where(e => e.Key.StartsWith(target, StringComparison.Ordinal))
                .SelectMany(e => e.Value), StringComparer.Ordinal);

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in RepoPaths.EnumerateCsFiles(from))
        {
            string code = ReadSource(file);
            bool binds = false;

            foreach (string imported in ImportedNamespaces(code))
            {
                if (namespaceOwners.TryGetValue(imported, out var owners)
                    && owners.Overlaps(targetProjects))
                {
                    binds = true;
                    break;
                }
            }

            if (!binds)
            {
                // A fully-qualified reference binds the assembly even without a
                // using directive.
                foreach (string qualified in QualifiedHarborNames(code))
                {
                    if (LongestNamespaceOwners(qualified, namespaceOwners)
                        .Overlaps(targetProjects))
                    {
                        binds = true;
                        break;
                    }
                }
            }

            if (binds)
            {
                result.Add(ProjectRelative(file, from));
            }
        }

        return result;
    }

    /// <summary>
    ///     The projects owning the longest declared namespace that prefixes
    ///     <paramref name="qualified" /> — the namespace a C# identifier actually
    ///     resolves into. Several projects may legitimately share it.
    /// </summary>
    private static HashSet<string> LongestNamespaceOwners(
        string qualified,
        IReadOnlyDictionary<string, HashSet<string>> namespaceOwners)
    {
        string[] parts = qualified.Split('.');
        for (int take = parts.Length; take > 1; take--)
        {
            string candidate = string.Join('.', parts[..take]);
            if (namespaceOwners.TryGetValue(candidate, out var owners))
            {
                return owners;
            }
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Matching runs over checked-in source only; the timeout satisfies the regex-analyzer rule.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    // Regex sources use verbatim strings (@"") throughout: several patterns end
    // with a '"' or contain '"""' sequences, which a raw string literal cannot
    // express without a 4-quote delimiter.
    /// <summary>
    ///     The implicit facade edge: permitting <c>Harbor.Abstractions</c> also
    ///     permits <c>Harbor.Abstractions.Contracts</c>, because the facade
    ///     re-exports contract types and consumer IL legitimately emits the
    ///     Contracts AssemblyRef. Excluded from
    ///     <see cref="Matrix_AllowedEntries_AreLive" />: the derived edge is not a
    ///     hand-written permission, and a consumer may bind contract types without
    ///     binding the facade assembly itself.
    /// </summary>
    internal const string FacadeAssembly = "Harbor.Abstractions";

    /// <summary>The contract assembly the facade re-exports.</summary>
    internal const string ContractAssembly = "Harbor.Abstractions.Contracts";

    private static readonly Regex NamespacePattern = new(
        @"^\s*namespace\s+([A-Za-z0-9_.]+)\s*[;{]",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex UsingPattern = new(
        @"^\s*(?:global\s+)?using\s+(?:static\s+)?([A-Za-z0-9_.]+)\s*;",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex QualifiedPattern = new(
        @"\b(Harbor(?:\.[A-Za-z0-9_]+)+)",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex BlockCommentPattern = new(
        @"/\*.*?\*/",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex LineCommentPattern = new(
        @"//[^\n]*",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex StringPattern = new(
        @"""(?:[^""\\\n]|\\.)*""",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static IEnumerable<string> DeclaredNamespaces(string code)
        => NamespacePattern.Matches(code).Select(m => m.Groups[1].Value);

    private static IEnumerable<string> ImportedNamespaces(string code)
        => UsingPattern.Matches(code).Select(m => m.Groups[1].Value);

    private static IEnumerable<string> QualifiedHarborNames(string code)
        => QualifiedPattern.Matches(code).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);

    /// <summary>
    ///     Reads a source file with comments and string literals removed, so a
    ///     mention in prose is never mistaken for a dependency.
    /// </summary>
    private static string ReadSource(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }

        return StringPattern
            .Replace(LineCommentPattern.Replace(BlockCommentPattern.Replace(text, " "), " "), "\"\"");
    }

    /// <summary>
    ///     Path of a source file relative to its project directory, always with
    ///     forward slashes so the value compares equal to the exception tables
    ///     regardless of host OS (Windows separators would never match).
    /// </summary>
    /// <remarks>
    ///     #456: since <c>RepoPaths.EnumerateCsFiles</c> started returning linked
    ///     shared-source files, a file can sit outside the project directory while
    ///     being compiled into it. Such a file is named by its <c>..</c>-relative path
    ///     (<c>../Harbor.Storage.Shared/SessionStoreErrors.cs</c>) rather than by bare
    ///     filename: two shared folders could each hold a <c>SessionStoreErrors.cs</c>,
    ///     and an exception scoped to sites would then be able to name either one. Files
    ///     inside the project are unchanged — they still render as
    ///     <c>ViewModels/OnboardingViewModel.cs</c>, so no existing site moves.
    /// </remarks>
    private static string ProjectRelative(string path, string projectDir)
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return Path.GetFileName(path);
        }

        // Absolute on both sides: the test host's working directory is the test
        // bin folder, not the repository root, so a relative prefix never matches.
        string full = Path.GetFullPath(path);
        string projectRoot = Path.GetFullPath(Path.Combine(root, "src", projectDir));
        string prefix = projectRoot + Path.DirectorySeparatorChar;

        string relative = full.StartsWith(prefix, StringComparison.Ordinal)
            ? full[prefix.Length..]
            : Path.GetRelativePath(projectRoot, full);

        return relative.Replace('\\', '/');
    }
}
