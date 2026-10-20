using Harbor.Abstractions.Sessions;
using Harbor.Hosting.Rendering;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Tui.AnsiPlain;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting.Tests;

/// <summary>
///     Issue #175: strategy-registry dispatchers (TUI / storage / HARBOR_MODE).
///     Pins the behavioral contract through the public composition surface:
///     every previously valid env value resolves to the same backend as
///     before, and an unknown storage id is now an explicit failure instead
///     of a silent jsonl fallback.
/// </summary>
[NotInParallel("hosting")]
public class HostingStrategyRegistryTests
{
    // ── helpers ──────────────────────────────────────────────────────────

    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-hosting-strategy-tests", Guid.NewGuid().ToString("N"));

    private static ServiceProvider Compose(HarborComposeOptions options)
    {
        var services = new ServiceCollection();
        services.AddHarbor(options);
        return services.BuildServiceProvider();
    }
    private static HarborComposeOptions BaselineOptions() =>
        new() { HarborDir = TempHarborDir(), DefaultStorageBackend = "memory", DefaultTuiRenderer = "plain" };

    // ── Storage: unknown id is now an explicit failure ───────────────────

    [Test]
    public async Task Storage_UnknownId_ThrowsArgumentException_NamingIdAndKnownBackends()
    {
        await EnvScope.WithAsync("HARBOR_MODE", null, async () =>
        {
            await EnvScope.WithAsync("HARBOR_STORAGE", "bogus-backend-175", async () =>
            {
                ArgumentException? caught = null;
                try
                {
                    using var sp = Compose(BaselineOptions());
                }
                catch (ArgumentException ex)
                {
                    caught = ex;
                }

                await Assert.That(caught).IsNotNull();
                await Assert.That(caught!.Message.Contains("bogus-backend-175")).IsTrue();
                await Assert.That(caught.Message.Contains("memory")).IsTrue();
                await Assert.That(caught.Message.Contains("jsonl")).IsTrue();
            });
        });
    }

    [Test]
    public async Task Storage_Ids_AreCaseInsensitive_AndTrimmed()
    {
        await EnvScope.WithAsync("HARBOR_MODE", null, async () =>
        {
            await EnvScope.WithAsync("HARBOR_STORAGE", "MEMORY", async () =>
            {
                using var sp = Compose(BaselineOptions());
                await Assert.That(sp.GetRequiredService<ISessionStore>()).IsTypeOf<MemorySessionStore>();
            });

            await EnvScope.WithAsync("HARBOR_STORAGE", "  Jsonl  ", async () =>
            {
                using var sp = Compose(BaselineOptions());
                await Assert.That(sp.GetRequiredService<ISessionStore>()).IsTypeOf<JsonlSessionStore>();
            });
        });
    }

    // ── TUI: alias + canonical id + single fallback rule ─────────────────

    [Test]
    public async Task Tui_ConsoleexAlias_ResolvesCellForge()
    {
        await EnvScope.WithAsync("HARBOR_MODE", null, async () =>
        {
            await EnvScope.WithAsync("HARBOR_TUI", "consoleex", async () =>
            {
                using var sp = Compose(BaselineOptions());
                await Assert.That(sp.GetRequiredService<ITuiRenderer>())
                    .IsTypeOf<Harbor.Tui.CellForge.CellForgeTuiRenderer>();
                await Assert.That(sp.GetRequiredService<IRendererPipeline>().CurrentBackendId)
                    .IsEqualTo("cellforge");
            });
        });
    }

    [Test]
    public async Task Tui_CellforgeCanonicalId_ResolvesCellForge()
    {
        await EnvScope.WithAsync("HARBOR_MODE", null, async () =>
        {
            await EnvScope.WithAsync("HARBOR_TUI", "cellforge", async () =>
            {
                using var sp = Compose(BaselineOptions());
                await Assert.That(sp.GetRequiredService<ITuiRenderer>())
                    .IsTypeOf<Harbor.Tui.CellForge.CellForgeTuiRenderer>();
                await Assert.That(sp.GetRequiredService<IRendererPipeline>().CurrentBackendId)
                    .IsEqualTo("cellforge");
            });
        });
    }

    [Test]
    public async Task Tui_UnknownId_FallsBackToBaselineRenderer_WithMatchingPipelineId()
    {
        await EnvScope.WithAsync("HARBOR_MODE", null, async () =>
        {
            await EnvScope.WithAsync("HARBOR_TUI", "bogus-tui-175", async () =>
            {
                using var sp = Compose(BaselineOptions());
                object renderer = sp.GetRequiredService<ITuiRenderer>();
                bool isAnsi = renderer is AnsiTuiRenderer;
                bool isPlain = renderer is PlainTuiRenderer;
                await Assert.That(isAnsi || isPlain).IsTrue();

                // The pipeline id always names the ACTUAL renderer (no silent skew).
                string backendId = sp.GetRequiredService<IRendererPipeline>().CurrentBackendId;
                await Assert.That(backendId == "ansi" || backendId == "plain").IsTrue();
                await Assert.That((isAnsi && backendId == "ansi") || (isPlain && backendId == "plain")).IsTrue();
            });
        });
    }

    // ── HARBOR_MODE: strategies resolve, unknown modes fail fast ─────────

    [Test]
    public async Task Mode_UnknownId_ThrowsArgumentException_NamingId()
    {
        await EnvScope.WithAsync("HARBOR_STORAGE", null, async () =>
        {
            await EnvScope.WithAsync("HARBOR_MODE", "bogus-mode-175", async () =>
            {
                ArgumentException? caught = null;
                try
                {
                    using var sp = Compose(BaselineOptions());
                }
                catch (ArgumentException ex)
                {
                    caught = ex;
                }

                await Assert.That(caught).IsNotNull();
                await Assert.That(caught!.Message.Contains("bogus-mode-175")).IsTrue();
            });
        });
    }

    [Test]
    public async Task Mode_IpcServer_And_IpcClient_ComposeWithoutThrowing()
    {
        await EnvScope.WithAsync("HARBOR_STORAGE", null, async () =>
        {
            await EnvScope.WithAsync("HARBOR_TUI", null, async () =>
            {
                await EnvScope.WithAsync("HARBOR_LISTEN", null, async () =>
                {
                    await EnvScope.WithAsync("HARBOR_MODE", "ipc-server", async () =>
                    {
                        using var sp = Compose(BaselineOptions());
                        await Assert.That(sp).IsNotNull();
                    });

                    await EnvScope.WithAsync("HARBOR_MODE", "ipc-client", async () =>
                    {
                        using var sp = Compose(BaselineOptions());
                        await Assert.That(sp).IsNotNull();
                    });
                });
            });
        });
    }
}
