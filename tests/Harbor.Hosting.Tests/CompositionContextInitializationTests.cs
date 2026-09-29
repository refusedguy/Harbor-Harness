// CompositionContextInitializationTests.cs — the #562 guard.
//
// HarborCompositionContext declared seven public non-nullable members and
// initialised five of them with `null!`:
//
//     public IEventBus EventBus { get; internal set; } = null!;
//     public AgentRegistry Agents { get; internal set; } = null!;   // …and 3 more
//
// Nothing in the type, and no test, recorded that they are non-null only
// because some EARLIER AddHarbor module happened to assign them. A module
// reordered in Registration.AddHarbor therefore did not fail — it read a null
// member and turned it into a NullReferenceException three frames deeper, or
// (worse) into a silently empty registry: StorageModule reads
// ctx.Registries.SessionStores, and running before AddHarborRegistries gave it
// an empty plugin map and a working-looking container.
//
// These tests pin the fixed contract, in two directions:
//
//   * a member nobody assigned is LOUD — reading it names the member and the
//     module that owns the assignment, so the reorder is diagnosed at the
//     composition call site instead of at the first NRE downstream;
//   * a member that IS assigned is assigned ONCE and identically — the
//     container hands out the very instances the context holds.
//
// The reflection sweep is the part that generalises: it reads every public
// instance property of both composition types and fails on any that returns
// null in silence, so the next `= null!` in this file is caught even if it is
// a member nobody thought to name here.
//
// NotInParallel: the composed cases share the process-wide environment
// variables (HARBOR_STORAGE / HARBOR_TUI / HARBOR_MODE) with the other hosting
// suites.

using System.Reflection;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.Registries.Agents;
using Harbor.Registries.Providers;
using Harbor.Registries.Tools;

namespace Harbor.Hosting.Tests;

[NotInParallel("hosting")]
public class CompositionContextInitializationTests
{
    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-hosting-562", Guid.NewGuid().ToString("N"));

    private static HarborComposeOptions Options() => new()
    {
        HarborDir = TempHarborDir(),
        DefaultStorageBackend = "memory",
        DefaultTuiRenderer = "plain",
    };

    /// <summary>A context that has been constructed but has run no module at all.</summary>
    private static HarborCompositionContext BareContext() =>
        new(Options(), NullLoggerFactory.Instance);

    /// <summary>
    ///     Every public instance property of <paramref name="target" /> that
    ///     returns null. A property that THROWS is not in this list — a member
    ///     that says "I was never assigned, and here is who assigns me" is the
    ///     contract this file exists to pin.
    /// </summary>
    private static IReadOnlyList<string> SilentNullMembers(object target)
    {
        var silent = new List<string>();
        foreach (PropertyInfo property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(target);
            }
            catch (TargetInvocationException)
            {
                continue; // Loud by construction.
            }

            if (value is null)
            {
                silent.Add($"{target.GetType().Name}.{property.Name}");
            }
        }

        return silent;
    }

    // ── Read before assign: loud, named, and not an NRE ──────────────────

    [Test]
    public async Task BareContext_EventBus_NamesTheModuleThatAssignsIt()
    {
        HarborCompositionContext ctx = BareContext();

        var ex = Assert.Throws<InvalidOperationException>(() => { _ = ctx.EventBus; });

        await Assert.That(ex!.Message).Contains("EventBus")
            .Because("the message must name the member — an NRE three frames deeper names nothing");
        await Assert.That(ex.Message).Contains("AddHarborConfiguration")
            .Because("the message must name the module that owns the assignment, which is the edit a reorder needs");
    }

    [Test]
    public async Task BareContext_Registries_NamesTheModuleThatAssignsIt()
    {
        HarborCompositionContext ctx = BareContext();

        var ex = Assert.Throws<InvalidOperationException>(() => { _ = ctx.Registries; });

        await Assert.That(ex!.Message).Contains("Registries");
        await Assert.That(ex.Message).Contains("AddHarborRegistries");
    }

    [Test]
    public async Task BareContext_NoPublicMember_ReturnsSilentNull()
    {
        HarborCompositionContext ctx = BareContext();

        List<string> silent = [.. SilentNullMembers(ctx)];

        await Assert.That(silent).IsEmpty()
            .Because(
                "a public non-nullable member of the composition context must either hold a value or refuse to "
                + "hand one out. The five `= null!` properties this file was written for returned null in "
                + "silence, so no reader could tell a composed context from a fresh one.");
    }

    [Test]
    public async Task BareContext_ExactlyTheTwoOwnedMembers_AreTheOnesThatRefuse()
    {
        HarborCompositionContext ctx = BareContext();

        // The ratchet in the other direction: the loud set is pinned, so adding
        // a sixth "assigned later" member is a deliberate edit to this file
        // rather than an accident that only shows up as an NRE in production.
        var loud = new List<string>();
        foreach (PropertyInfo property in typeof(HarborCompositionContext).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            try
            {
                _ = property.GetValue(ctx);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
            {
                loud.Add(property.Name);
            }
        }

        await Assert.That(loud.Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(new[] { "EventBus", "Registries" })
            .Because(
                "EventBus is assigned by AddHarborConfiguration and Registries by AddHarborRegistries; every other "
                + "public member of the context is non-null from the constructor on.");
    }

    // ── The reorder itself: every dependent module fails at its own call ──

    [Test]
    public async Task TelemetryBeforeConfiguration_FailsLoudly_InsteadOfSkippingTheBusMetrics()
    {
        var services = new ServiceCollection();
        HarborCompositionContext ctx = BareContext();

        // What the composition root does today: AddHarborTelemetry(ctx) with a
        // context whose EventBus was never assigned. `if (ctx.EventBus is
        // IEventBusQueueMetrics …)` treated the unassigned bus as "this bus is
        // not instrumented" and skipped the queue-age reporter — a process
        // that comes up with no event-bus metrics and nothing in the log.
        var ex = Assert.Throws<InvalidOperationException>(() => { _ = services.AddHarborTelemetry(ctx); });

        await Assert.That(ex!.Message).Contains("AddHarborConfiguration")
            .Because("the reorder's fix is to move one line; the error has to say which one");
    }

    [Test]
    public async Task StorageBeforeRegistries_FailsLoudly_InsteadOfComposingAnEmptyPluginMap()
    {
        var services = new ServiceCollection();
        HarborCompositionContext ctx = services.AddHarborConfiguration(Options());

        // The silent half of the issue: with the registries unassigned, this
        // used to read an empty SessionStores map, resolve "memory", and hand
        // back a container that looks complete but can never see a plugin
        // backend. A wrong order must not be a working-looking container.
        var ex = Assert.Throws<InvalidOperationException>(() => { _ = services.AddHarborStorage(ctx); });

        await Assert.That(ex!.Message).Contains("Registries");
        await Assert.That(ex.Message).Contains("AddHarborRegistries");
    }

    // ── Assigned: once, and the same instance the container publishes ─────

    [Test]
    public async Task ComposedContext_HasAllFiveMembersAssigned()
    {
        var services = new ServiceCollection();
        HarborCompositionContext ctx = services.AddHarbor(Options());
        using ServiceProvider sp = services.BuildServiceProvider();

        await Assert.That(ctx.EventBus).IsNotNull();
        await Assert.That(ctx.Registries.Agents).IsNotNull();
        await Assert.That(ctx.Registries.Tools).IsNotNull();
        await Assert.That(ctx.Registries.Providers).IsNotNull();
        await Assert.That(ctx.Registries.Panels).IsNotNull()
            .Because(
                "the issue's minimum: one test that resolves the context through AddHarbor and asserts all five, "
                + "so a reorder fails here instead of somewhere downstream");

        // Same instances, not merely non-null ones: the composition context is
        // the raw (pre-instrumentation) view of the registries, and the
        // instrumented views the container publishes wrap exactly these.
        await Assert.That(sp.GetRequiredService<IEventBus>()).IsSameReferenceAs(ctx.EventBus);
        await Assert.That(sp.GetRequiredService<PanelRegistry>()).IsSameReferenceAs(ctx.Registries.Panels);
        await Assert.That(sp.GetRequiredService<IAgentRegistry>()).IsSameReferenceAs(ctx.Registries.Agents);
    }

    [Test]
    public async Task ComposedContext_NoPublicMember_ReturnsSilentNull()
    {
        var services = new ServiceCollection();
        HarborCompositionContext ctx = services.AddHarbor(Options());
        using ServiceProvider _ = services.BuildServiceProvider();

        var silent = new List<string>(SilentNullMembers(ctx));
        silent.AddRange(SilentNullMembers(ctx.Registries));

        await Assert.That(silent).IsEmpty()
            .Because(
                "after composition every public member of both types holds a real object. This is the direction "
                + "that the throwing getters must not have broken on the happy path.");
    }

    [Test]
    public async Task Registries_AreAssignedExactlyOnce()
    {
        HarborCompositionContext ctx = BareContext();
        ctx.SetRegistries(new HarborRegistries(
            new AgentRegistry(), new ToolRegistry(), new ProviderRegistry(), new PanelRegistry()));

        var ex = Assert.Throws<InvalidOperationException>(() => ctx.SetRegistries(
            new HarborRegistries(
                new AgentRegistry(), new ToolRegistry(), new ProviderRegistry(), new PanelRegistry())));

        await Assert.That(ex!.Message).Contains("already")
            .Because(
                "a second assignment means AddHarborRegistries ran twice; two bundles would split the process over "
                + "two registry sets, which is the same class of silent wrong-order bug this issue is about");
    }

    [Test]
    public async Task RegistryBundle_CannotBeConstructedHalfBuilt()
    {
        // The `null!` below is the only one in the test suite, and it is the
        // point: it stands for the caller the compiler CANNOT stop — a plugin or
        // an embedded host building the bundle by hand. The old shape
        // (`= null!` + internal setter) accepted that caller silently and failed
        // at the first read; the constructor rejects it, by argument name.
        var ex = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new HarborRegistries(new AgentRegistry(), new ToolRegistry(), new ProviderRegistry(), null!);
        });

        await Assert.That(ex!.ParamName).IsEqualTo("panels")
            .Because(
                "the four registries are constructor arguments, so a half-built bundle is a compile error for the "
                + "three honest ones and a constructor rejection for the null-forgiving one — and the argument name "
                + "says which. The old shape (`= null!` plus an internal setter) made all four a runtime NRE.");
    }
}
