// ServiceLocatorBoundaryRules.cs — the guard for issue #470.
//
// WHY THIS FILE EXISTS
// --------------------
// Issue #470 was "[DIP] Two service locators in hot paths": `ToolContext.Services`
// — declared `IServiceProvider`, passed as `null!` by BOTH production call sites
// (ToolDispatcher / McpStdioServer), so the signature promised a container that
// never existed — and `PanelContext.Services`, an `IServiceProvider?` re-resolved
// ~18 times per painted frame across the CellForge panels. PR #573 deleted both
// and replaced the second with `PanelServices`: a typed record, filled once by
// the composition root.
//
// What #573 did NOT catch is the third member of the same family, one layer over.
// `SessionFactory` kept `private readonly IServiceProvider _services` whose ONLY
// purpose was `_services.GetService<ICommonConfigReader>()` — a container lookup
// performed on every session creation and every agent-definition resolution, to
// fetch ONE optional service. (That config seam was named ICommonConfigReader then;
// #453 renamed it ICommonConfigModelRefReader and gave it a different carrier, so
// the member is gone by three separate means — but the locator is what this rule
// exists for, and it must not be able to come back under any name.) Its direct
// sibling `SessionManager`, in the same
// folder, had been de-located by #189 (`SessionOptionalFactories`) with exactly
// this argument. `SessionFactory` was missed.
//
// The shape is worth naming, because "no service locators anywhere" does not catch
// it: a locator that resolves exactly ONE service is invisible in review — there
// is nothing to enumerate, and it looks like a DI convenience — while its cost is
// paid per call and the dependency stays absent from the signature. Only an
// explicit rule makes it visible.
//
// WHAT IS RULED
// -------------
//   1. Every concrete type in `Harbor.Ui.Framework.Sessions` STORES no locator —
//      no `IServiceProvider` field (instance or static), none in any constructor.
//   2. Same rule for every concrete type in `Harbor.Ui.Framework.State` — the
//      per-frame state layer where `PanelContext` / `PanelServices` live.
//   3. The three hot contracts (`ToolContext`, `PanelContext`, `PanelServices`)
//      expose no locator on ANY member, public or not.
//
// WHAT IS DELIBERATELY NOT RULED, AND WHY
// ---------------------------------------
//   * `Harbor.Hosting.Modules.*` and every `apps/` composition root. Resolving
//     from a container AT the root is not a service locator, it is the one
//     legitimate use. `PanelServices.FromContainer(IServiceProvider)` is called
//     from exactly such a root, on purpose.
//   * `Harbor.Desktop.Shared.Locators.ViewModelLocator` — a NAMED abstraction
//     with its own interface, registered in the root and injected at the use
//     site. Different thing entirely; it is the target of #470, not a breach of
//     it.
//   * `Harbor.Ipc.HarborIpcServer(IServiceProvider, …)` — an IPC host's own
//     bootstrap entry point. Also used as a live positive control below.
//
// KNOWN LIMIT, STATED RATHER THAN PRETENDED AWAY
// ---------------------------------------------
// The predicate inspects stored state (fields) and construction surface
// (constructors) plus the hot contracts' properties. It deliberately does NOT
// inspect ordinary METHOD parameters, because of one member in the very
// assembly this file sweeps:
//
//     src/Harbor.Ui.Framework.State/Panels/PanelServices.cs
//         public static PanelServices FromContainer(IServiceProvider container)
//
// That takes a container, on purpose, and `PerFrameStateLayer_StoresNoServiceLocator`
// walks straight past it. It is NOT a hole, and the two wrong responses to it are
// both worse than the rule as written:
//
//   * Widening this rule to cover method parameters would redden the one member
//     #573 introduced to FIX #470 — and would redden every composition-root
//     factory besides. The fix would then be "allowlist FromContainer", i.e. an
//     exception for the abstraction the issue was about.
//   * Assuming the rule is therefore leaky, and re-checking the same thing by
//     hand in review, which is what let SessionFactory survive in the first place.
//
// The distinction the rule actually draws is WHO HOLDS THE CONTAINER, not what
// shape the container arrives in:
//
//   * A container kept as a FIELD lives longer than composition, so its
//     dependencies are absent from the signature and it can resolve anything
//     from anywhere, at any time. That is the defect — `ToolContext.Services`,
//     `PanelContext.Services`, and `SessionFactory._services` were all exactly
//     this. The rule matches it.
//   * A container passed to a STATIC FACTORY that a composition root calls once
//     to produce an immutable record of named, typed fields is the legitimate
//     use. `FromContainer` is that. The rule does not match it, and should not.
//
// So: if you are tempted to extend the sweep to method parameters, the thing to
// add is NOT a broader rule — it is a positive control asserting
// `PanelServices.FromContainer` still takes a container, so that anyone who
// quietly deletes it (the direction that WOULD be a real regression) gets a red
// build. That is `FromContainer_StillTakesTheContainerAtCompositionTime` below.
//
// A future per-frame method accepting a live container would still slip past this
// file. That shape deserves its own rule with a per-site baseline, like
// `PresentationCapabilityRules` — not an exception bolted onto this one.
//
// NON-VACUITY — the part that makes the guard worth having
// ------------------------------------------------------
// A reflection rule that finds nothing is indistinguishable from a reflection rule
// that is broken, and a broken guard is worse than no guard because it is
// believed. Two tests close that:
//   * `Detector_ReportsTheLocatorsThatStillExistByDesign` runs the SAME predicate
//     against two real, deliberately-kept locators — `HarborIpcServer`'s
//     constructor parameter and `ViewModelLocator`'s field — and REQUIRES both to
//     be reported. Predicate degradation shows up here, not as a silent pass.
//   * `..._Sweep_CoversRealTypes` requires the assembly sweep to actually visit
//     the session/state types, so "no violations" can never be explained by
//     "no types discovered".
//
// DISCOVERY IS AN ASSEMBLY, NOT A LIST OF OFFENDERS
// -------------------------------------------------
// The sweep enumerates `typeof(SessionFactory).Assembly`, so a brand-new session
// service is covered the day it is written. A hard-coded list of today's three
// offenders is precisely the shape that ages silently — the same argument as
// `TuiReadLineContractRules` and #578.

using System.Runtime.CompilerServices;
using Harbor.Abstractions.Tools;
using Harbor.Desktop.Shared.Locators;
using Harbor.Ipc;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Sessions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Enforces that the UI-framework session and per-frame state layers never
///     STORE a service locator, and that the #470 hot contracts expose none.
///     See the file header for the boundary and its stated limits.
/// </summary>
public class ServiceLocatorBoundaryRules
{
    /// <summary>
    ///     The session layer stores no locator. This is the rule that fails on
    ///     <c>SessionFactory._services</c> — a container kept alive purely to
    ///     resolve one optional <c>ICommonConfigModelRefReader</c> per session creation.
    /// </summary>
    [Test]
    public async Task SessionLayer_StoresNoServiceLocator()
    {
        IReadOnlyList<string> hits = FindStoredLocators(typeof(SessionFactory).Assembly);
        await Assert.That(hits.ToArray()).IsEmpty();
    }

    /// <summary>
    ///     The per-frame state layer stores no locator — the layer
    ///     <c>PanelContext</c> / <c>PanelServices</c> live in, where #470's
    ///     18-per-frame resolutions used to sit.
    /// </summary>
    [Test]
    public async Task PerFrameStateLayer_StoresNoServiceLocator()
    {
        IReadOnlyList<string> hits = FindStoredLocators(typeof(PanelServices).Assembly);
        await Assert.That(hits.ToArray()).IsEmpty();
    }

    /// <summary>
    ///     The three hot contracts expose no locator on any member — constructor
    ///     parameter, field or property, public or private. <c>ToolContext</c> is
    ///     re-checked here even though
    ///     <c>Harbor.Abstractions.Tests.ServiceLocatorContractTests</c> owns it,
    ///     so the UI-side and tool-side halves of #470 fail in the same place.
    /// </summary>
    [Test]
    public async Task HotContracts_ExposeNoServiceLocator()
    {
        Type[] contracts = [typeof(ToolContext), typeof(PanelContext), typeof(PanelServices)];

        var hits = new List<string>();
        foreach (Type contract in contracts)
        {
            hits.AddRange(FindStoredLocators(contract));
            hits.AddRange(FindLocatorProperties(contract));
        }

        await Assert.That(hits.ToArray()).IsEmpty();
    }

    /// <summary>
    ///     The positive control for the one deliberate hole in this file's scope.
    ///     <c>PanelServices.FromContainer(IServiceProvider)</c> is a static factory
    ///     called once by a composition root — the rule's field/constructor sweep
    ///     walks past it, and the header argues at length that this is correct.
    ///     That argument is only worth anything if the member is actually pinned:
    ///     quietly deleting the container parameter and re-resolving per frame
    ///     would restore the exact #470 defect with no rule complaining. So the
    ///     permitted shape is asserted, not merely excused.
    /// </summary>
    [Test]
    public async Task FromContainer_StillTakesTheContainerAtCompositionTime()
    {
        MethodInfo factory = typeof(PanelServices)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(m => m.Name == nameof(PanelServices.FromContainer));

        ParameterInfo[] parameters = factory.GetParameters();

        await Assert.That(parameters.Length).IsEqualTo(1);
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(IServiceProvider));
    }

    /// <summary>
    ///     Non-vacuity: the very same predicate must still report the two
    ///     locators this rule deliberately permits. If it ever degrades to "never
    ///     matches", it fails here instead of passing everything forever.
    /// </summary>
    [Test]
    public async Task Detector_ReportsTheLocatorsThatStillExistByDesign()
    {
        // Composition-root / bootstrap entry point: the container is a constructor
        // argument of the IPC host itself.
        IReadOnlyList<string> ipcHost = FindStoredLocators(typeof(HarborIpcServer));
        await Assert.That(ipcHost.Count).IsGreaterThan(0);

        // A named, injected abstraction — the replacement pattern, not a breach.
        IReadOnlyList<string> vmLocator = FindStoredLocators(typeof(ViewModelLocator));
        await Assert.That(vmLocator.Count).IsGreaterThan(0);
    }

    /// <summary>
    ///     Non-vacuity for the assembly sweeps: both assemblies must contribute a
    ///     real set of concrete types, so an empty violation list can never be
    ///     explained by an empty discovery step.
    /// </summary>
    [Test]
    public async Task Sweeps_CoverRealTypes()
    {
        int sessionTypes = CountScannableTypes(typeof(SessionFactory).Assembly);
        int stateTypes = CountScannableTypes(typeof(PanelServices).Assembly);

        await Assert.That(sessionTypes).IsGreaterThan(5);
        await Assert.That(stateTypes).IsGreaterThan(5);
    }

    // ── Detection ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Every field and every constructor parameter of <paramref name="type" />
    ///     that is a service locator, described for a human-readable failure.
    /// </summary>
    private static IReadOnlyList<string> FindStoredLocators(Type type)
    {
        var hits = new List<string>();

        foreach (FieldInfo field in type.GetFields(
                     BindingFlags.Instance | BindingFlags.Static |
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (IsLocator(field.FieldType))
            {
                hits.Add($"{type.FullName}.{field.Name} (field: {field.FieldType.Name})");
            }
        }

        foreach (ConstructorInfo ctor in type.GetConstructors(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (ParameterInfo parameter in ctor.GetParameters())
            {
                if (IsLocator(parameter.ParameterType))
                {
                    hits.Add($"{type.FullName}.{parameter.Name} (ctor parameter: {parameter.ParameterType.Name})");
                }
            }
        }

        return hits;
    }

    /// <summary>Every property of <paramref name="type" /> typed as a service locator.</summary>
    private static IReadOnlyList<string> FindLocatorProperties(Type type) =>
    [
        .. type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => IsLocator(p.PropertyType))
            .Select(p => $"{type.FullName}.{p.Name} (property: {p.PropertyType.Name})")
    ];

    /// <summary>
    ///     The locator predicate: <see cref="IServiceProvider" /> itself, or any
    ///     type derived from it (a <c>ServiceProvider</c> held directly, say).
    /// </summary>
    private static bool IsLocator(Type candidate) => typeof(IServiceProvider).IsAssignableFrom(candidate);

    /// <summary>Every concrete, non-compiler-generated type in <paramref name="assembly" />.</summary>
    private static IReadOnlyList<Type> ScannableTypes(Assembly assembly) =>
    [
        .. assembly.GetTypes()
            .Where(t => !t.IsAbstract && !IsCompilerGenerated(t))
    ];

    private static int CountScannableTypes(Assembly assembly) => ScannableTypes(assembly).Count;

    private static IReadOnlyList<string> FindStoredLocators(Assembly assembly)
    {
        var hits = new List<string>();
        foreach (Type type in ScannableTypes(assembly))
        {
            hits.AddRange(FindStoredLocators(type));
        }
        return hits;
    }

    /// <summary>
    ///     Async state machines and closure classes are compiler bookkeeping; they
    ///     hoist whatever a method captured, so scanning them reports the caller's
    ///     locals back under a name nobody wrote.
    /// </summary>
    private static bool IsCompilerGenerated(Type type) =>
        type.Name.Contains('<')
        || type.Name.Contains('>')
        || type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null;
}
