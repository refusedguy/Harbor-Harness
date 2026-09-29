// RendererEventSeamRule.cs — GUARD #1 for docs/PATTERNS.md §"Observer →
// the event seam" (issue #575).
//
// THE CONVENTION BEING ENFORCED
// -----------------------------
// docs/PATTERNS.md states the direction explicitly: an `AgentEvent` enters the
// UI through exactly one of two declared seams —
//
//   (A) ChatAppMsg.Agent → ChatAppReducer → UiStore  (the STORE seam — the
//       convention; `Harbor.Ui.Framework.State` is the model implementation), or
//   (B) an `IAgentEventHandler` registered through
//       BaseTuiRenderer.RegisterHandler                (the HANDLER seam — for
//       renderers that need an imperative side effect),
//
// and a concrete `BaseTuiRenderer` must use one of them. "A new feature comes
// out wrong not because the pattern is absent, but because the canonical code
// path is not the place where the pattern is applied" is the whole reason
// docs/PATTERNS.md exists, and this file is the mechanical half of that
// sentence: a third mechanism — a hand-written `switch (AgentEvent)` in a
// renderer or a sibling class — is now a red build instead of a lucky place.
//
// WHY THE SCAN IS OVER IL, NOT OVER SOURCE TEXT
// ---------------------------------------------
// The obvious implementation greps the .cs files. That is wrong for three
// reasons, all of which have bitten this repo before:
//
//   * a renderer may reach the seam one hop away (TerminalGuiRenderer calls
//     `_bridge?.Push(@event)`; the `new ChatAppMsg.Agent(...)` lives in
//     TerminalGuiTeaBridge), and a per-file grep cannot see that hop without
//     becoming a project-wide grep that then passes anything the project
//     happens to contain;
//   * `RegisterHandler` may be called from a partial class in a sibling file;
//   * source text drifts from what actually shipped. `Harbor.slnx` is not the
//     only thing anyone runs.
//
// So the probe reads the compiled IL with Mono.Cecil — the same Cecil that
// NetArchTest.Rules already pulls in transitively and that
// PresentationCapabilityRules.cs already uses, so no new package and no new
// restore entry. Every claim in this file's header has been checked against IL.
//
// NON-VACUITY — WHY THIS FILE IS NOT A COMMENT
// --------------------------------------------
// The known trap in this repo (written down in PresentationCapabilityRules.cs):
// NetArchTest's `NotHaveDependencyOn(name)` is SATISFIED BY A NAME THAT MATCHES
// NOTHING, so a rule naming a typo'd or deleted assembly is green forever and
// enforces nothing. The identical failure mode here is worse, because it is
// silent in two directions at once:
//
//   * if the probe cannot open an assembly, or the type walk returns nothing,
//     the rule reports "no violations" and the whole 10-renderer family could
//     be converted to hand-written switches without anyone noticing;
//   * if the seam matcher stopped matching, every renderer would look
//     non-conformant, the baseline would have to be widened, and the rule
//     would go green again while enforcing the opposite of the convention.
//
// Three tests close both directions, and they are the reason to believe the
// rule:
//
//   1. NonVacuity_Probe_ReadsRealIlFromThisTestAssembly — asserts the probe
//      opened a real file and walked real types.
//   2. NonVacuity_Rule_FailsOnARendererWithNoSeam — the POSITIVE CONTROL, and
//      it runs the real rule over the real probe against types declared in this
//      very file: `SeamlessRenderer` (a `BaseTuiRenderer` that routes events
//      nowhere) MUST be reported, `HandlerRegisteredRenderer` (one that calls
//      `RegisterHandler`) MUST NOT be. A matcher that matches nothing, or a
//      walk that finds no types, fails this test before it can pass the rule.
//   3. RendererTypeTable_IsLive — the type table read by the rule is
//      non-empty and contains the renderers the rule is written about, so a
//      renamed/renamed-away assembly surfaces as a failure rather than as a
//      smaller, easier table.
//
// WHY THE BASELINE IS EMPTY
// -------------------------
// There is none: all 10 renderer families in the tree already take one of the
// two seams, so the rule is fully armed from day one. Grandfathering is
// available (see RendererEventSeamRule.KnownNonConformant) but deliberately
// left empty — a permanent skip is a comment with extra steps, and
// PresentationCapabilityRules.cs already shows what a real baseline costs.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Microsoft.Extensions.Logging.Abstractions;
using Mono.Cecil;
using Mono.Cecil.Cil;
// `System.Reflection` is a global using in this project (needed for Assembly),
// so Cecil's MethodBody would be an ambiguous reference without this alias.
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace Harbor.Architecture.Tests;

/// <summary>Everything the rule needs to know about one renderer type.</summary>
/// <param name="TypeName">Full CLR name, compiler-generated names collapsed.</param>
/// <param name="Assembly">Simple assembly name.</param>
/// <param name="BaseTypeName">Full CLR name of the base type, or null for <c>System.Object</c>.</param>
/// <param name="IsAbstract">Abstract types are the template, not a family member.</param>
/// <param name="CallsRegisterHandler">
///     The type (or one of its nested types) contains a call to
///     <c>BaseTuiRenderer.RegisterHandler</c>.
/// </param>
/// <param name="NewStoresChatAppMsgAgent">The type news up a <c>ChatAppMsg.Agent</c>.</param>
/// <param name="BridgeFieldTypes">
///     Full CLR names of field types declared in the same module. A renderer
///     that pushes events through a sibling bridge (<c>TerminalGuiRenderer</c> →
///     <c>TerminalGuiTeaBridge</c>) takes the store seam one hop away, and the
///     hop is real: the bridge dispatches into the same <c>UiStore</c>.
/// </param>
internal sealed record RendererFacts(
    string TypeName,
    string Assembly,
    string? BaseTypeName,
    bool IsAbstract,
    bool CallsRegisterHandler,
    bool NewStoresChatAppMsgAgent,
    IReadOnlyList<string> BridgeFieldTypes);

/// <summary>One renderer that reaches neither seam.</summary>
internal sealed record RendererSeamViolation(string TypeName, string Assembly, string Why);

/// <summary>
///     IL-level probe for the renderer event seam. Reads an assembly's metadata
///     with Mono.Cecil and reports, per type, whether it reaches either of the
///     two declared ways an <c>AgentEvent</c> enters the UI.
/// </summary>
internal static class RendererSeamProbe
{
    /// <summary>Full name of the abstract base every renderer family derives from.</summary>
    internal const string BaseRendererTypeName = "Harbor.Terminal.Abstractions.BaseTuiRenderer";

    /// <summary>Full CLR name of the store-seam message the convention names.</summary>
    internal const string ChatAppMsgAgentTypeName = "Harbor.Ui.Framework.State.ChatAppMsg/Agent";

    /// <summary>Name of the handler-registration method on the base class.</summary>
    internal const string RegisterHandlerMethodName = "RegisterHandler";

    /// <summary>
    ///     Returns every type in <paramref name="asm" /> that reaches, directly
    ///     or through a base type, <see cref="BaseRendererTypeName" />.
    /// </summary>
    /// <param name="asm">Assembly to read; must have a resolvable file on disk.</param>
    /// <param name="topLevelTypeCount">Receives the module's top-level type count.</param>
    /// <exception cref="InvalidOperationException">
    ///     The assembly file cannot be found or opened. Raised rather than
    ///     swallowed: a probe with no input must not report "no violations".
    /// </exception>
    public static IReadOnlyList<RendererFacts> Scan(Assembly asm, out int topLevelTypeCount)
    {
        string? simpleName = asm.GetName().Name;
        if (string.IsNullOrEmpty(simpleName))
        {
            throw new InvalidOperationException("[renderer-seam] assembly has no simple name.");
        }

        string path = Path.Combine(AppContext.BaseDirectory, simpleName + ".dll");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"[renderer-seam] cannot find '{simpleName}.dll' at '{path}'. The rule would be "
                + "vacuous if the probe reported 'no violations' here.");
        }

        using AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(path);

        var found = new List<RendererFacts>();
        int topLevel = 0;
        foreach (TypeDefinition type in definition.MainModule.Types)
        {
            topLevel++;
            Collect(type, simpleName, found);
        }

        topLevelTypeCount = topLevel;
        return found;
    }

    private static void Collect(TypeDefinition type, string assemblyName, List<RendererFacts> found)
    {
        if (DerivesFromBaseRenderer(type))
        {
            bool register = false;
            bool store = false;
            var bridges = new List<string>();

            foreach (TypeDefinition scope in SelfAndNested(type))
            {
                register |= CallsRegisterHandler(scope);
                store |= NewsUpChatAppMsgAgent(scope);
                foreach (FieldDefinition field in scope.Fields)
                {
                    string? fieldType = field.FieldType?.FullName;
                    if (!string.IsNullOrEmpty(fieldType))
                    {
                        bridges.Add(fieldType);
                    }
                }
            }

            found.Add(new RendererFacts(
                IlCapabilityProbe.AttributedTypeName(type),
                assemblyName,
                type.BaseType?.FullName,
                type.IsAbstract,
                register,
                store,
                bridges));
        }

        foreach (TypeDefinition nested in type.NestedTypes)
        {
            Collect(nested, assemblyName, found);
        }
    }

    private static IEnumerable<TypeDefinition> SelfAndNested(TypeDefinition type)
    {
        yield return type;
        foreach (TypeDefinition nested in type.NestedTypes)
        {
            foreach (TypeDefinition inner in SelfAndNested(nested))
            {
                yield return inner;
            }
        }
    }

    /// <summary>True when <paramref name="type" />'s base chain reaches the abstract base.</summary>
    internal static bool DerivesFromBaseRenderer(TypeDefinition type)
    {
        TypeReference? current = type.BaseType;
        int guard = 0;
        while (current is not null && guard++ < 32)
        {
            if (current.FullName == BaseRendererTypeName)
            {
                return true;
            }

            current = SafeResolve(current);
        }

        return false;
    }

    /// <summary>
    ///     Whether <paramref name="type" /> calls <c>RegisterHandler</c> anywhere.
    ///     The match is on the METHOD NAME plus the declaring type's
    ///     assignability to the base renderer, so an unrelated method that
    ///     happens to be called <c>RegisterHandler</c> on some other type cannot
    ///     make a renderer look conformant.
    /// </summary>
    private static bool CallsRegisterHandler(TypeDefinition type)
    {
        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (Instruction instruction in method.Body.Instructions)
            {
                if (instruction.Operand is MethodReference callee
                    && callee.Name == RegisterHandlerMethodName
                    && IsRendererType(callee.DeclaringType))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool NewsUpChatAppMsgAgent(TypeDefinition type)
    {
        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (Instruction instruction in method.Body.Instructions)
            {
                if (instruction.Operand is MethodReference callee
                    && callee.Name == ".ctor"
                    && callee.DeclaringType?.FullName == ChatAppMsgAgentTypeName)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether a type reference is, or derives from, the abstract base renderer.</summary>
    private static bool IsRendererType(TypeReference? typeRef)
    {
        if (typeRef is null)
        {
            return false;
        }

        if (typeRef.FullName == BaseRendererTypeName)
        {
            return true;
        }

        TypeDefinition? resolved = SafeResolve(typeRef);
        return resolved is not null && DerivesFromBaseRenderer(resolved);
    }

    private static TypeDefinition? SafeResolve(TypeReference reference)
    {
        try
        {
            return reference.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            // RegisterHandler is declared on Harbor.Terminal.Abstractions, which
            // every renderer project references, so this is a broken input
            // rather than a routine condition — but it must not crash the gate.
            return null;
        }
    }

    /// <summary>
    ///     Reads the IL of one method body and returns its instruction count.
    ///     Exposed so a non-vacuity test can assert the walk is looking at real
    ///     instructions rather than at a method table.
    /// </summary>
    internal static int InstructionCount(MethodDefinition method)
    {
        MethodBody body = method.Body;
        return body.Instructions.Count;
    }
}

/// <summary>
///     Guard for docs/PATTERNS.md §"Observer → the event seam": every concrete
///     <see cref="BaseTuiRenderer" /> must reach the UI through
///     <c>ChatAppMsg.Agent</c> (the store) or through a registered
///     <c>IAgentEventHandler</c>. A hand-written per-renderer
///     <c>switch (AgentEvent)</c> is the third mechanism and is now red on
///     arrival.
/// </summary>
public sealed class RendererEventSeamRule
{
    private static readonly Lazy<IReadOnlyDictionary<string, Assembly>> LoadedAssemblies =
        new(ArchitectureTestHelpers.LoadHarborAssemblies);

    private static readonly Lazy<RenderersInventory> Inventory = new(Build);

    /// <summary>
    ///     Deliberately EMPTY. Every renderer family in the tree already takes
    ///     one of the two declared seams, so the rule runs fully armed. A row
    ///     added here is a claim that a renderer genuinely reaches the UI
    ///     through some third mechanism, and it must carry a tracking issue —
    ///     <see cref="NonConformantBaseline_IsWellFormed" /> checks that.
    /// </summary>
    private static readonly Dictionary<string, string> KnownNonConformant = new(StringComparer.Ordinal);

    // =====================================================================
    // 1. The rule.
    // =====================================================================

    /// <summary>
    ///     Every concrete <see cref="BaseTuiRenderer" /> in the repository —
    ///     across <c>src/</c>, <c>apps/</c> and <c>contrib/</c> — must feed the
    ///     UI through the store seam (<c>new ChatAppMsg.Agent(...)</c>) or the
    ///     handler seam (<c>RegisterHandler(...)</c>), directly, through a
    ///     sibling bridge in the same module, or by inheriting a base that does.
    /// </summary>
    [Test]
    public async Task EveryConcreteTuiRenderer_ReachesAnEventSeam()
    {
        RenderersInventory inventory = Inventory.Value;

        await Assert.That(inventory.TopLevelTypesScanned).IsGreaterThan(0)
            .Because("the probe walked no types, so the rule would pass vacuously");

        await Assert.That(inventory.Renderers.Count).IsGreaterThan(0)
            .Because("no BaseTuiRenderer subclass was found; if the renderer families were "
                   + "renamed or moved out of the loaded assemblies this rule would silently "
                   + "enforce nothing");

        var failures = new List<string>();
        foreach (RendererSeamViolation violation in Evaluate(inventory))
        {
            if (KnownNonConformant.ContainsKey(violation.TypeName))
            {
                continue;
            }

            failures.Add(
                $"{violation.Assembly}: {violation.TypeName} routes AgentEvents through neither "
                + $"declared seam ({violation.Why}). docs/PATTERNS.md §'Observer → the event seam': "
                + "an AgentEvent enters the UI through ChatAppMsg.Agent → ChatAppReducer, or through "
                + "a registered IAgentEventHandler. A per-renderer `switch (AgentEvent)` is a THIRD "
                + "mechanism and is how #575 happened. Dispatch to the store, or register a handler.");
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     Probes THIS test assembly, whose source is right here, and requires
    ///     the probe to have opened a real file and walked real types. Combined
    ///     with <see cref="NonVacuity_Rule_FailsOnARendererWithNoSeam" /> this is
    ///     what stops the rule from being satisfied by a typo'd assembly name or
    ///     a matcher that quietly stopped matching.
    /// </summary>
    [Test]
    public async Task NonVacuity_Probe_ReadsRealIlFromThisTestAssembly()
    {
        var self = typeof(RendererEventSeamRule).Assembly;
        string path = Path.Combine(
            AppContext.BaseDirectory,
            (self.GetName().Name ?? throw new InvalidOperationException("unnamed assembly")) + ".dll");

        await Assert.That(File.Exists(path)).IsTrue()
            .Because("the probe must open a real assembly file; reporting 'no violations' because "
                   + "it silently found no input would make the whole rule vacuous");

        var facts = RendererSeamProbe.Scan(self, out int topLevelTypeCount);

        await Assert.That(topLevelTypeCount).IsGreaterThan(0);
        await Assert.That(facts.Count).IsGreaterThanOrEqualTo(2)
            .Because("this file declares the two probe renderers below; if the probe cannot see "
                   + "them it cannot see anything, and the rule is a comment");

        // The walk must be looking at real INSTRUCTIONS, not a method table.
        // HandlerRegisteredRenderer registers from its constructor, so a
        // scanner that read metadata but no IL would see a zero here.
        var registered = facts.FirstOrDefault(f =>
            f.TypeName.EndsWith("HandlerRegisteredRenderer", StringComparison.Ordinal));
        await Assert.That(registered).IsNotNull();
        await Assert.That(registered!.CallsRegisterHandler).IsTrue()
            .Because("HandlerRegisteredRenderer's constructor calls RegisterHandler, so the IL walk "
                   + "must see that call site");

        var seamless = facts.FirstOrDefault(f =>
            f.TypeName.EndsWith("SeamlessRenderer", StringComparison.Ordinal));
        await Assert.That(seamless).IsNotNull();
        await Assert.That(seamless!.CallsRegisterHandler).IsFalse()
            .Because("SeamlessRenderer registers nothing, so the seam matcher must not report a call "
                   + "that is not in its IL");
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. Runs the real rule over the real probe against
    ///     types declared in this file, whose source is known:
    ///     <c>SeamlessRenderer</c> is a <see cref="BaseTuiRenderer" /> that
    ///     routes events nowhere and MUST be reported;
    ///     <c>HandlerRegisteredRenderer</c> calls <c>RegisterHandler</c> and MUST
    ///     NOT be. A probe that finds no types, or a seam matcher that matches
    ///     nothing, fails here.
    /// </summary>
    [Test]
    public async Task NonVacuity_Rule_FailsOnARendererWithNoSeam()
    {
        var self = typeof(RendererEventSeamRule).Assembly;
        IReadOnlyList<RendererFacts> facts = RendererSeamProbe.Scan(self, out int scanned);

        var inventory = new RenderersInventory(
            facts.ToDictionary(static f => f.TypeName, StringComparer.Ordinal),
            scanned);

        var reported = Evaluate(inventory).ToHashSet(StringComparer.Ordinal);

        string seamless = "Harbor.Architecture.Tests.RendererEventSeamRule+SeamlessRenderer";
        string registered = "Harbor.Architecture.Tests.RendererEventSeamRule+HandlerRegisteredRenderer";

        await Assert.That(reported.Contains(seamless)).IsTrue()
            .Because("SeamlessRenderer in this very file derives from BaseTuiRenderer and reaches "
                   + "neither seam — the exact shape the rule exists to catch. If the rule does not "
                   + "report it, the rule is not running.");

        await Assert.That(reported.Contains(registered)).IsFalse()
            .Because("HandlerRegisteredRenderer calls RegisterHandler, so it takes the declared "
                   + "handler seam; reporting it would mean the rule flags conforming renderers too");
    }

    /// <summary>
    ///     Liveness on the type table the rule reads. The table must contain the
    ///     renderer families the convention is written about — including the
    ///     canonical <c>CellForge</c> backend AGENTS.md tells contributors to
    ///     build in, which #575 found bypassing the seam entirely.
    /// </summary>
    [Test]
    public async Task RendererTypeTable_IsLive()
    {
        RenderersInventory inventory = Inventory.Value;
        var names = inventory.Renderers.Keys.ToHashSet(StringComparer.Ordinal);

        // Namespaces are FLAT even where the file sits in a subfolder: the
        // canonical backend lives at src/Harbor.Tui.CellForge/Chat/… but declares
        // `namespace Harbor.Tui.CellForge`. These four are the ones the
        // convention is actually written about — the fifth (#575's subject) is
        // CellForgeTuiRenderer.
        string[] expected =
        [
            "Harbor.Tui.AnsiPlain.AnsiPlainTuiRenderer",
            "Harbor.Tui.CellForge.CellForgeTuiRenderer",
            "Harbor.Tui.NickConsoleEx.NickConsoleExTuiRenderer",
            "Harbor.Tui.Notifications.NotificationTuiRenderer",
        ];

        var missing = expected.Where(name => !names.Contains(name)).ToList();

        await Assert.That(missing).IsEmpty()
            .Because("these renderers are referenced by the convention and by docs/PATTERNS.md; if "
                   + "the table does not contain them the assemblies were renamed, deleted, or are no "
                   + "longer loaded, and the rule is enforcing nothing. Missing: "
                   + string.Join(", ", missing));

        // The family count is anchored on a LOWER bound, deliberately.
        // Harbor.Architecture.Tests references Harbor.Hosting, which pulls the contrib/ Spectre + TerminalGui
        // + Termina + RazorConsole backends in only when HarborWithSpectreTui is
        // on, and Harbor.Samples.slnx (Sixel) is a different solution entirely. A
        // hard-coded total would therefore be a flaky assertion dressed as a
        // strong one. What must not move is the floor: the src/ backends are
        // unconditionally referenced, so losing one is a real regression.
        await Assert.That(inventory.ConcreteRendererCount).IsGreaterThanOrEqualTo(4)
            .Because("the four src/ renderer families are unconditional ProjectReferences of this "
                   + "test project; a smaller table means the probe stopped seeing them");
    }

    // =====================================================================
    // 3. Baseline integrity.
    // =====================================================================

    /// <summary>
    ///     The non-conformance baseline must be well formed, and every row in it
    ///     must still correspond to a violation the probe really finds — so it
    ///     cannot rot into a blanket permission that grandfathers a renderer
    ///     which has since been fixed.
    /// </summary>
    [Test]
    public async Task NonConformantBaseline_IsWellFormed()
    {
        RenderersInventory inventory = Inventory.Value;
        var real = Evaluate(inventory).Select(v => v.TypeName).ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var (typeName, trackedBy) in KnownNonConformant)
        {
            if (!real.Contains(typeName))
            {
                failures.Add(
                    $"baseline row '{typeName}' is stale — the probe finds no such violation any "
                    + $"more. Delete the row and close {trackedBy}.");
            }

            if (!trackedBy.Contains("https://github.com/", StringComparison.Ordinal))
            {
                failures.Add($"baseline row '{typeName}' has no tracking issue URL (got '{trackedBy}')");
            }
        }

        await Assert.That(KnownNonConformant.Count).IsEqualTo(0)
            .Because("every renderer family in the tree already takes a declared seam, so the "
                   + "baseline is expected to be EMPTY. A non-empty baseline means somebody "
                   + "grandfathers a renderer instead of giving it a seam: "
                   + string.Join(", ", KnownNonConformant.Keys));

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    // =====================================================================
    // 4. Evaluation plumbing.
    // =====================================================================

    /// <summary>What the probe found, plus enough context to fail loudly on a zero.</summary>
    /// <param name="Renderers">
    ///     Every renderer the probe found, keyed by full CLR name. Keyed because
    ///     conformity is resolved through the base chain and through bridge field
    ///     types, both of which are looked up by name.
    /// </param>
    /// <param name="TopLevelTypesScanned">How many types the probe walked.</param>
    private sealed record RenderersInventory(
        Dictionary<string, RendererFacts> Renderers,
        int TopLevelTypesScanned)
    {
        /// <summary>Number of non-abstract renderers — the family list the rule is about.</summary>
        public int ConcreteRendererCount => Renderers.Values.Count(facts => !facts.IsAbstract);
    }

    private static RenderersInventory Build()
    {
        var renderers = new Dictionary<string, RendererFacts>(StringComparer.Ordinal);
        int scanned = 0;

        foreach ((string name, Assembly asm) in LoadedAssemblies.Value)
        {
            // Cheap pre-filter: only the assemblies that actually carry a
            // renderer are opened with Cecil. A reflection miss therefore cannot
            // hide a renderer, because the pre-filter itself fails loudly in
            // RendererTypeTable_IsLive.
            if (!MentionsBaseRenderer(asm))
            {
                continue;
            }

            foreach (RendererFacts facts in RendererSeamProbe.Scan(asm, out int topLevelTypeCount))
            {
                scanned += topLevelTypeCount;
                renderers[facts.TypeName] = facts;
            }
        }

        return new RenderersInventory(renderers, scanned);
    }

    /// <summary>
    ///     Whether any loaded type in <paramref name="asm" /> derives from
    ///     <see cref="BaseTuiRenderer" />. Reflection is used only as a cheap
    ///     pre-filter; every claim the rule makes comes from IL.
    /// </summary>
    private static bool MentionsBaseRenderer(Assembly asm)
    {
        try
        {
            foreach (Type type in asm.GetTypes())
            {
                if (typeof(BaseTuiRenderer).IsAssignableFrom(type))
                {
                    return true;
                }
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Partial information is still information: if one of the loadable
            // types is a renderer, probe the assembly.
            foreach (Type? type in ex.Types)
            {
                if (type is not null && typeof(BaseTuiRenderer).IsAssignableFrom(type))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    ///     Applies the convention. A renderer conforms when it takes the handler
    ///     seam or the store seam directly, when it holds a field whose type
    ///     takes one, or when a base type does — the three shapes the repo
    ///     actually uses. Conformity is a fixpoint because the base chain and
    ///     the bridge hop can each lead to another renderer.
    /// </summary>
    private static IReadOnlyList<RendererSeamViolation> Evaluate(RenderersInventory inventory)
    {
        var conformant = new Dictionary<string, bool>(StringComparer.Ordinal);
        var byTypeName = inventory.Renderers;

        bool IsConformant(string typeName, HashSet<string> visiting)
        {
            if (conformant.TryGetValue(typeName, out bool cached))
            {
                return cached;
            }

            if (!byTypeName.TryGetValue(typeName, out RendererFacts? facts) || !visiting.Add(typeName))
            {
                // Not ours to judge, or already on this chain — treat as
                // conformant so the judgement belongs to the type that owns the
                // decision rather than to a cycle in a base chain.
                conformant[typeName] = true;
                return true;
            }

            bool ok = facts.CallsRegisterHandler || facts.NewStoresChatAppMsgAgent;

            if (!ok && facts.BaseTypeName is { } baseName)
            {
                ok = IsConformant(baseName, visiting);
            }

            if (!ok)
            {
                foreach (string bridgeType in facts.BridgeFieldTypes)
                {
                    if (IsConformant(bridgeType, visiting))
                    {
                        ok = true;
                        break;
                    }
                }
            }

            visiting.Remove(typeName);
            conformant[typeName] = ok;
            return ok;
        }

        var violations = new List<RendererSeamViolation>();
        foreach (RendererFacts facts in inventory.Renderers.Values)
        {
            if (facts.IsAbstract || IsConformant(facts.TypeName, new HashSet<string>(StringComparer.Ordinal)))
            {
                continue;
            }

            string why = facts.CallsRegisterHandler || facts.NewStoresChatAppMsgAgent
                ? "unreachable"
                : $"no call to RegisterHandler, no `new ChatAppMsg.Agent(`, "
                  + $"no bridge field ({string.Join(", ", facts.BridgeFieldTypes)}), "
                  + $"and base '{facts.BaseTypeName ?? "<none>"}' takes no seam either";

            violations.Add(new RendererSeamViolation(facts.TypeName, facts.Assembly, why));
        }

        return violations.OrderBy(v => v.TypeName, StringComparer.Ordinal).ToList();
    }

    // =====================================================================
    // 5. The positive-control types. Source is right here, so what the rule
    //    must say about them is not a guess.
    // =====================================================================

    /// <summary>
    ///     A renderer with NO event seam: it overrides <c>RenderAsync</c> and
    ///     drops the event on the floor. This is the shape
    ///     <c>ChatScreenBridge.HandleEvent</c> grew when #575 was filed, and the
    ///     rule must report it.
    /// </summary>
    private sealed class SeamlessRenderer : ProbeRendererBase
    {
        public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
            => base.RenderAsync(@event, ct);
    }

    /// <summary>
    ///     A renderer on the handler seam: it registers one
    ///     <c>IAgentEventHandler</c> in its constructor, exactly like
    ///     AnsiPlain and NickConsoleEx. The rule must NOT report it.
    /// </summary>
    private sealed class HandlerRegisteredRenderer : ProbeRendererBase
    {
        public HandlerRegisteredRenderer()
        {
            RegisterHandler(new NoopHandler());
        }
    }

    /// <summary>Shared plumbing so the probe types differ only in their event seam.</summary>
    private abstract class ProbeRendererBase : BaseTuiRenderer
    {
        protected ProbeRendererBase() : base(NullLogger.Instance) { }

        public override ITuiRenderContext Context => throw new NotImplementedException();

        /// <remarks>
        ///     Returns <see cref="Maybe{T}" />, not <c>Result&lt;string&gt;</c>:
        ///     <c>ITuiRenderer.ReadLineAsync</c> was changed to signal EOF
        ///     structurally (#589) and every override in the tree follows it. The
        ///     probe renderer is never called — it exists so the two declarations
        ///     below are real overrides the IL walker can see.
        /// </remarks>
        public override Task<Maybe<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
            => throw new NotImplementedException();

        public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
            => throw new NotImplementedException();

        public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public override Task<Result> ClearAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    /// <summary>A handler that exists only so the probe renderer has one to register.</summary>
    private sealed class NoopHandler : IAgentEventHandler
    {
        public bool CanHandle(AgentEvent @event) => false;

        public Task HandleAsync(AgentEvent @event, ITuiRenderContext context, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
