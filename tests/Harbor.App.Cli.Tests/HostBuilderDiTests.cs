using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Core.Configuration;
using Harbor.Core.Onboarding;
using Harbor.Core.Sessions;
using Harbor.Core.Tools;
using Harbor.Cli.Hosting;
using Harbor.Plugins.Abstractions;
using Harbor.Terminal.Abstractions;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
// Alias avoids CS0104 ambiguity between Harbor.Cli.Hosting.HostBuilder (the
// one we want to call) and Microsoft.Extensions.Hosting.HostBuilder (the
// generic host builder class pulled in by the Microsoft.Extensions.Hosting using).
using HostBuilder = Harbor.Cli.Hosting.HostBuilder;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

// DI006 (do not cache IServiceProvider in static fields) is intentionally
// violated by this test fixture: the whole point of the file is to build the
// host once and resolve services from it across many [Test] methods. The
// host's lifetime is bounded by the test process; there is no captive-
// dependency risk because no production Singleton captures the test's
// ServiceProvider. Suppressing here keeps the build clean without weakening
// the rule for production code (where DI006 is a real bug — see ANALYZERS.md).
#pragma warning disable DI006

namespace Harbor.App.Cli.Tests;

/// <summary>
///     DI registration tests for <see cref="HostBuilder.Build"/>.
///     Each [Test] resolves one (or a related group) of the services declared
///     with [Exposes(typeof(T))] on HostBuilder.Build. A failure here means
///     either (a) a service registration was removed accidentally, or
///     (b) one of its dependencies is missing (will throw at resolve time).
/// </summary>
/// <remarks>
///     <para>
///         <b>Test isolation:</b> <see cref="HostBuilder.Build"/> creates the
///         <c>~/.harbor</c> directory tree on first run and reads
///         <c>~/.harbor/config.json</c>. Tests use the real user home so they
///         exercise the same path the production CLI uses. The JsonConfigStore
///         returns defaults when the file is absent, so no setup is required.
///     </para>
///     <para>
///         <b>HARBOR_MINIMAL:</b> the DI surface is the same with or without
///         the HARBOR_MINIMAL flag — only the CS plugin loader is skipped.
///         These tests do not require HARBOR_MINIMAL.
///     </para>
/// </remarks>
public class HostBuilderDiTests
{
    /// <summary>
    ///     Cached host instance — built once per test session. TUnit creates a
    ///     fresh instance of the test class for each [Test] method, so we use a
    ///     static field to share the host. The host's lifetime ends with the
    ///     test process — no explicit disposal is needed.
    /// </summary>
    private static readonly Lazy<IHost> _hostLazy = new(() =>
    {
        var host = HostBuilder.Build("--log-level", "Warning");
        return host;
    });

    private static IServiceProvider Services => _hostLazy.Value.Services;

    private static IHost Host => _hostLazy.Value;

    /// <summary>
    ///     Class-level cleanup: NO-OP. The cached host lives for the entire
    ///     test process and is torn down by the OS. Earlier versions of this
    ///     file used <c>[After(HookType.Class)]</c> to dispose the host, but
    ///     TUnit 0.50 runs that hook per-class-instance (one per test method),
    ///     which disposed the shared host mid-test-run and caused subsequent
    ///     tests to fail with "Cannot access a disposed object". Removing the
    ///     hook is safe — the test process exits and the OS reclaims resources.
    /// </summary>
    // [After(HookType.Class)] — intentionally omitted; see summary above.

    // ── Core services ─────────────────────────────────────────────────────

    [Test]
    public async Task Build_Registers_IConfigStore()
    {
        await Assert.That(Services.GetService<IConfigStore>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_AuthStore()
    {
        await Assert.That(Services.GetService<AuthStore>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_OnboardingWizard()
    {
        await Assert.That(Services.GetService<OnboardingWizard>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_ITokenEstimator()
    {
        await Assert.That(Services.GetService<ITokenEstimator>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_IEventBus()
    {
        await Assert.That(Services.GetService<IEventBus>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_ISystemPromptBuilder()
    {
        await Assert.That(Services.GetService<ISystemPromptBuilder>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_MessageConverter()
    {
        await Assert.That(Services.GetService<MessageConverter>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_IAgentLoop()
    {
        await Assert.That(Services.GetService<IAgentLoop>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_IAgent()
    {
        await Assert.That(Services.GetService<IAgent>()).IsNotNull();
    }

    // ── Registries ────────────────────────────────────────────────────────

    [Test]
    public async Task Build_Registers_IAgentRegistry()
    {
        await Assert.That(Services.GetService<IAgentRegistry>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_IToolRegistry()
    {
        await Assert.That(Services.GetService<IToolRegistry>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_IProviderRegistry()
    {
        await Assert.That(Services.GetService<IProviderRegistry>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_IMcpRegistry()
    {
        await Assert.That(Services.GetService<IMcpRegistry>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_PanelRegistry_Concrete()
    {
        await Assert.That(Services.GetService<PanelRegistry>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_IPanelRegistry()
    {
        await Assert.That(Services.GetService<IPanelRegistry>()).IsNotNull();
    }

    // ── Services with deps ────────────────────────────────────────────────

    [Test]
    public async Task Build_Registers_ICompactionService()
    {
        await Assert.That(Services.GetService<ICompactionService>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_IPermissionService()
    {
        await Assert.That(Services.GetService<IPermissionService>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_ISessionStore()
    {
        await Assert.That(Services.GetService<ISessionStore>()).IsNotNull();
    }

    [Test]
    public async Task Build_Registers_ITuiRenderer()
    {
        await Assert.That(Services.GetService<ITuiRenderer>()).IsNotNull();
    }

    // ── Aggregate / cross-cutting ─────────────────────────────────────────

    /// <summary>
    ///     Aggregate test: builds the host and resolves every [Exposes(typeof(T))]
    ///     service in one shot. Provides a single test-id to look at when the DI
    ///     surface breaks, instead of 18 separate failures.
    /// </summary>
    [Test]
    public async Task Build_AllDeclaredServices_Resolvable()
    {
        var sp = Services;

        var required = new[]
        {
            typeof(IConfigStore),
            typeof(AuthStore),
            typeof(OnboardingWizard),
            typeof(ITokenEstimator),
            typeof(IEventBus),
            typeof(ISystemPromptBuilder),
            typeof(MessageConverter),
            typeof(IAgentLoop),
            typeof(IAgent),
            typeof(IAgentRegistry),
            typeof(IToolRegistry),
            typeof(IProviderRegistry),
            typeof(IMcpRegistry),
            typeof(PanelRegistry),
            typeof(IPanelRegistry),
            typeof(ICompactionService),
            typeof(IPermissionService),
            typeof(ISessionStore),
            typeof(ITuiRenderer),
        };

        var missing = new List<Type>();
        foreach (var t in required)
        {
            var svc = sp.GetService(t);
            if (svc is null)
            {
                missing.Add(t);
            }
        }

        await Assert.That(missing).IsEmpty();
    }

    /// <summary>
    ///     HTTP client factory is registered via <c>services.AddHttpClient</c>
    ///     for named clients: anthropic, openai, ollama, providers, default.
    ///     Resolving the factory itself should always succeed; the named clients
    ///     are exercised here too.
    /// </summary>
    [Test]
    public async Task Build_Registers_HttpClientFactory_AndNamedClients()
    {
        var factory = Services.GetService<IHttpClientFactory>();
        await Assert.That(factory).IsNotNull();

        foreach (var name in new[] { "anthropic", "openai", "ollama", "providers", "default" })
        {
            var client = factory!.CreateClient(name);
            await Assert.That(client).IsNotNull();
        }
    }

    /// <summary>
    ///     No service should resolve to null when the host is built. This is a
    ///     regression guard against accidental removal of registrations: if the
    ///     aggregate test above fails, this one fails too with a different
    ///     signal (exception vs assertion) — useful for triage.
    /// </summary>
    [Test]
    public async Task Build_ResolvingRequiredServices_DoesNotThrow()
    {
        var sp = Services;

        void Resolve<T>() where T : notnull => sp.GetRequiredService<T>();

        // If any of these throw, the test fails fast.
        Resolve<IConfigStore>();
        Resolve<AuthStore>();
        Resolve<ITokenEstimator>();
        Resolve<IEventBus>();
        Resolve<IAgentLoop>();
        Resolve<IAgent>();
        Resolve<IToolRegistry>();
        Resolve<IProviderRegistry>();
        Resolve<IPermissionService>();
        Resolve<ISessionStore>();
        Resolve<IHttpClientFactory>();

        // Reaching this line means none of the Resolve<T>() calls above threw,
        // so the test passes. No trailing Assert.That(...) is needed — TUnit
        // treats a [Test] method that returns without throwing as Passed.
    }

    /// <summary>
    ///     Resolving a singleton interface twice should return the same instance.
    ///     This guards against accidental transient registration of a service
    ///     that the rest of the app assumes is shared (e.g. IEventBus — every
    ///     subscriber needs to see the same bus).
    /// </summary>
    [Test]
    public async Task Build_Singletons_AreSharedInstances()
    {
        var sp = Services;

        var bus1 = sp.GetRequiredService<IEventBus>();
        var bus2 = sp.GetRequiredService<IEventBus>();
        await Assert.That(ReferenceEquals(bus1, bus2)).IsTrue();

        var tools1 = sp.GetRequiredService<IToolRegistry>();
        var tools2 = sp.GetRequiredService<IToolRegistry>();
        await Assert.That(ReferenceEquals(tools1, tools2)).IsTrue();

        var providers1 = sp.GetRequiredService<IProviderRegistry>();
        var providers2 = sp.GetRequiredService<IProviderRegistry>();
        await Assert.That(ReferenceEquals(providers1, providers2)).IsTrue();
    }
}
