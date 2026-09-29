using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Sessions;
using Harbor.Hosting.Rendering;
using Harbor.Storage.Memory;
using Harbor.Terminal.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting.Tests;

/// <summary>
///     Issues #581/#584: the backend registries carried a SECOND hand-maintained copy of
///     their own key set — <c>SessionStoreRegistry.KnownIds</c>,
///     <c>HarborModeRegistry.KnownIds</c>, and, on the TUI axis, a four-entry
///     <c>pipeline.Register(...)</c> list in <c>TuiModule</c> that had fallen six backends
///     behind the ten compiled in. Adjacency is not enforcement: <c>rg "KnownIds" tests/</c>
///     returned nothing, so registering a backend without editing the string shipped a
///     process whose error message named a set that did not exist.
/// </summary>
/// <remarks>
///     Both copies are gone — the id lists are derived from <c>registry.Keys</c> at the call
///     site and the swap table from <c>TuiBackendRegistry.Build()</c>. These tests pin what
///     the deletion can no longer give for free: that the DERIVED set is still exactly the
///     resolvable set, in both directions, and that a backend registered but not surfaced
///     anywhere is a failure. Without them the derivation would be correct today and could
///     still be bypassed by the next caller that hand-writes a list.
/// </remarks>
[NotInParallel("hosting")]
public class BackendRegistrySingleSourceTests
{
    private const string ExpectedIdsPattern = @"Expected one of: (?<ids>[^.]*)\.";

    // ── helpers ──────────────────────────────────────────────────────────

    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-hosting-single-source-tests", Guid.NewGuid().ToString("N"));

    private static HarborComposeOptions BaselineOptions() =>
        new() { HarborDir = TempHarborDir(), DefaultStorageBackend = "memory", DefaultTuiRenderer = "plain" };

    private static async Task WithEnv(string name, string? value, Func<Task> action)
    {
        string? previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    /// <summary>
    ///     Compose with <paramref name="envName" /> set to a value no registry can resolve,
    ///     and pull the "Expected one of: …" id list back out of the thrown message. The
    ///     list the user sees IS the assertion target — that is the whole point of the
    ///     issue: the message used to be a string literal nobody compared to the registry.
    /// </summary>
    private static async Task<string[]> ReportedIdsFor(string envName, string bogusValue)
    {
        string[] reported = [];
        await WithEnv("HARBOR_MODE", null, async () =>
        {
            await WithEnv("HARBOR_STORAGE", null, async () =>
            {
                await WithEnv("HARBOR_TUI", null, async () =>
                {
                    await WithEnv(envName, bogusValue, () =>
                    {
                        try
                        {
                            // Composition throws before the provider is ever resolved;
                            // building and immediately disposing is the whole point.
                            using (ServiceProvider ignored = Compose())
                            {
                            }
                        }
                        catch (ArgumentException ex)
                        {
                            Match match = Regex.Match(ex.Message, ExpectedIdsPattern);
                            reported = match.Success
                                ? match.Groups["ids"].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries)
                                : [];
                        }

                        return Task.CompletedTask;
                    });
                });
            });
        });

        return reported;
    }

    private static ServiceProvider Compose()
    {
        var services = new ServiceCollection();
        services.AddHarbor(BaselineOptions());
        return services.BuildServiceProvider();
    }

    // ── storage ──────────────────────────────────────────────────────────

    /// <summary>
    ///     The bidirectional gate for the storage axis: every id the unknown-backend error
    ///     names must resolve, and every id the registry holds must be named. Delete a
    ///     factory without touching the message and this fails; name an id the registry
    ///     dropped and this fails too.
    /// </summary>
    [Test]
    public async Task Storage_ReportedIds_AreExactlyTheResolvableSet()
    {
        string[] reported = await ReportedIdsFor("HARBOR_STORAGE", "bogus-store-581");
        // The message must name the known backends at all — otherwise the
        // "every reported id resolves" loop below would pass vacuously.
        await Assert.That(reported.Length).IsGreaterThan(0);

        FrozenDictionary<string, ISessionStoreFactory> registry = SessionStoreRegistry.Build();

        foreach (string id in reported)
        {
            // Direction 1: every id the message names must actually resolve.
            Maybe<ISessionStoreFactory> factory = SessionStoreRegistry.Resolve(registry, id);
            await Assert.That(factory.HasValue).IsTrue();
            await Assert.That(factory.Value.BackendId).IsEqualTo(id);
        }

        foreach (string id in registry.Keys)
        {
            // Direction 2: every registered backend must be named.
            await Assert.That(reported).Contains(id);
        }
    }

    /// <summary>
    ///     A plugin backend reaches the same registry and therefore the same reported set
    ///     — the door added in #581 is wired to the single source, not to a parallel list.
    /// </summary>
    [Test]
    public async Task Storage_PluginBackend_JoinsTheReportedSet()
    {
        var pluginStores = new Dictionary<string, Func<ISessionStore>>(StringComparer.OrdinalIgnoreCase)
        {
            ["redis"] = () => new MemorySessionStore(),
        };

        FrozenDictionary<string, ISessionStoreFactory> registry = SessionStoreRegistry.Build(pluginStores);

        await Assert.That(registry.ContainsKey("redis")).IsTrue();
        Maybe<ISessionStoreFactory> factory = SessionStoreRegistry.Resolve(registry, "REDIS");
        await Assert.That(factory.HasValue).IsTrue();
        await Assert.That(factory.Value.BackendId).IsEqualTo("redis");
        await Assert.That(factory.Value).IsTypeOf<PluginSessionStoreFactory>();

        // …and the compiled-in backends are still there: the merge is additive.
        await Assert.That(registry.ContainsKey("memory")).IsTrue();
        await Assert.That(registry.ContainsKey("jsonl")).IsTrue();
    }

    // ── HARBOR_MODE ──────────────────────────────────────────────────────

    /// <summary>
    ///     The same bidirectional gate for <c>HARBOR_MODE</c>, whose <c>KnownIds</c> string
    ///     was the second of the two hand-written copies in the issue.
    /// </summary>
    [Test]
    public async Task Mode_ReportedIds_AreExactlyTheResolvableSet()
    {
        string[] reported = await ReportedIdsFor("HARBOR_MODE", "bogus-mode-581");
        // The message must name the known modes at all — otherwise the
        // "every reported id resolves" loop below would pass vacuously.
        await Assert.That(reported.Length).IsGreaterThan(0);

        FrozenDictionary<string, IHarborModeStrategy> registry = HarborModeRegistry.Build();

        foreach (string id in reported)
        {
            // Direction 1: every id the message names must actually resolve.
            Maybe<IHarborModeStrategy> strategy = HarborModeRegistry.Resolve(registry, id);
            await Assert.That(strategy.HasValue).IsTrue();
            await Assert.That(strategy.Value.ModeId).IsEqualTo(id);
        }

        foreach (string id in registry.Keys)
        {
            // Direction 2: every registered mode must be named.
            await Assert.That(reported).Contains(id);
        }
    }

    // ── TUI backends ─────────────────────────────────────────────────────

    /// <summary>
    ///     The #584 gate, and the strongest of the four: the runtime-swap table is derived
    ///     from <see cref="TuiBackendRegistry" />, so every registered backend must be a
    ///     swap target and every swap target must be a registered backend. Before the fix
    ///     the second direction held while the first did not — six compiled-in backends were
    ///     absent from the pipeline, and <c>/renderer</c> printed three of ten.
    /// </summary>
    [Test]
    public async Task Tui_SwapTable_IsExactlyTheRegisteredBackendSet()
    {
        await WithEnv("HARBOR_MODE", null, async () =>
        {
            await WithEnv("HARBOR_STORAGE", null, async () =>
            {
                await WithEnv("HARBOR_TUI", "plain", async () =>
                {
                    using var sp = Compose();
                    IRendererPipeline pipeline = sp.GetRequiredService<IRendererPipeline>();
                    var swapTargets = pipeline.AvailableBackends.ToArray();

                    FrozenDictionary<string, ITuiRendererFactory> registry = TuiBackendRegistry.Build();

                    // Every id the registry knows (canonical ids and aliases) is swappable.
                    foreach (string id in registry.Keys)
                    {
                        await Assert.That(swapTargets).Contains(id);
                    }

                    // …and the pipeline offers nothing that is not a registered backend.
                    foreach (string id in swapTargets)
                    {
                        await Assert.That(registry.ContainsKey(id)).IsTrue();
                    }
                });
            });
        });
    }

    /// <summary>
    ///     The startup backend is always a swap target, and the current id names the real
    ///     renderer (no half-registration skew) — the invariant the half-registered
    ///     Spectre backends were violating before #584.
    /// </summary>
    [Test]
    public async Task Tui_StartupBackend_IsSwappableAndIdMatchesTheRenderer()
    {
        await WithEnv("HARBOR_MODE", null, async () =>
        {
            await WithEnv("HARBOR_STORAGE", null, async () =>
            {
                await WithEnv("HARBOR_TUI", "consoleex", async () =>
                {
                    using var sp = Compose();
                    IRendererPipeline pipeline = sp.GetRequiredService<IRendererPipeline>();
                    var swapTargets = pipeline.AvailableBackends.ToArray();

                    await Assert.That(pipeline.CurrentBackendId).IsEqualTo("cellforge");
                    await Assert.That(swapTargets).Contains("cellforge");
                    await Assert.That(swapTargets).Contains("consoleex");
                });
            });
        });
    }

    /// <summary>
    ///     A plugin backend reaches the registry through the #581 door and therefore the
    ///     swap table through the same loop — the mirror of the storage test above.
    /// </summary>
    [Test]
    public async Task Tui_PluginBackend_JoinsTheSwapTable()
    {
        var pluginBackends = new Dictionary<string, PluginTuiBackend>(StringComparer.OrdinalIgnoreCase)
        {
            ["web"] = new PluginTuiBackend(new[] { "webui" }, () => new Harbor.Tui.AnsiPlain.PlainTuiRenderer()),
        };

        FrozenDictionary<string, ITuiRendererFactory> registry = TuiBackendRegistry.Build(pluginBackends);

        await Assert.That(registry.ContainsKey("web")).IsTrue();
        await Assert.That(registry.ContainsKey("webui")).IsTrue();
        await Assert.That(registry["web"]).IsTypeOf<PluginTuiRendererFactory>();

        // Compiled-in backends survive the merge.
        await Assert.That(registry.ContainsKey("plain")).IsTrue();
        await Assert.That(registry.ContainsKey("ansi")).IsTrue();
        await Assert.That(registry.ContainsKey("cellforge")).IsTrue();
    }

    /// <summary>
    ///     The registries stay lazy: building one must not construct a store or a renderer.
    ///     Both <c>Build</c> overloads take a factory map, and neither may invoke it — the
    ///     <c>Func&lt;ISessionStore&gt;</c> / <c>Func&lt;ITuiRenderer&gt;</c> only runs when
    ///     the backend is selected or swapped to. Forcing eager construction here would
    ///     turn a plugin's registration into a startup cost it never asked for.
    /// </summary>
    [Test]
    public async Task Registries_DoNotInvokePluginFactoriesAtBuildTime()
    {
        int storeCalls = 0;
        int rendererCalls = 0;

        var pluginStores = new Dictionary<string, Func<ISessionStore>>(StringComparer.OrdinalIgnoreCase)
        {
            ["lazy-store"] = () => { storeCalls++; return new MemorySessionStore(); },
        };
        var pluginBackends = new Dictionary<string, PluginTuiBackend>(StringComparer.OrdinalIgnoreCase)
        {
            ["lazy-tui"] = new PluginTuiBackend(null, () =>
            {
                rendererCalls++;
                return new Harbor.Tui.AnsiPlain.PlainTuiRenderer();
            }),
        };
        FrozenDictionary<string, ISessionStoreFactory> storeRegistry = SessionStoreRegistry.Build(pluginStores);
        await Assert.That(storeRegistry.ContainsKey("lazy-store")).IsTrue();
        await Assert.That(storeCalls).IsEqualTo(0);

        FrozenDictionary<string, ITuiRendererFactory> tuiRegistry = TuiBackendRegistry.Build(pluginBackends);
        await Assert.That(tuiRegistry.ContainsKey("lazy-tui")).IsTrue();
        await Assert.That(rendererCalls).IsEqualTo(0);

        // The no-argument overloads are the compiled-in-only case and must stay honest too.
        await Assert.That(SessionStoreRegistry.Build().ContainsKey("memory")).IsTrue();
        await Assert.That(TuiBackendRegistry.Build().ContainsKey("plain")).IsTrue();
    }

    /// <summary>
    ///     The plugin-free overloads are the compiled-in-only view; the argument overloads
    ///     are the same set plus the plugin contribution. Guards against the two drifting
    ///     apart when a new overload is added.
    /// </summary>
    [Test]
    public async Task Registries_EmptyPluginMaps_MatchTheNoArgumentOverloads()
    {
        var emptyStores = ImmutableDictionary<string, Func<ISessionStore>>.Empty;
        var emptyBackends = ImmutableDictionary<string, PluginTuiBackend>.Empty;

        await Assert.That(SessionStoreRegistry.Build(emptyStores).Keys.Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(SessionStoreRegistry.Build().Keys.Order(StringComparer.Ordinal).ToArray());

        await Assert.That(TuiBackendRegistry.Build(emptyBackends).Keys.Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(TuiBackendRegistry.Build().Keys.Order(StringComparer.Ordinal).ToArray());
    }
}
