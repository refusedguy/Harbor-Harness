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
//     from exactly such a root, on purpose. `App.axaml.cs` is that root for the
//     desktop app, and `Program.cs` is the file that hands it over — so those two
//     files are the declared handover channel, named in
//     `CompositionRootHandoverFiles` and re-asserted by
//     `DesktopProduct_DoesNotReachTheAmbientContainerOutsideTheCompositionRoot`.
//     What is ruled is everything ELSE in `apps/` naming the ambient: see #779
//     below for why `apps/` needed its own scan rather than one more reflection
//     rule.
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
//
// THE GENERAL CASE (#760) — an instance field is NOT the same defect as a static
// ---------------------------------------------------------------------------
// #470 closed one instance of "a stored container". #474 closed another and said
// out loud that the general case was undecided; #760 is that general case, and
// asks whether an `IServiceProvider` in an INSTANCE field or constructor parameter
// is caught anywhere repo-wide. It is not — and that is a decision, not a hole:
//
//   * Constructor parameter → DI011, set to `suggestion` with the reason written
//     out in .editorconfig ("IServiceProvider injection is intentional in
//     HostBuilder.cs"). Deliberate. Cited by rule and section rather than by line
//     number: #838 added and removed lines in that file, and a line-number
//     citation in a comment is a drift waiting to happen.
//   * Static field or property → DI006, `warning`, so `TreatWarningsAsErrors`
//     makes it a build error. Covered, and hard.
//   * Instance field → no DI rule, BY THE ANALYZER'S OWN CONTRACT. DI006's own
//     README offers `private readonly IServiceProvider _provider;` in a sealed
//     class as the "Better pattern" to replace a static provider cache. The
//     package authors classify the instance form as the fix, not the defect.
//
// So the sharp half of the question is the second one: is an instance field with
// a container the same thing as a static one? NO — and the difference is the
// whole answer, because the danger is not "in a field", it is the OWNER OF THE
// FIELD. A static container is global state and is caught for being global. An
// instance container is a *lifetime* question: a singleton that retains a
// container and hands out a SCOPED service from it is a captive dependency, and
// the scoped instance lives as long as the singleton — forever. A per-request
// object holding a container it was handed captures nothing extra, and is fine.
// "No IServiceProvider in a field" cannot tell those apart, so it is the wrong
// rule; the right one is "no scope outlives its owner", and that is ALREADY
// ENFORCED, at the strictest level, by the rules that can actually see lifetimes:
// DI003 (captive dependency) and DI019 (scoped resolved from root) are `error`,
// with DI002 / DI004 / DI001 at `warning` behind them. No new axis is needed for
// it, and none is added — feature freeze #555.
//
// What this file adds is the one shape the analyzer layer genuinely does not own
// and that a field sweep CAN own: a stored `IServiceScope`. Unlike a container, a
// concrete scope is a leak in EVERY owner — it cannot be disposed at the right
// time by anyone, and it pins every scoped service resolved through it. That is
// `SrcAssemblies_StoreNoServiceScope` below, swept over the whole `src/` tree
// rather than two assemblies, with `IServiceScopeFactory` deliberately excluded
// because a scope FACTORY on a singleton is the correct idiom and both DI011 and
// DI019 name it as a sanctioned exception.
//
// The container inventory is also widened to the whole `src/` tree
// (`StoredLocatorInventory_IsExactlyTheDeclaredBaseline`), so the honest
// "undetected" surface is now a number in an assertion instead of an assumption.
// Measured on dev, src/ holds exactly two container-storing types: `ViewModelLocator`
// (the named locator abstraction, deliberate since #63 — it IS the pattern) and
// `HarborIpcServer` (an IPC host's own bootstrap ctor, which resolves what it
// needs and retains nothing). apps/ is composition root and unreferenced by this
// project; reading it found three instance holders — `ReplRunner._rendererHost`
// (forwarded untouched, never resolved from), `AvaloniaChatViewBinder` (a
// singleton, but its only resolution is `ChatViewModel`, registered
// `AddSingleton`, so nothing is captured) and `DemoCellForgeScreen` (a demo
// screen). Zero of them resolve a scoped service, which is why the captive case is
// an empty set here rather than a suppressed one.

// apps/ IS composition root AND unreferenced by this project. #779 — the third
// member of the #470 family, one layer out again
// -----------------------------------------------------------------------
// `Harbor.App.Avalonia.App.Services` was `public static IServiceProvider
// Services { get; set; } = null!` — written once by `Program.cs`, read twelve
// times from XAML view code-behinds, and primed by two test sites before they
// could run at all. The views are NOT the root: their dependency is absent from
// their signature and reachable from anywhere through a static, which is
// `ToolContext.Services` / `PanelContext.Services` / `SessionFactory._services`
// again, one layer out.
//
// Two things made it invisible here, and BOTH had to be closed — fixing one alone
// yields a rule that cannot fail:
//
//   * `apps/` is not referenced by this test project at all. #760 read it by hand
//     and found three instance holders; a hand read expires silently.
//   * it is a static PROPERTY. `FindStoredLocators` reads fields and constructor
//     parameters; `FindLocatorProperties` exists but is applied only to the three
//     #470 hot contracts.
//
// Hence the source scan over the product trees below. The replacement was NOT a
// new abstraction — `Harbor.Desktop.Shared.Locators.IViewModelLocator` already
// existed, already registered by the desktop composition root, already tested,
// and had ZERO product consumers; its own doc comment says it "replaces the
// scattered `App.Services.GetService<T>()`". The defect was an unused abstraction
// living beside a used duplicate, which is #874's shape, not #470's.

using System.Runtime.CompilerServices;
using Harbor.Abstractions.Tools;
using Harbor.Desktop.Shared.Locators;
using Harbor.Ipc;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Sessions;
using Microsoft.Extensions.DependencyInjection;
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

    // ── #760: the general case, repo-wide ──────────────────────────────────

    /// <summary>
    ///     The one container shape a field sweep can own and the analyzer layer
    ///     does not: a stored <see cref="IServiceScope" />. A concrete scope is a
    ///     leak in EVERY owner — whoever holds it also owns its disposal, so the
    ///     scoped services behind it are pinned until the holder is collected, and
    ///     nothing can dispose them at the end of the request that made them.
    ///     <c>IServiceScopeFactory</c> is deliberately NOT matched: a factory on a
    ///     singleton is the correct way to open a scope per unit of work, and both
    ///     DI011 and DI019 list it as a sanctioned exception.
    /// </summary>
    /// <remarks>
    ///     Swept over the whole <c>src/</c> tree, not the two assemblies the locator
    ///     rule covers. This is the sharpened form of #760: the issue asked for "no
    ///     container in a field", which cannot distinguish a harmless per-request
    ///     holder from a singleton that outlives what it resolves. A stored scope is
    ///     unambiguous, so it is the form worth ruling.
    /// </remarks>
    [Test]
    public async Task SrcAssemblies_StoreNoServiceScope()
    {
        var hits = new List<string>();
        foreach (Assembly assembly in SrcAssemblies())
        {
            hits.AddRange(FindStoredScopes(assembly));
        }

        await Assert.That(hits.ToArray()).IsEmpty();
    }

    /// <summary>
    ///     Non-vacuity for the rule above. A sweep that must find nothing cannot
    ///     tell "clean" from "broken", so the SAME predicate is run against a
    ///     planted holder — a scope in a field AND a scope in a constructor — and
    ///     both must be reported. If the predicate ever narrows to something the
    ///     planted type does not match, it fails here rather than passing the
    ///     sweep forever.
    /// </summary>
    [Test]
    public async Task ScopeDetector_ReportsThePlantedHolder()
    {
        IReadOnlyList<string> hits = FindStoredScopes(typeof(PlantedScopeHolder));

        await Assert.That(hits.Count).IsEqualTo(2);
    }

    /// <summary>
    ///     The <c>src/</c>-wide container inventory, pinned. #760 is right that an
    ///     instance container field is invisible outside the two assemblies the
    ///     locator rule sweeps; this turns that undetected surface into an asserted
    ///     number. Dev holds exactly two container-storing product types, both
    ///     deliberate — see the file header for why each one is the pattern rather
    ///     than a breach. A third type appearing here is the signal to argue for it
    ///     in a review, not a blank entry in a list.
    /// </summary>
    [Test]
    public async Task StoredLocatorInventory_IsExactlyTheDeclaredBaseline()
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Assembly assembly in SrcAssemblies())
        {
            foreach (string hit in FindStoredLocators(assembly))
            {
                // "Namespace.Type.Member (field: …)" → "Namespace.Type".
                int member = hit.LastIndexOf('.');
                offenders.Add(member > 0 ? hit[..member] : hit);
            }
        }

        // Two declared types, in ordinal order so the message is stable.
        string actual = string.Join(" | ", offenders);
        string declared = "Harbor.Desktop.Shared.Locators.ViewModelLocator | Harbor.Ipc.HarborIpcServer";

        await Assert.That(actual).IsEqualTo(declared);
    }

    // ── #779: the desktop ambient container, `apps/Harbor.App.Avalonia.App.Services` ──

    /// <summary>
    ///     The two files that make up the desktop composition root's HANDOVER
    ///     channel for <c>App.Services</c>, and the only two allowed to name it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #760 closed the <c>src/</c> container inventory and said out loud what it
    ///         could not see: "apps/ is composition root and unreferenced by this project".
    ///         It then read apps/ BY HAND and listed three instance holders. A hand read
    ///         is not a rule — it is a snapshot that expires silently, which is how
    ///         <c>SessionFactory</c> survived #470 in the first place (see this file's
    ///         header).
    ///     </para>
    ///     <para>
    ///         <c>App.Services</c> is invisible to the reflection sweep for TWO independent
    ///         reasons, which is why the hand read missed it too:
    ///     </para>
    ///     <list type="number">
    ///         <item>
    ///             It lives in <c>apps/</c>, and <see cref="SrcAssemblies" /> only yields
    ///             <c>src/</c> projects. The desktop app is not a reference of this test
    ///             project at all, so it cannot be reached by reflection without a new
    ///             (and layering-invisible) ProjectReference.
    ///         </item>
    ///         <item>
    ///             It is a static PROPERTY, and <see cref="FindStoredLocators(Type)" />
    ///             reads fields and constructor parameters only.
    ///             <see cref="FindLocatorProperties(Type)" /> would match it, but it is
    ///             applied to the three #470 hot contracts alone, never to an assembly sweep.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         Closing (1) without (2) would have produced a rule that cannot fail, so
    ///         this is a SOURCE scan over the product trees instead — the same
    ///         <see cref="SourceScan" /> helpers the other apps/-walking rules use, and
    ///         the same "an empty violation list must be explainable" discipline.
    ///     </para>
    /// </remarks>
    private static readonly string[] CompositionRootHandoverFiles =
    [
        "apps/Harbor.App.Avalonia/App.axaml.cs",
        "apps/Harbor.App.Avalonia/Program.cs",
    ];

    /// <summary>
    ///     Nothing outside the desktop composition root's handover channel may read
    ///     the ambient container. On dev, twelve reads sat in XAML view code-behinds —
    ///     the #470 family one layer out: the dependency is absent from the view's
    ///     signature and reachable from anywhere, at any time, through a static.
    /// </summary>
    [Test]
    public async Task DesktopProduct_DoesNotReachTheAmbientContainerOutsideTheCompositionRoot()
    {
        await Assert.That(FindAmbientContainerReadsOutsideCompositionRoot().ToArray()).IsEmpty();
    }

    /// <summary>
    ///     The handover itself must not be null-forgiving. <c>= null!</c> makes
    ///     "the host was never handed over" representable, and every read of it then
    ///     fails as a bare <see cref="NullReferenceException" /> at whatever depth the
    ///     read happened to sit — which is the "works in the REPL, dies in a test"
    ///     shape this whole file exists to remove. The getter names the missing step
    ///     instead.
    /// </summary>
    [Test]
    public async Task DesktopAmbientContainer_IsNotNullForgiving()
    {
        string? app = ReadProductFile("apps/Harbor.App.Avalonia/App.axaml.cs");

        // Non-vacuity: the file the rule is about must actually have been read, and
        // must actually be about the ambient at all — otherwise the `= null!` check
        // below would pass on a file that never mentioned one.
        await Assert.That(app).IsNotNull();
        if (app is null)
        {
            return;
        }

        await Assert.That(app.Contains("Services", StringComparison.Ordinal)).IsTrue();
        await Assert.That(app.Contains("= null!", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    ///     Non-vacuity for the source scan above. The SAME predicate is run against
    ///     planted sources: one file that names the ambient and one that does not.
    ///     If the matcher ever narrows to something neither reaches — or if the
    ///     composition-root exemption ever swallows everything — it fails here rather
    ///     than passing the real sweep forever.
    /// </summary>
    [Test]
    public async Task AmbientContainerDetector_ReportsAReadOutsideTheCompositionRoot()
    {
        IReadOnlyList<string> planted = FindAmbientContainerReads(
            [
                ("apps/Harbor.App.Avalonia/Views/SomethingView.axaml.cs",
                    "private IShellChrome C => App.Services.GetRequiredService<IShellChrome>();"),
                ("apps/Harbor.App.Avalonia/App.axaml.cs",
                    "var vm = Services.GetRequiredService<MainViewModel>();"),
                ("apps/Harbor.App.Avalonia/Program.cs",
                    "App.Services = host.Services;"),
                ("apps/Harbor.App.Cli/Program.cs",
                    "var x = 1; // nothing to see"),
            ]);

        // One file: the view. The root's own file and the writing Program.cs pass.
        await Assert.That(planted.Count).IsEqualTo(1);
        await Assert.That(planted[0]).IsEqualTo("apps/Harbor.App.Avalonia/Views/SomethingView.axaml.cs");
    }

    /// <summary>
    ///     Product files that name <c>App.Services</c> while NOT being part of the
    ///     composition root's handover channel, repo-relative and ordinal-sorted.
    /// </summary>
    private static IReadOnlyList<string> FindAmbientContainerReadsOutsideCompositionRoot()
    {
        var sources = new List<(string Relative, string Text)>();
        foreach (string file in SourceScan.EnumerateProductCsFiles())
        {
            string? text = SourceScan.TryReadAllText(file);
            if (text is not null)
            {
                sources.Add((SourceScan.Relative(file), text));
            }
        }

        return FindAmbientContainerReads(sources);
    }

    /// <summary>
    ///     Grading for the rule above, split out so the planted control drives the
    ///     REAL matcher — comment stripping and the composition-root exemption
    ///     included — rather than a second implementation of it, which is the only way
    ///     "it can fail" means anything.
    /// </summary>
    private static IReadOnlyList<string> FindAmbientContainerReads(
        IReadOnlyList<(string Relative, string Text)> sources)
    {
        var hits = new List<string>();
        foreach ((string relative, string text) in sources)
        {
            if (CompositionRootHandoverFiles.Contains(relative, StringComparer.Ordinal))
            {
                continue;
            }

            // Comments are stripped so prose ABOUT the ambient ("App.Services is
            // the composition root's handover channel") cannot redden the sweep.
            string stripped = SourceScan.StripComments(text);
            if (stripped.Contains("App.Services", StringComparison.Ordinal))
            {
                hits.Add(relative);
            }
        }

        return [.. hits.OrderBy(h => h, StringComparer.Ordinal)];
    }

    /// <summary>Reads one repo-relative product file, or <see langword="null" />.</summary>
    private static string? ReadProductFile(string relative)
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return null;
        }

        return SourceScan.TryReadAllText(
            Path.Combine(root, relative.Replace('/', Path.GetDirectorySeparatorChar)));
    }

    // ── #760 planted control types — never constructed, only reflected over ──

    /// <summary>
    ///     A deliberately-kept offender for <see cref="ScopeDetector_ReportsThePlantedHolder" />.
    ///     It stores a scope twice — once as a field, once as a constructor
    ///     parameter — so the control proves the predicate reaches BOTH surfaces,
    ///     not just the one that happened to match.
    /// </summary>
    private sealed class PlantedScopeHolder
    {
        private readonly IServiceScope _scope;

        public PlantedScopeHolder(IServiceScope scope) => _scope = scope;
    }

    // ── Detection ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Every assembly in the main-solution <c>src/</c> tree that this test
    ///     project can reach. Sourced from the loaded inventory intersected with
    ///     <see cref="FullLayerMatrixTests.AllSrcAssemblies" /> — the one list that
    ///     <c>EnforcerIntegrityTests.SrcProjects_AreAllClassified</c> keeps honest —
    ///     so a new src project is swept the day it is written and an app/composition
    ///     root is not swept by accident.
    /// </summary>
    private static IEnumerable<Assembly> SrcAssemblies()
    {
        IReadOnlyDictionary<string, Assembly> loaded = ArchitectureTestHelpers.LoadHarborAssemblies();
        var inventory = FullLayerMatrixTests.AllSrcAssemblies.ToHashSet(StringComparer.Ordinal);

        foreach ((string name, Assembly assembly) in loaded)
        {
            if (inventory.Contains(name))
            {
                yield return assembly;
            }
        }
    }

    /// <summary>Every concrete type in <paramref name="assembly" /> that STORES a scope.</summary>
    private static IReadOnlyList<string> FindStoredScopes(Assembly assembly)
    {
        var hits = new List<string>();
        foreach (Type type in ScannableTypes(SafeGetTypes(assembly)))
        {
            hits.AddRange(FindStoredScopes(type));
        }

        return hits;
    }

    /// <summary>
    ///     Types of an assembly, tolerating the ones that will not enumerate. The
    ///     <c>src/</c> sweep reaches ~60 assemblies, one of which failing to load
    ///     must not crash the rule — the same tolerance
    ///     <c>ExtensionAxisFreezeRule.ProductTypes</c> and <c>SeamLeakProbe</c>
    ///     apply. Discovery is not a silent pass: <c>Sweeps_CoverRealTypes</c>
    ///     requires the type count to be real.
    /// </summary>
    private static IReadOnlyList<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return [.. ex.Types.Where(static t => t is not null).Select(static t => t!)];
        }
        catch (Exception ex)
        {
            _ = ex;
            return [];
        }
    }

    /// <summary>
    ///     Every field and constructor parameter of <paramref name="type" /> that is
    ///     a service scope — the captive-dependency primitive, in either position.
    /// </summary>
    private static IReadOnlyList<string> FindStoredScopes(Type type)
    {
        var hits = new List<string>();

        foreach (FieldInfo field in type.GetFields(
                     BindingFlags.Instance | BindingFlags.Static |
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (IsStoredScope(field.FieldType))
            {
                hits.Add($"{type.FullName}.{field.Name} (field: {field.FieldType.Name})");
            }
        }

        foreach (ConstructorInfo ctor in type.GetConstructors(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (ParameterInfo parameter in ctor.GetParameters())
            {
                if (IsStoredScope(parameter.ParameterType))
                {
                    hits.Add($"{type.FullName}.{parameter.Name} (ctor parameter: {parameter.ParameterType.Name})");
                }
            }
        }

        return hits;
    }

    /// <summary>
    ///     The scope predicate. Matches <see cref="IServiceScope" /> and anything
    ///     derived from it; deliberately excludes <see cref="IServiceScopeFactory" />,
    ///     which is the sanctioned per-unit-of-work idiom rather than a retained scope.
    /// </summary>
    private static bool IsStoredScope(Type candidate) =>
        typeof(IServiceScope).IsAssignableFrom(candidate);

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
    private static IReadOnlyList<Type> ScannableTypes(Assembly assembly) => ScannableTypes(assembly.GetTypes());

    /// <summary>Every concrete, non-compiler-generated type among <paramref name="types" />.</summary>
    private static IReadOnlyList<Type> ScannableTypes(IReadOnlyList<Type> types) =>
    [
        .. types.Where(t => !t.IsAbstract && !IsCompilerGenerated(t))
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
