// PanelImplementationBoundaryRules.cs — the guard for issue #474.
//
// WHAT #474 SAID, AND WHAT WAS TRUE
// ---------------------------------
// #474 was filed from the 2026-09-28 audit: "`PanelContext.cs:5 — IServiceProvider?
// Services stuffed into a per-frame context bag. 11 `ctx.Services?.GetService<T>()`
// calls across 4 panels… Panels cannot be unit-tested without a full
// ServiceProvider. Fix: inject via IPanelProvider constructor."
//
// Every load-bearing sentence of that is STALE, and the fix it prescribes would
// have destroyed a working seam:
//
//   * `PanelContext.Services` is not an `IServiceProvider`. It is `PanelServices?`
//     — an immutable record of six named, typed, nullable fields
//     (src/Harbor.Ui.Framework.State/Panels/PanelContext.cs:22, PanelServices.cs:34).
//     #470 / PR #573 removed the container. The replacement is documented in the
//     prose of both files, at the exact lines the audit cited.
//   * Zero `ctx.Services?.GetService<T>()` calls remain. The only occurrence of
//     the string "ctx.Services" anywhere under a Panels directory is a comment at
//     CellForgeSubagentsPanel.cs:31 explaining that the locator is gone.
//   * The panels ARE unit-tested, with a hand-built bag and no container:
//     tests/Harbor.Tui.CellForge.Tests/CellForgeBuiltinPanelsTests.cs builds
//     `new PanelServices { Diagnostics = … }` and asserts on rendered rows;
//     CellForgeSubagentsPanelTests.cs builds
//     `new PanelServices { SessionStore = store, Sessions = manager, Store = uiStore }`.
//   * The cited `ChatScreenLayout.cs:909,984,1014,1097,1150` "5 more methods the
//     provider is threaded through" are comments and layout arithmetic. The file
//     has drifted since the audit; there is no provider there.
//
// So there is no port left to do. Zero of the four named panels remain to be
// converted, because all four were converted. This file is what remains.
//
// WHY A BOUNDARY AND NOT THE PRESCRIBED FIX
// -----------------------------------------
// #474's prescription — "inject via IPanelProvider constructor" — is the one change
// that would break panels, and the reason is the thing #474 did not ask about:
//
//     PANELS ARE AN EXTENSION AXIS. THEY ARE NOT ONLY OUR CODE.
//
// Three independent facts in the tree, none of them an opinion:
//
//   1. `ITuiPanelPlugin` is one of the seven axes #555 froze, and
//      `ExtensionAxisFreezeRule.SealedAxes` (:450) pairs it with the door
//      `RegisterPanelProvider`. `EverySealedAxis_IsDispatchedAndOpened` FAILS THE
//      BUILD if either half stops being real. So a third-party plugin contributing
//      a panel is a shipped, sanctioned capability — not a theoretical one.
//   2. The plugin CONSTRUCTS the panel. `ITuiPanelPlugin.RegisterPanels(
//      IPanelRegistry)` hands the host a FINISHED `IPanelProvider` instance;
//      `IPluginLoadHost.RegisterPanelProvider(IPanelProvider)` (:98) stores that
//      instance. The host never news a plugin panel up, so it never gets to run its
//      constructor. Constructor injection at the composition root is therefore
//      STRUCTURALLY IMPOSSIBLE for exactly the panels the axis exists to admit.
//   3. `PluginContext.Services` is an `IServiceCollection`
//      (src/Harbor.Abstractions/Plugins/*:108) — a registration sink, not a
//      container. A plugin can ADD to the graph in `Initialize`; it is handed
//      nothing it could resolve from in `RegisterPanels`.
//
// So the per-frame `PanelContext` is the ONLY channel from host to panel on that
// axis. Deleting it to "fix" #474 would delete the extension axis, and the honest
// statement of the remaining defect is the opposite of #474's: a container in a
// per-frame bag is not a defect to be removed, it is a BOUNDARY to be drawn. The
// container is gone (#470). What was never drawn is the line that says who may hold
// one — and the panels are where that line has to be, because the panels are the
// code that a future contributor will edit, in the one directory where a locator is
// easiest to reach for and hardest to notice.
//
// WHAT IS RULED
// -------------
//   1. NO PANEL STORES A LOCATOR. Every concrete `IPanelProvider` in a product
//      assembly keeps no `IServiceProvider` in a field and takes none in a
//      constructor. This is the implementer-side half of the boundary.
//      `ServiceLocatorBoundaryRules` already rules the CONTRACT side — it sweeps
//      `Harbor.Ui.Framework.Sessions` + `.State`, where `PanelContext` and
//      `PanelServices` live, and asserts the three hot contracts expose no locator.
//      It never looks at a panel, because a panel is not in either assembly. That
//      gap is this file: the contract was guarded and the implementers were not.
//   2. EVERY BUILTIN PANEL IS CONSTRUCTIBLE WITH NO ARGUMENTS. This is #474's own
//      sentence — "a panel cannot be built in a test without a live composition" —
//      turned into a mechanical fact. A panel that needs a constructor argument
//      cannot be `new`-ed in a unit test, and its behaviour becomes reachable only
//      through the renderer, where a failure does not localise.
//   3. `PanelServices` IS CONSTRUCTIBLE WITHOUT A CONTAINER, and every field is
//      `init`-only. This is the other half of the same sentence, and it is the
//      property that makes rules 1 and 2 worth having: the bag is what a test fills
//      in by hand, so a panel's dependencies are visible to a test as VALUES. Add a
//      required constructor argument to `PanelServices` and every panel silently
//      becomes untestable again, with no other test failing.
//
// WHAT THE ANALYZER LAYER ALREADY BLOCKS, AND THE ONE FORM IT DOES NOT
// -------------------------------------------------------------------
// This file is not a rediscovery of something the build already refuses. The repo
// wires DependencyInjection.Lifetime.Analyzers solution-wide and sets a deliberate
// stance on each rule (.editorconfig:420-512), so the prior art is:
//
//   * DI006 "static IServiceProvider cache" → warning. A container parked in a
//     STATIC field fails the strict build (`--warnaserror`) anywhere under src/.
//   * DI007 "service locator anti-pattern" → SUGGESTION, on purpose: the comment at
//     .editorconfig:500 calls it an anti-pattern rather than a bug, and DI011
//     (IServiceProvider injection) is intentional in HostBuilder.cs. It never blocks
//     anything.
//   * DI003 captive dependency, DI015 unresolvable, DI017 circular → error.
//
// The first attempt at proving this file red planted a STATIC container, and the
// build refused it with `error DI006` before a single test ran. That was the
// compiler doing a service, not this rule — and it is why the plant here is the
// INSTANCE form, the one shape nothing else covers: DI006 is static-only, DI007
// never blocks, and ServiceLocatorBoundaryRules does not sweep a panel because a
// panel is in neither of the two assemblies it sweeps. The hole is exactly the size
// of one field, and this rule is exactly that wide.
//
// WHY NOT EXTEND `ServiceLocatorBoundaryRules` INSTEAD
// ----------------------------------------------------
// The two files draw the same line for different reasons and the reasons are worth
// keeping apart. That file's boundary is "WHO HOLDS THE CONTAINER": a field or a
// constructor parameter persists one for the lifetime of an instance, and that is
// the defect. This file's boundary is "WHO CONSTRUCTS THE PANEL": nobody the host
// controls does, so the frame is the only channel and the line has to be drawn on
// the implementer. Merging them would bury the second argument inside a header
// whose spine is the first, and the next reader would conclude that method
// parameters are broadly fine here — which is true of `FromContainer` and false of a
// panel. #470's guard is left exactly as it is.
//
// PERIMETER, AND WHAT IS DELIBERATELY OUTSIDE IT
// ----------------------------------------------
// Sweeps PRODUCT assemblies only; test and benchmark assemblies are excluded by
// name, as in `ExtensionAxisProbe`. Consequence worth stating: an OUT-OF-TREE plugin
// panel is not swept, and cannot be. Whether a third-party panel holds a container
// is a property of that plugin, not of Harbor; guarding it would mean a rule about
// code this repository does not contain. What IS enforced for a plugin panel is
// everything that does not require seeing it: the contracts it must implement and
// the bag it is handed are fixed, and rule 3 keeps the bag container-free.
//
//   * `PanelServices.FromContainer(IServiceProvider)` is still permitted and is
//     still correct — a static factory a composition root calls once. It is
//     `ServiceLocatorBoundaryRules`' positive control
//     (`FromContainer_StillTakesTheContainerAtCompositionTime`), and this file does
//     not touch it. `PanelServices` is not an `IPanelProvider`, so it is not swept
//     here either.
//   * `IPanelProvider` itself is an interface and is not swept; `CellForgePanelBase`
//     is abstract and is not swept. Both are excluded by construction, not by name.
//
// NON-VACUITY
// -----------
//   1. `Sweep_VisitsTheBuiltinPanels` pins the deterministic half: the concrete
//      implementers in `Harbor.Tui.CellForge`, keyed on a compile-time type
//      reference, with a floor. So "no violations" can never be explained by "no
//      panels discovered" — the cross-assembly sweep additionally rides on the
//      bin-directory inventory, which is exactly the kind of discovery step that
//      can quietly find nothing.
//   2. `PositiveControl_DetectsAStoredLocator_AndIgnoresATypedDependency` drives the
//      REAL predicate over REAL types: a panel that stores a container in a field
//      and one that takes it as a constructor argument must both be reported, and
//      a panel holding a `PanelServices` — the shape every panel is supposed to be
//      — must be reported by neither. A predicate that degraded to "never matches"
//      would turn this whole file into a green rule enforcing nothing, which is
//      worse than no rule because it is believed.
//
// HOW IT WAS PROVEN RED
// ---------------------
// The rules above all pass at the commit that introduced them, because #470 already
// did the work — a guard written against an already-fixed defect is a guard nobody
// has seen fail. So the first commit of this file's PR also planted the #474 shape
// in a real panel (`CellForgeSessionSidebarPanel` held an `IServiceProvider` in a
// field and called `GetService` from `Build`) and let CI fail on it. That run's log
// is in the PR body; the second commit deletes the plant. Nothing about the defect
// was hypothetical — it is the exact code the issue describes, reintroduced on
// purpose to show the rule catches it.

using System.Runtime.CompilerServices;
using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Locates the shapes this file rules: a service locator stored on a panel, or
///     demanded of it at construction. Split out from the rule class so the positive
///     control can drive the REAL predicate over REAL types.
/// </summary>
internal static class PanelLocatorProbe
{
    /// <summary>
    ///     The locator predicate: <see cref="IServiceProvider" /> itself, or anything
    ///     deriving from it (a <c>ServiceProvider</c> kept directly, say). Deliberately
    ///     the same definition <c>ServiceLocatorBoundaryRules</c> uses — these are two
    ///     halves of one boundary, and a rule that quietly disagreed with its sibling
    ///     about what a container is would be worse than no second rule.
    /// </summary>
    internal static bool IsLocator(Type candidate) => typeof(IServiceProvider).IsAssignableFrom(candidate);

    /// <summary>
    ///     Every field (instance or static) and every constructor parameter of
    ///     <paramref name="type" /> that is a service locator, described for a
    ///     human-readable failure.
    /// </summary>
    /// <param name="type">The type to inspect.</param>
    /// <returns>One description per stored or demanded locator; empty when clean.</returns>
    internal static IReadOnlyList<string> FindStoredLocators(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var hits = new List<string>();

        foreach (FieldInfo field in type.GetFields(
                     BindingFlags.Instance | BindingFlags.Static |
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (IsLocator(field.FieldType))
            {
                hits.Add($"{type.FullName}.{field.Name} ({(field.IsStatic ? "static field" : "field")}: {field.FieldType.Name})");
            }
        }

        foreach (ConstructorInfo ctor in type.GetConstructors(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (ParameterInfo parameter in ctor.GetParameters())
            {
                if (IsLocator(parameter.ParameterType))
                {
                    hits.Add($"{type.FullName} :: ctor parameter '{parameter.Name}' ({parameter.ParameterType.Name})");
                }
            }
        }

        return hits;
    }

    /// <summary>
    ///     Every concrete <see cref="IPanelProvider" /> in a PRODUCT assembly.
    /// </summary>
    /// <returns>The panel types the sweep grades.</returns>
    /// <remarks>
    ///     <para>
    ///         Assembly-name filtering excludes test and benchmark assemblies by the
    ///         same markers <c>ExtensionAxisProbe</c> uses: a test panel implements
    ///         <see cref="IPanelProvider" /> to exercise the registry, it does not ship
    ///         one, and grading it would forbid the fixture this file's own positive
    ///         control is built from.
    ///     </para>
    ///     <para>
    ///         The filter is on the TYPE, not the spelling, for the same reason
    ///         <c>ExtensionAxisProbe</c> filters markers by type: a name scan would
    ///         match unrelated helpers and the rule would be wrong about code that has
    ///         nothing to do with panels.
    ///     </para>
    /// </remarks>
    internal static IReadOnlyList<Type> ProductPanels()
    {
        var panels = new List<Type>();

        foreach ((string name, Assembly assembly) in ArchitectureTestHelpers.LoadHarborAssemblies())
        {
            if (IsTestAssemblyName(name))
            {
                continue;
            }

            try
            {
                panels.AddRange(assembly.GetExportedTypes());
            }
            catch (ReflectionTypeLoadException ex)
            {
                panels.AddRange(ex.Types.Where(t => t is not null).Select(t => t!));
            }
            catch (Exception)
            {
                // An assembly that will not enumerate is covered by its own layering
                // tests. Skipping it must not crash this rule — and must not be able
                // to hide a panel either, which is why Sweep_VisitsTheBuiltinPanels
                // pins the in-tree set independently of this walk.
                continue;
            }
        }

        return
        [
            .. panels
                .Where(t => !t.IsAbstract && !IsCompilerGenerated(t) && typeof(IPanelProvider).IsAssignableFrom(t))
        ];
    }

    /// <summary>
    ///     Whether a property can only be set at initialisation — the C# <c>init</c>
    ///     accessor, detected through the <see cref="IsExternalInit" /> modifier the
    ///     compiler puts on its return.
    /// </summary>
    /// <param name="property">The property to inspect.</param>
    /// <returns><see langword="true" /> when the property has an <c>init</c> setter only.</returns>
    internal static bool IsInitOnly(PropertyInfo property)
    {
        MethodInfo? setter = property.SetMethod;
        if (setter is null)
        {
            return false;
        }

        return setter.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
    }

    private static readonly string[] TestAssemblyMarkers =
        [".Tests", ".Test", ".Benchmarks", ".E2E"];

    private static bool IsTestAssemblyName(string name) =>
        TestAssemblyMarkers.Any(m => name.Contains(m, StringComparison.Ordinal));

    /// <summary>
    ///     Async state machines and closure classes are compiler bookkeeping that
    ///     hoists whatever a method captured, so scanning them reports a caller's
    ///     locals back under a name nobody wrote.
    /// </summary>
    private static bool IsCompilerGenerated(Type type) =>
        type.Name.Contains('<')
        || type.Name.Contains('>')
        || type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null;
}

/// <summary>
///     Guard for issue #474: the panel axis is real and frozen, which is why the
///     per-frame bag must stay — and the line that keeps a panel from becoming a
///     service locator of its own is drawn here, on the implementer side. See the
///     file header for the fact-check that closed #470's part of the issue and the
///     three reasons the prescribed "inject via constructor" fix would have broken
///     the axis.
/// </summary>
public sealed class PanelImplementationBoundaryRules
{
    /// <summary>
    ///     The discoverable inventory, computed once. A rule that found nothing
    ///     because discovery found nothing would be indistinguishable from a clean
    ///     sweep, and <see cref="Sweep_VisitsTheBuiltinPanels" /> exists to keep that
    ///     ambiguity out.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<Type>> Inventory = new(PanelLocatorProbe.ProductPanels);

    /// <summary>
    ///     RULE 1 — no panel stores a service locator. This is the implementer-side
    ///     half of the boundary, and the half that was actually unguarded:
    ///     <c>ServiceLocatorBoundaryRules</c> sweeps the two framework assemblies
    ///     where <c>PanelContext</c> and <c>PanelServices</c> are DECLARED, so it
    ///     grades the contract every panel is given and never a panel. A panel that
    ///     reached for a container on its own would have been invisible to it.
    /// </summary>
    [Test]
    public async Task Panels_StoreNoServiceLocator()
    {
        var hits = new List<string>();
        foreach (Type panel in Inventory.Value)
        {
            hits.AddRange(PanelLocatorProbe.FindStoredLocators(panel));
        }

        await Assert.That(string.Join(" | ", hits)).IsEmpty()
            .Because(
                "a panel is constructed by whoever wrote it — a plugin, for the ones on "
                + "the ITuiPanelPlugin axis — so the host never runs its constructor and can never inject "
                + "into it. That makes the per-frame PanelContext the only channel host→panel, and it makes a "
                + "locator kept here doubly wrong: invisible in the signature, and shared by every frame. "
                + "Take the collaborator off the frame instead — ctx.Deps.Store, ctx.Deps.SessionStore, "
                + "ctx.Deps.Diagnostics and the rest are named, typed and nullable, and a test fills them in "
                + "by hand. Stored locators: "
                + (hits.Count == 0 ? "(none)" : string.Join(" | ", hits)));
    }

    /// <summary>
    ///     RULE 2 — every builtin panel is constructible with no arguments. #474's
    ///     cost, restated as a fact a build can check: a panel that cannot be
    ///     <c>new</c>-ed in a unit test has no observable behaviour of its own, and
    ///     every change to it is then verified only through the renderer, where a
    ///     failure does not say which panel broke.
    /// </summary>
    [Test]
    public async Task EveryBuiltinPanel_HasAParameterlessConstructor()
    {
        Type[] panels =
        [
            .. typeof(CellForgePanelBase).Assembly
                .GetExportedTypes()
                .Where(t => !t.IsAbstract && typeof(IPanelProvider).IsAssignableFrom(t))
        ];

        var missing = panels
            .Where(t => t.GetConstructor(Type.EmptyTypes) is null)
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        await Assert.That(string.Join(" | ", missing)).IsEmpty()
            .Because(
                "the panels are ordinary objects and the frame carries their dependencies, so a panel that "
                + "needs a constructor argument is taking a dependency the frame is supposed to carry. Today "
                + "every one of them is `new`-ed bare — that is what lets "
                + "tests/Harbor.Tui.CellForge.Tests/CellForgeBuiltinPanelsTests.cs assert on rendered rows "
                + "instead of only on 'it did not crash'. A panel with a required constructor parameter: "
                + (missing.Count == 0 ? "(none)" : string.Join(" | ", missing)));
    }

    /// <summary>
    ///     RULE 3 — <see cref="PanelServices" /> is buildable without a container and
    ///     its fields are <c>init</c>-only. This is the property that makes rules 1
    ///     and 2 mean anything: the bag is what a test fills in by hand, so a panel's
    ///     dependencies are visible to a test as VALUES. Nobody had pinned it, and it
    ///     is one required constructor argument away from silently making every panel
    ///     untestable again — with no other test in the repository failing.
    /// </summary>
    [Test]
    public async Task PanelServices_IsBuildableWithoutAContainer()
    {
        await Assert.That(typeof(PanelServices).GetConstructor(Type.EmptyTypes)).IsNotNull()
            .Because(
                "PanelServices is filled by hand in tests (new PanelServices { Diagnostics = … }) and by "
                + "FromContainer at the composition root. A required constructor argument would force one of "
                + "those two callers to own a container, and the first one to go is every panel test.");

        var mutable = typeof(PanelServices)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(p => !PanelLocatorProbe.IsInitOnly(p))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        await Assert.That(string.Join(" | ", mutable)).IsEmpty()
            .Because(
                "the bag is rebuilt per session (WithStore), so a settable field would let a panel or a "
                + "renderer rebind a collaborator behind the frame's back — the mutation the TEA state is "
                + "there to prevent, arriving through the dependency channel instead. init-only is what makes "
                + "the value handed to a frame the value that frame renders. Settable: "
                + (mutable.Count == 0 ? "(none)" : string.Join(" | ", mutable)));
    }

    /// <summary>
    ///     Non-vacuity for the cross-assembly sweep. The bin-directory inventory it
    ///     rides on is exactly the kind of discovery step that can quietly find
    ///     nothing, and a rule that reports no violations for that reason is believed
    ///     and wrong. The floor here is on the deterministic half — the panels in
    ///     <c>Harbor.Tui.CellForge</c>, reached through a compile-time type
    ///     reference — so a broken inventory shows up as a failure instead of a pass.
    /// </summary>
    [Test]
    public async Task Sweep_VisitsTheBuiltinPanels()
    {
        Type[] inTree =
        [
            .. typeof(CellForgePanelBase).Assembly
                .GetExportedTypes()
                .Where(t => !t.IsAbstract && typeof(IPanelProvider).IsAssignableFrom(t))
        ];

        await Assert.That(inTree.Length).IsGreaterThanOrEqualTo(10)
            .Because(
                "the eleven builtin panels are the whole subject of #474. The floor is one under the real "
                + "count on purpose: a legitimate rename or a genuine removal should not redden this, but a "
                + "discovery step that has stopped finding panels must. Read: " + inTree.Length + " panels.");

        // The inventory must find those same panels, or rule 1 is grading a
        // different set than the one this file reasons about.
        IReadOnlyList<Type> swept = Inventory.Value;
        var missed = inTree
            .Where(t => !swept.Contains(t))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        await Assert.That(string.Join(" | ", missed)).IsEmpty()
            .Because(
                "rule 1 grades the cross-assembly inventory; this rule pins the deterministic set. If the "
                + "inventory no longer contains a builtin panel, rule 1 is not grading the panels it claims "
                + "to and its green means nothing. Missing from the sweep: "
                + (missed.Count == 0 ? "(none)" : string.Join(" | ", missed)));
    }

    /// <summary>
    ///     THE PROOF OF DISCRIMINATION. The predicate is handed three real types
    ///     built to be told apart:
    ///     <list type="bullet">
    ///     <item>a panel that STORES a container in a field — must be reported;</item>
    ///     <item>a panel DEMANDING one as a constructor parameter — must be reported;</item>
    ///     <item>
    ///         a panel holding a <see cref="PanelServices" />, which is the shape every
    ///         panel is supposed to be — must be reported by neither.
    ///     </item>
    ///     </list>
    ///     The third case is the one that matters most and the one a lazy rule gets
    ///     wrong: a predicate that matched anything service-shaped would flag the
    ///     entire #470 fix, and the obvious response would be to switch the rule off.
    /// </summary>
    [Test]
    public async Task PositiveControl_DetectsAStoredLocator_AndIgnoresATypedDependency()
    {
        IReadOnlyList<string> stored =
            PanelLocatorProbe.FindStoredLocators(typeof(PanelLocatorFixture.ContainerFieldPanel));

        await Assert.That(string.Join(" | ", stored))
            .IsEqualTo(
                $"{typeof(PanelLocatorFixture.ContainerFieldPanel).FullName}._container (field: IServiceProvider)")
            .Because(
                "this is the #474 shape — a panel that keeps a service locator so it can resolve whatever it "
                + "wants, on any frame, from anywhere. A field outlives composition and hides the dependency "
                + "from the signature, which is why the rule matches it. It is the exact shape this guard was "
                + "proven red against, in CellForgeSessionSidebarPanel, and the one shape nothing else in the "
                + "repo blocks: DI006 covers the static case, DI007 is a suggestion. If this stops being "
                + "reported, rule 1 enforces nothing. Read: "
                + (stored.Count == 0 ? "(none — the predicate matches nothing)" : string.Join(" | ", stored)));

        IReadOnlyList<string> demanded =
            PanelLocatorProbe.FindStoredLocators(typeof(PanelLocatorFixture.ContainerCtorPanel));

        await Assert.That(string.Join(" | ", demanded))
            .IsEqualTo(
                $"{typeof(PanelLocatorFixture.ContainerCtorPanel).FullName} :: ctor parameter 'services' (IServiceProvider)")
            .Because(
                "the other half of the same defect, and the one #474's own prescription would have "
                + "recommended: a panel taking the container it should be handed values by. Read: "
                + (demanded.Count == 0 ? "(none — the predicate matches nothing)" : string.Join(" | ", demanded)));

        IReadOnlyList<string> typed =
            PanelLocatorProbe.FindStoredLocators(typeof(PanelLocatorFixture.TypedBagPanel));

        await Assert.That(typed.ToArray()).IsEmpty()
            .Because(
                "a panel that reads its collaborators from the typed bag — exactly what "
                + "CellForgeSubagentsPanel and every other panel does — is the SHAPE THE RULE IS FOR, and "
                + "must never be reported. A predicate broad enough to flag it would be pushed to 'no "
                + "dependencies at all', which is not a better rule; it is a rule that blocks the fix. Read: "
                + (typed.Count == 0 ? "(none)" : string.Join(" | ", typed)));

        // And the init-only detector, which rule 3 rests on, must reject a plain setter.
        await Assert.That(PanelLocatorProbe.IsInitOnly(typeof(PanelServices).GetProperty(nameof(PanelServices.Store))!)).IsTrue()
            .Because(
                "every field of PanelServices is declared `init`. If the detector stops recognising the "
                + "accessor, rule 3's half about settable fields passes for having nothing to look at.");

        await Assert.That(PanelLocatorProbe.IsInitOnly(typeof(SettableBag).GetProperty(nameof(SettableBag.Store))!)).IsFalse()
            .Because(
                "the other direction: a plain setter must NOT read as init-only, or rule 3 is a rule that "
                + "cannot fail. A bag with a settable field is what the detector is there to catch, and it is "
                + "the shape a future refactor would most plausibly introduce.");
    }
}

/// <summary>
///     The typed bag the way a panel is supposed to receive it, and the two shapes it
///     must not. Declared here rather than reused from production because the point is
///     to drive the detector with three REAL types that differ only in the field or
///     parameter they hold.
/// </summary>
internal sealed class SettableBag
{
    /// <summary>A plain settable field — the shape the init-only detector must reject.</summary>
    public UiStore? Store { get; set; }
}

/// <summary>Types used only by the positive control. See the file header.</summary>
internal static class PanelLocatorFixture
{
    /// <summary>
    ///     Stores a container in an INSTANCE field. The #474 defect, in the exact
    ///     shape the guard was proven against, and the one shape nothing else in
    ///     the repo blocks. Must be reported.
    /// </summary>
    internal sealed class ContainerFieldPanel : IPanelProvider
    {
        // Instance, holding a container, read from Build — the shape planted in
        // CellForgeSessionSidebarPanel for the red-by-construction run. A STATIC
        // container would be a weaker demonstration: DI006 already fails the build
        // on that one, which is precisely why the plant is not it.
        private readonly IServiceProvider? _container;

        public ContainerFieldPanel() => _container = null;

        public string Id => "fixture-container-field";

        public string Title => "Fixture";

        public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;

        public int DefaultSize => 10;

        public object? Build(PanelContext ctx) => _container?.GetService(typeof(PanelContext))?.ToString();

        public bool OnKey(UiKey key, PanelContext ctx) => false;
    }

    /// <summary>
    ///     Demands a container as a constructor parameter. The other half, and the
    ///     one #474's own prescription would have recommended. Must be reported.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Deliberately NOT <c>readonly</c> and deliberately not also holding a
    ///         container in a field: the control must attribute each reported line to
    ///         one mechanism. An earlier version of this fixture used a readonly field
    ///         assigned from the ctor parameter, which the first red run duly
    ///         reported TWICE — the field first, then the parameter — and the
    ///         exact-string assertion caught the ambiguity the probe had just proved
    ///         correct. The container is now taken and discarded rather than stored,
    ///         so the ctor parameter is the only locator on the type, and the control's
    ///         expected string is pinned to that parameter's own name.
    ///     </para>
    /// </remarks>
    internal sealed class ContainerCtorPanel : IPanelProvider
    {
        // The container is taken and DISCARDED: this fixture exists so the probe
        // reports the ctor PARAMETER and nothing else. Storing it in a field — even
        // a non-readonly one — makes the probe report both, which is correct
        // behaviour and a useless control.
        public ContainerCtorPanel(IServiceProvider services) => _ = services;

        public string Id => "fixture-container-ctor";

        public string Title => "Fixture";

        public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;

        public int DefaultSize => 10;

        public object? Build(PanelContext ctx) => ctx.Deps.ToString();

        public bool OnKey(UiKey key, PanelContext ctx) => false;
    }

    /// <summary>Holds the typed bag, as a panel should. Must NOT be reported.</summary>
    internal sealed class TypedBagPanel : IPanelProvider
    {
        private readonly PanelServices _services = PanelServices.Empty;

        public string Id => "fixture-typed-bag";

        public string Title => "Fixture";

        public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;

        public int DefaultSize => 10;

        public object? Build(PanelContext ctx) => _services.ToString();

        public bool OnKey(UiKey key, PanelContext ctx) => false;
    }
}
