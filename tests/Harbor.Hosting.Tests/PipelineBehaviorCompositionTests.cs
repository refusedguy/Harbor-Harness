// PipelineBehaviorCompositionTests.cs — GUARD for #480 (A10).
//
// WHAT #480 A10 CLAIMED
// ---------------------
// "src/Harbor.Application/Agents/AgentLoop.cs assembles its pipeline by hand:
//  :109-113 new AgentPipeline([new LoggingBehavior, new PermissionCheckBehavior])
//  — the behaviour list is HARDCODED, so a third behaviour is unreachable
//  without editing AgentLoop."
//
// The shape is real and it is worth a gate. `IPipelineBehavior` is a public,
// documented extension seam in src/Harbor.Application/Agents/Pipeline/, and
// `AgentPipeline` already takes `IEnumerable<IPipelineBehavior>` — the door is
// built and the frame is on the wall. What is missing is the DOOR: nothing in
// the composition root ever registers a behaviour, so the set of behaviours
// that can wrap an agent run is a literal inside one constructor body. Adding
// the third concern means editing the class that runs the agent — the same
// shape as #620's half-open axis, one layer down.
//
// WHY THIS MEASURES THE REAL COMPOSITION ROOT
// --------------------------------------------
// `EventBusSinkCompositionTests.cs` in this same project already made the call:
// a composition claim measured against a hand-built container is a claim about
// the test, not about the product. So the registrations below are read off the
// `IServiceCollection` that the REAL `AddHarbor` returns — the same call the
// CLI, the desktop apps and the embedders make.
//
// The behaviours are RESOLVED from the container that the REAL `AddHarbor`
// produces — the same call the CLI, the desktop apps and the embedders make.
// Resolving rather than reading descriptors is deliberate and is explained at
// the method: a factory-registered descriptor carries no ImplementationType, so
// a descriptor-only reader sees an empty registration set and calls every
// behaviour unreachable. That is this file's own failure mode, reached from the
// other side, and `NonVacuity_TheRuleReportsABehaviorNobodyRegistered` is the
// test that pins it shut: it has to show the probe reads a NON-EMPTY set before
// it is allowed to assert that the set difference can report a miss.
//
// WHAT MAKES THIS RED BY CONSTRUCTION
// ------------------------------------
// At the commit that adds this file, CoreModule registers zero behaviours and
// two exist in Harbor.Application, so `EveryPipelineBehavior_IsRegistered`
// reports both as unreachable. It cannot be green by accident: the product half
// is a reflection sweep over a named assembly, and NonVacuity below pins two
// concrete type names into that sweep's answer.
//
// NotInParallel: composition reads the process-wide environment variables
// (HARBOR_STORAGE / HARBOR_TUI / HARBOR_MODE) shared with the other hosting
// suites.

using System.Reflection;
using CSharpFunctionalExtensions;
using Harbor.Application.Agents;
using Harbor.Application.Agents.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Hosting.Tests;

/// <summary>
///     Asserts that the agent run's cross-cutting behaviours are reachable by
///     REGISTRATION, not by editing <see cref="AgentLoop" />'s constructor body
///     (#480 A10).
/// </summary>
[NotInParallel("hosting")]
public class PipelineBehaviorCompositionTests
{
    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-pipeline-behavior-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    ///     Runs the real <c>AddHarbor</c> and returns the types of the behaviours
    ///     the composed container actually hands out — the set a composition root
    ///     offers.
    /// </summary>
    /// <remarks>
    ///     These are RESOLVED, not read off the descriptors, and that is load
    ///     bearing. <c>CoreModule</c> registers each behaviour through a factory
    ///     lambda (it has to, to hand the behaviour its own typed logger), and a
    ///     factory-registered <see cref="ServiceDescriptor" /> has an
    ///     <c>ImplementationType</c> of <c>null</c> — a descriptor-only reader
    ///     finds an EMPTY registration set and reports every behaviour
    ///     unregistered. That is the exact failure this file exists to prevent,
    ///     reached from the other direction: a probe that reads nothing.
    ///
    ///     Resolving also proves the registrations BUILD, not merely that a
    ///     descriptor exists — a behaviour whose logger cannot be supplied fails
    ///     here rather than at the first agent run.
    ///
    ///     <c>ILogger&lt;&gt;</c> is supplied as <see cref="NullLogger{T}" />
    ///     because <c>AddHarbor</c> does not add logging itself: the apps compose
    ///     on a host that already has it, and a bare <c>ServiceCollection</c> does
    ///     not. Null keeps the probe hermetic and comes from the Abstractions
    ///     package this project already references directly.
    /// </remarks>
    private static IReadOnlyList<Type> RegisteredByCompositionRoot()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddHarbor(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory"
        });

        using ServiceProvider sp = services.BuildServiceProvider();

        return [.. sp.GetServices<IPipelineBehavior>().Select(behavior => behavior.GetType())];
    }

    /// <summary>
    ///     Every concrete <see cref="IPipelineBehavior" /> declared in
    ///     <c>Harbor.Application</c> — keyed by TYPE, never by name (#626/#735:
    ///     a name scan also matches typos and renamed types, which is how a
    ///     guard goes green while enforcing nothing).
    /// </summary>
    private static IReadOnlyList<Type> DeclaredBehaviors()
    {
        Assembly assembly = typeof(IPipelineBehavior).Assembly;

        return
        [
            .. assembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
                .Where(t => typeof(IPipelineBehavior).IsAssignableFrom(t))
                // Nested private types of other rules in this assembly are not
                // product behaviours; only the ones the product declares count.
                .Where(t => t.IsPublic || t.IsNestedPublic)
        ];
    }

    /// <summary>
    ///     THE RULE. A behaviour that no composition root registers cannot wrap
    ///     a run in the shipped product — the run gets the two behaviours the
    ///     <see cref="AgentLoop" /> constructor happens to spell out, and the
    ///     third concern stays unreachable without editing the loop.
    /// </summary>
    [Test]
    public async Task EveryPipelineBehavior_IsRegistered()
    {
        IReadOnlyList<Type> registered = RegisteredByCompositionRoot();
        List<Type> declared = [.. DeclaredBehaviors()];

        var unreachable = declared
            .Where(t => !registered.Contains(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        string failures = string.Join(
            "\n",
            unreachable.Select(t =>
                $"{t.FullName} implements IPipelineBehavior but no AddHarbor module registers it. "
                + "An agent run wraps the two behaviours AgentLoop's constructor spells out and nothing else, "
                + "so this concern can only be added by editing the class that runs the agent (#480 A10). "
                + "Register it: services.AddSingleton<IPipelineBehavior>(sp => new X(sp.GetRequiredService<ILogger<X>>()))"));

        await Assert.That(unreachable).IsEmpty()
            .Because(
                "IPipelineBehavior is a public seam in src/Harbor.Application/Agents/Pipeline/ and AgentPipeline "
                + "already takes IEnumerable<IPipelineBehavior>. An implementation nobody registers is an "
                + "extension point that exists and cannot be delivered — #620's half-open axis, in the agent "
                + "pipeline. Unregistered: " + failures);
    }

    /// <summary>
    ///     The other half of #480 A10: a registration is only an offer the loop
    ///     can accept. If <see cref="AgentLoop" /> has no parameter that takes
    ///     the behaviours, a registered behaviour is inert — the container has
    ///     it, the run does not.
    /// </summary>
    [Test]
    public async Task AgentLoop_AcceptsBehavioursAsAParameter()
    {
        ConstructorInfo[] constructors = typeof(AgentLoop).GetConstructors();
        bool accepts = constructors.Any(ctor => ctor
            .GetParameters()
            .Any(p => p.ParameterType == typeof(IEnumerable<IPipelineBehavior>)));

        await Assert.That(accepts).IsTrue()
            .Because(
                "#480 A10: AgentLoop built its AgentPipeline from a literal list inside the constructor "
                + "(new AgentPipeline([new LoggingBehavior(logger), new PermissionCheckBehavior(logger)])). "
                + "Until it takes IEnumerable<IPipelineBehavior>, a registered behaviour has nowhere to enter "
                + "and the hardcoded list stays the whole truth about what wraps a run.");
    }

    // =====================================================================
    // Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The sweep must be reading real types out of the real assembly, and the
    ///     behaviours #480 names by line number must still be in it. A sweep that
    ///     found nothing would report "nothing is unregistered" — green forever,
    ///     enforcing nothing — and a sweep that found a different pair would mean
    ///     the types moved or were deleted, which is a failure to report rather
    ///     than a smaller, easier table.
    /// </summary>
    [Test]
    public async Task NonVacuity_TheSweepFindsTheBehavioursNamedBy480()
    {
        List<Type> declared = [.. DeclaredBehaviors()];
        string names = string.Join(", ", declared.Select(t => t.Name));

        await Assert.That(declared.Count).IsGreaterThan(0)
            .Because(
                "the reflection sweep over " + typeof(IPipelineBehavior).Assembly.GetName().Name
                + " found no IPipelineBehavior implementation. The rule above would then report an empty "
                + "unreachable set on every run — green by finding nothing, which is the shape of a guard that "
                + "enforces nothing");

        foreach (string expected in new[] { "LoggingBehavior", "PermissionCheckBehavior" })
        {
            await Assert.That(names).Contains(expected)
                .Because(
                    expected + " is one of the two behaviours #480 A10 names as being newed up inside "
                    + "AgentLoop's constructor (AgentLoop.cs:109-113). If the type is gone or was renamed, the "
                    + "rule above is no longer talking about the code this guard was written for. Found: "
                    + names);
        }
    }

    /// <summary>
    ///     THE POSITIVE CONTROL, and the non-vacuity for the REGISTERED side.
    ///     Given a container that really does hand out behaviours, the very
    ///     comparison the rule uses must still report a type that is absent from
    ///     it.
    /// </summary>
    /// <remarks>
    ///     The first half is what makes the second half mean anything. This file
    ///     shipped one red run whose probe read an EMPTY registration set —
    ///     factory descriptors have a null <c>ImplementationType</c> — and both
    ///     rule tests failed for a reason that had nothing to do with the product
    ///     while the registered set was silently zero. A positive control that
    ///     passes over an empty set proves nothing: <c>!Contains</c> is trivially
    ///     true of nothing. So the control first has to show the probe reads a
    ///     real set, and only then that the set difference can report a miss.
    /// </remarks>
    [Test]
    public async Task NonVacuity_TheRuleReportsABehaviorNobodyRegistered()
    {
        IReadOnlyList<Type> registered = RegisteredByCompositionRoot();
        string handedOut = string.Join(", ", registered.Select(t => t.Name));

        await Assert.That(registered.Count).IsGreaterThan(0)
            .Because(
                "the composition root handed out NO IPipelineBehavior at all. EveryPipelineBehavior_IsRegistered "
                + "then reports every declared behaviour unreachable no matter what the product does — the exact "
                + "vacuous shape of this file, reached by a reader that resolved nothing");

        foreach (string expected in new[] { "LoggingBehavior", "PermissionCheckBehavior" })
        {
            await Assert.That(handedOut).Contains(expected)
                .Because(
                    expected + " is registered by CoreModule and must come back out of the composed container. If "
                    + "it does not, the probe is reading a set that does not describe the product. Handed out: "
                    + handedOut);
        }

        bool reported = !registered.Contains(typeof(UnregisteredBehavior));

        await Assert.That(reported).IsTrue()
            .Because(
                "UnregisteredBehavior is declared in this very file and no AddHarbor module can register it, so "
                + "EveryPipelineBehavior_IsRegistered's `declared minus registered` comparison must report such a "
                + "type. A comparison that cannot report it cannot report LoggingBehavior either, and the rule "
                + "above would pass on any content at all.");
    }

    /// <summary>
    ///     A behaviour that exists only so the positive control above has
    ///     something real to compare against. Never registered, never run.
    /// </summary>
    private sealed class UnregisteredBehavior : IPipelineBehavior
    {
        public Task<Result> HandleAsync(PromptRequest request, PipelineNext next, CancellationToken ct) =>
            next(request, ct);
    }
}