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

namespace Harbor.Architecture.Tests;
/// <summary>Everything the rule needs to know about one type.</summary>
/// <param name="TypeName">Full CLR name.</param>
/// <param name="Assembly">Simple assembly name.</param>
/// <param name="BaseTypeName">Full CLR name of the base type, or null for <c>System.Object</c>.</param>
/// <param name="IsAbstract">Abstract types are the template, not a family member.</param>
/// <param name="IsRenderer">
///     Whether the type is, or derives from, the abstract base renderer. Read by
///     reflection, which is exact — Cecil's type resolution is not consulted for
///     the hierarchy at all; see the probe's remarks for why.
/// </param>
/// <param name="CallsRegisterHandler">
///     One of the type's OWN method bodies calls
///     <c>BaseTuiRenderer.RegisterHandler</c>.
/// </param>
/// <param name="NewStoresChatAppMsgAgent">The type news up a <c>ChatAppMsg.Agent</c>.</param>
/// <param name="BridgeFieldTypes">
///     Full CLR names of the type's own field types. A renderer that pushes
///     events through a sibling bridge (<c>TerminalGuiRenderer</c> →
///     <c>TerminalGuiTeaBridge</c>) takes the store seam one hop away, and the
///     hop is real: the bridge dispatches into the same <c>UiStore</c>.
/// </param>
internal sealed record RendererFacts(
    string TypeName,
    string Assembly,
    string? BaseTypeName,
    bool IsAbstract,
    bool IsRenderer,
    bool CallsRegisterHandler,
    bool NewStoresChatAppMsgAgent,
    IReadOnlyList<string> BridgeFieldTypes);

/// <summary>One renderer that reaches neither seam.</summary>
internal sealed record RendererSeamViolation(string TypeName, string Assembly, string Why);

/// <summary>
///     Probe for the renderer event seam: the type table and the hierarchy come
///     from <b>reflection</b>, the call sites from <b>IL</b> (Mono.Cecil).
/// </summary>
/// <remarks>
/// <para>
///     <b>Why the split.</b> The first version walked the base chain with
///     Cecil's <c>TypeReference.Resolve()</c>, which silently yielded null at
///     the first hop across an assembly boundary. On this very test assembly it
///     therefore found 1 of the 3 renderers the file declares, and the
///     positive control went quietly green — which is exactly the failure the
///     non-vacuity tests exist to prevent, caught by them. Reflection answers
///     "is this a renderer, what is its base, what are its fields" exactly, and
///     Cecil answers only "does this method body call X", which needs no
///     resolver because a call operand's declaring type is a name away.
/// </para>
/// <para>
///     <b>Package provenance:</b> Mono.Cecil arrives transitively with
///     <c>NetArchTest.Rules</c> (<c>exclude="Build,Analyzers"</c> in its nuspec)
///     — the same provenance <c>PresentationCapabilityRules.cs</c> relies on. No
///     new <c>PackageReference</c>, no new restore entry.
/// </para>
/// </remarks>
internal static class RendererSeamProbe
{
    /// <summary>Full name of the abstract base every renderer family derives from.</summary>
    internal const string BaseRendererTypeName = "Harbor.Terminal.Abstractions.BaseTuiRenderer";

    /// <summary>Full CLR name of the store-seam message the convention names.</summary>
    internal const string ChatAppMsgAgentTypeName = "Harbor.Ui.Framework.State.ChatAppMsg/Agent";

    /// <summary>Name of the handler-registration method on the base class.</summary>
    internal const string RegisterHandlerMethodName = "RegisterHandler";

    /// <summary>
    ///     Every renderer in <paramref name="asm" />, plus every bridge type one
    ///     of them holds — the bridge is not a renderer, but its seam is the
    ///     renderer's seam one hop away, so it has to be in the table for
    ///     <c>Evaluate</c> to look it up.
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

        // Pass 1 — IL. Keyed by full CLR name, covering EVERY type in the module
        // (not just renderers), because a bridge's seam has to be findable.
        using AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(path);
        var seams = new Dictionary<string, (bool Register, bool Store)>(StringComparer.Ordinal);
        int topLevel = 0;
        foreach (TypeDefinition type in definition.MainModule.Types)
        {
            topLevel++;
            CollectSeams(type, seams);
        }

        topLevelTypeCount = topLevel;

        // Pass 2 — reflection, for the type table and the hierarchy.
        var found = new Dictionary<string, RendererFacts>(StringComparer.Ordinal);
        foreach (Type type in SafeGetTypes(asm))
        {
            if (!typeof(BaseTuiRenderer).IsAssignableFrom(type))
            {
                continue;
            }

            string typeName = type.FullName ?? type.Name;
            seams.TryGetValue(typeName, out (bool Register, bool Store) seam);
            found[typeName] = new RendererFacts(
                typeName,
                simpleName,
                type.BaseType?.FullName,
                type.IsAbstract,
                IsRenderer: true,
                seam.Register,
                seam.Store,
                BridgeFieldTypes(type));
        }

        // Pass 3 — the bridges. Not renderers, so pass 2 skipped them, but a
        // renderer that delegates to one takes the store seam through it.
        foreach (RendererFacts renderer in found.Values.ToList())
        {
            foreach (string bridge in renderer.BridgeFieldTypes)
            {
                if (found.ContainsKey(bridge))
                {
                    continue;
                }

                Type? bridgeType = asm.GetType(bridge);
                if (bridgeType is null)
                {
                    continue;
                }

                seams.TryGetValue(bridge, out (bool Register, bool Store) bridgeSeam);
                found[bridge] = new RendererFacts(
                    bridge,
                    simpleName,
                    bridgeType.BaseType?.FullName,
                    bridgeType.IsAbstract,
                    IsRenderer: false,
                    bridgeSeam.Register,
                    bridgeSeam.Store,
                    []);
            }
        }

        return found.Values.ToList();
    }

    private static IReadOnlyList<string> BridgeFieldTypes(Type type)
    {
        var bridges = new List<string>();
        foreach (FieldInfo field in type.GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            string? fieldType = field.FieldType.FullName;
            if (!string.IsNullOrEmpty(fieldType))
            {
                bridges.Add(fieldType);
            }
        }

        return bridges;
    }

    /// <summary>
    ///     Records, per type, whether any of ITS OWN method bodies takes a
    ///     seam. Nested types are recorded under their own names.
    /// </summary>
    /// <remarks>
    ///     Own methods only, and each type keyed by its own full name. An earlier
    ///     version scanned a type together with all its nested types and stored
    ///     the combined result under the OUTER name — which works for every
    ///     top-level renderer in the tree (they all register from their own
    ///     constructor) and silently reports "no seam" for a nested one. That
    ///     is precisely what the probe renderers in this file are, and the
    ///     non-vacuity test caught it.
    /// </remarks>
    private static void CollectSeams(
        TypeDefinition type,
        Dictionary<string, (bool Register, bool Store)> seams)
    {
        bool register = false;
        bool store = false;

        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (Instruction instruction in method.Body.Instructions)
            {
                if (instruction.Operand is not MethodReference callee)
                {
                    continue;
                }

                // A call operand's declaring type is a plain reference: its
                // FullName is readable without resolving an assembly.
                string? declaring = callee.DeclaringType?.FullName;
                if (callee.Name == RegisterHandlerMethodName && declaring == BaseRendererTypeName)
                {
                    register = true;
                }

                if (callee.Name == ".ctor" && declaring == ChatAppMsgAgentTypeName)
                {
                    store = true;
                }
            }
        }

        if (register || store)
        {
            seams[ReflectionName(type)] = (register, store);
        }

        foreach (TypeDefinition nested in type.NestedTypes)
        {
            CollectSeams(nested, seams);
        }
    }

    /// <summary>
    ///     Cecil spells a nested type <c>Namespace.Outer/Inner</c>; reflection
    ///     spells the same type <c>Namespace.Outer+Inner</c>. The seam table is
    ///     keyed by reflection names (pass 2 looks up with
    ///     <c>Type.FullName</c>), so Cecil names are translated here. Without
    ///     this, every top-level type matches — which is all of the production
    ///     renderers — and every nested one silently misses.
    /// </summary>
    private static string ReflectionName(TypeDefinition type) =>
        type.FullName.Replace('/', '+');

    private static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static t => t is not null).Select(static t => t!);
        }
    }

    /// <summary>
    ///     Total instruction count across every method body in
    ///     <paramref name="asm" />. Exposed so a non-vacuity test can assert the
    ///     IL walk is reading real instructions rather than a method table — a
    ///     probe that walked zero bodies would still "find" every type.
    /// </summary>
    internal static int TotalInstructionCount(Assembly asm)
    {
        string? simpleName = asm.GetName().Name;
        if (string.IsNullOrEmpty(simpleName))
        {
            throw new InvalidOperationException("[renderer-seam] assembly has no simple name.");
        }

        using AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(
            Path.Combine(AppContext.BaseDirectory, simpleName + ".dll"));

        int total = 0;
        foreach (TypeDefinition type in definition.MainModule.Types)
        {
            foreach (MethodDefinition method in type.Methods)
            {
                if (method.HasBody)
                {
                    total += method.Body.Instructions.Count;
                }
            }
        }

        return total;
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
        await Assert.That(RendererSeamProbe.TotalInstructionCount(self)).IsGreaterThan(0)
            .Because("the IL walk must be reading real method bodies. A probe that walked the type "
                   + "table but no instructions would still 'find' every type and report no seam, "
                   + "which is the vacuous shape this file is built to rule out");

        await Assert.That(facts.Count).IsGreaterThanOrEqualTo(3)
            .Because("this file declares three types below that reach BaseTuiRenderer: the abstract "
                   + "ProbeRendererBase and the two concrete probes. If the probe cannot see all of "
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
            scanned,
            ExcludedAssemblies: []);

        // Select before ToHashSet: a StringComparer cannot be an
        // IEqualityComparer<RendererSeamViolation>, so the set has to be of
        // the type NAMES.
        var reported = Evaluate(inventory)
            .Select(static v => v.TypeName)
            .ToList();

        // Matched by suffix, not by a spelled-out full name: the probes are
        // nested two deep (RendererEventSeamRule+ProbeRendererBase+X), and
        // hard-coding the separator is exactly the kind of detail that turns
        // this test into a false negative when it changes.
        bool reportsSeamless = reported.Exists(
            static n => n.EndsWith("+SeamlessRenderer", StringComparison.Ordinal));
        bool reportsRegistered = reported.Exists(
            static n => n.EndsWith("+HandlerRegisteredRenderer", StringComparison.Ordinal));

        await Assert.That(reportsSeamless).IsTrue()
            .Because("SeamlessRenderer in this very file derives from BaseTuiRenderer and reaches "
                   + "neither seam — the exact shape the rule exists to catch. If the rule does not "
                   + "report it, the rule is not running. Reported: " + string.Join(", ", reported));

        await Assert.That(reportsRegistered).IsFalse()
            .Because("HandlerRegisteredRenderer calls RegisterHandler, so it takes the declared "
                   + "handler seam; reporting it would mean the rule flags conforming renderers too. "
                   + "Reported: " + string.Join(", ", reported));
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

        // The exclusion list is the same trap one level up: an assembly quietly
        // dropped from it stops being enforced and the rule goes green without
        // saying so. Exactly one assembly may be excluded, and it must be the
        // one these tests live in.
        string self = typeof(RendererEventSeamRule).Assembly.GetName().Name ?? "<unnamed>";
        await Assert.That(inventory.ExcludedAssemblies.Count).IsEqualTo(1)
            .Because("only this test assembly may be excluded from the rule, and only because it "
                   + "declares the deliberate non-conformant probes. Excluded: "
                   + string.Join(", ", inventory.ExcludedAssemblies.Select(static e => e.Name)));

        await Assert.That(inventory.ExcludedAssemblies[0].Name).IsEqualTo(self)
            .Because("the excluded assembly must be the one these tests live in; a typo there would "
                   + "grandf a production assembly");
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
    /// <param name="ExcludedAssemblies">
    ///     Assemblies deliberately left out of the rule, each with the reason.
    ///     Asserted to be exactly this test assembly, whose two probe renderers
    ///     are DELIBERATE violations — they exist so the rule can be shown to
    ///     fire. Without the exclusion the rule would be red on its own controls.
    /// </param>
    private sealed record RenderersInventory(
        Dictionary<string, RendererFacts> Renderers,
        int TopLevelTypesScanned,
        IReadOnlyList<(string Name, string Why)> ExcludedAssemblies)
    {
        /// <summary>Number of non-abstract renderers — the family list the rule is about.</summary>
        public int ConcreteRendererCount =>
            Renderers.Values.Count(facts => facts.IsRenderer && !facts.IsAbstract);
    }

    private static RenderersInventory Build()
    {
        var renderers = new Dictionary<string, RendererFacts>(StringComparer.Ordinal);
        var excluded = new List<(string, string)>();
        int scanned = 0;
        string selfName = typeof(RendererEventSeamRule).Assembly.GetName().Name ?? "<unnamed>";

        foreach ((string name, Assembly asm) in LoadedAssemblies.Value)
        {
            if (string.Equals(name, selfName, StringComparison.Ordinal))
            {
                excluded.Add((name,
                    "this test assembly declares the two DELIBERATE non-conformant probe "
                    + "renderers the non-vacuity tests assert the rule catches"));
                continue;
            }

            // Cheap pre-filter: only the assemblies that actually carry a
            // renderer are opened. A reflection miss therefore cannot hide a
            // renderer, because the pre-filter is the same exact call the probe
            // makes and the liveness test names four of the families.
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

        return new RenderersInventory(renderers, scanned, excluded);
    }

    /// <summary>
    ///     Whether any loaded type in <paramref name="asm" /> derives from
    ///     <see cref="BaseTuiRenderer" />. Reflection only — it is the exact
    ///     answer, and it is the same call the probe makes, so the pre-filter
    ///     cannot disagree with the scan it guards.
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

            if (!visiting.Add(typeName))
            {
                // A cycle in a base chain. The type that closes it is the one
                // that gets judged, not this one.
                return false;
            }

            bool result;

            if (!byTypeName.TryGetValue(typeName, out RendererFacts? facts))
            {
                // NOT in the table, so NOT a seam we measured. Concluding
                // "conformant" here is the bug that made the first version of
                // this rule pass vacuously: an abstract base nobody had scanned
                // granted conformity to everything under it. A type the probe
                // cannot judge contributes nothing.
                result = false;
            }
            else
            {
                result = facts.CallsRegisterHandler || facts.NewStoresChatAppMsgAgent;

                // Inherit along the BASE chain only while the base is itself a
                // renderer. `BaseTuiRenderer` is abstract and takes no seam, so
                // walking into it must end the walk, not grant conformity.
                if (!result && facts.IsRenderer && facts.BaseTypeName is { } baseName
                    && byTypeName.TryGetValue(baseName, out RendererFacts? baseFacts)
                    && baseFacts.IsRenderer)
                {
                    result = IsConformant(baseName, visiting);
                }

                // The one legitimate hop for a non-renderer: a bridge field.
                if (!result)
                {
                    foreach (string bridgeType in facts.BridgeFieldTypes)
                    {
                        if (byTypeName.ContainsKey(bridgeType)
                            && IsConformant(bridgeType, visiting))
                        {
                            result = true;
                            break;
                        }
                    }
                }
            }

            visiting.Remove(typeName);
            conformant[typeName] = result;
            return result;
        }

        var violations = new List<RendererSeamViolation>();
        foreach (RendererFacts facts in inventory.Renderers.Values)
        {
            if (!facts.IsRenderer
                || facts.IsAbstract
                || IsConformant(facts.TypeName, new HashSet<string>(StringComparer.Ordinal)))
            {
                continue;
            }

            string why =
                $"no call to RegisterHandler, no `new ChatAppMsg.Agent(`, "
                + $"no bridge field that takes one ({string.Join(", ", facts.BridgeFieldTypes)}), "
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
