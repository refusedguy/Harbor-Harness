using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Tui.AnsiPlain;
using Harbor.Terminal.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting.Tests;

/// <summary>
///     Composition-root mirrors for the release presets (di-design §6.3 Ф2.7):
///     each preset must resolve its declared subset and must NOT resolve what
///     it excludes. Also pins the composition-order invariant §3.5 — registries
///     are frozen BEFORE they are published into the container (the resolved
///     snapshot already contains every tool registered during composition).
/// </summary>
[NotInParallel]
public class RegistrationCompositionTests
{
    // ── helpers ──────────────────────────────────────────────────────────

    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-hosting-tests", Guid.NewGuid().ToString("N"));

    private static ServiceProvider Compose(HarborComposeOptions options)
    {
        var services = new ServiceCollection();
        services.AddHarbor(options);
        return services.BuildServiceProvider();
    }

    /// <summary>Run <paramref name="action" /> with an env var pinned, restore afterwards.</summary>
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

    private static IReadOnlyList<string> ToolNames(IServiceProvider sp) =>
        sp.GetRequiredService<IToolRegistry>()
            .GetAllTools()
            .Select(t => t.Name.Value)
            .ToArray();

    // ── Full preset (CLI default) ────────────────────────────────────────

    [Test]
    public async Task AddHarbor_Full14_RegistersAll15Tools()
    {
        using var sp = Compose(new HarborComposeOptions { HarborDir = TempHarborDir(), DefaultStorageBackend = "memory" });

        var names = ToolNames(sp);
        // 14 classic tools + lsp (registered for both presets since the LSP
        // integration landed — 2ddd6ee) + skill (SKILL.md loader).
        await Assert.That(names.Count).IsEqualTo(16);
        foreach (string full in new[] { "task", "webfetch", "ripgrep", "mcp", "read", "write", "bash", "tree", "lsp", "skill" })
        {
            await Assert.That(names).Contains(full);
        }
    }

    // ── Standard preset (desktop subset) ─────────────────────────────────

    [Test]
    public async Task AddHarbor_Standard10_RegistersDesktopSubset_WithoutFullOnlyTools()
    {
        using var sp = Compose(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory",
            ToolSet = HarborToolSetKind.Standard10,
            IncludeMcpTools = false,
        });

        var names = ToolNames(sp);
        // 10 classic tools + lsp + skill.
        await Assert.That(names.Count).IsEqualTo(12);
        foreach (string safe in new[] { "read", "write", "edit", "bash", "glob", "grep", "ls", "patch", "notebook", "tree", "lsp", "skill" })
        {
            await Assert.That(names).Contains(safe);
        }

        foreach (string fullOnly in new[] { "task", "webfetch", "ripgrep", "mcp" })
        {
            await Assert.That(names).DoesNotContain(fullOnly);
        }
    }

    [Test]
    public async Task AddHarbor_Standard10_McpRegistryStillResolves_AsEmptyRegistry()
    {
        using var sp = Compose(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory",
            ToolSet = HarborToolSetKind.Standard10,
            IncludeMcpTools = false,
        });

        // Desktop view-models resolve IMcpRegistry unconditionally — the
        // subset preset provides an EMPTY registry rather than none.
        await Assert.That(sp.GetRequiredService<IMcpRegistry>()).IsNotNull();
    }

    // ── Composition order (§3.5): Freeze BEFORE publication ─────────────

    [Test]
    public async Task AddHarbor_PublishesFrozenRegistries_AsSingletons()
    {
        string harborDir = TempHarborDir();
        var services = new ServiceCollection();
        HarborCompositionContext ctx = services.AddHarbor(
            new HarborComposeOptions { HarborDir = harborDir, DefaultStorageBackend = "memory" });
        using var sp = services.BuildServiceProvider();

        var toolRegistry = sp.GetRequiredService<IToolRegistry>();
        var providerRegistry = sp.GetRequiredService<IProviderRegistry>();

        // sprint3-C C1: DI publishes an INSTRUMENTED VIEW over the same frozen
        // registries captured in the composition context — identical tool and
        // provider surfaces prove both views wrap one post-Freeze snapshot.
        await Assert.That(toolRegistry.GetAllTools().Count)
            .IsEqualTo(ctx.Registries.Tools.GetAllTools().Count);
        await Assert.That(toolRegistry.GetAllTools().Select(t => t.Name.Value).OrderBy(n => n).ToArray())
            .IsEquivalentTo(ctx.Registries.Tools.GetAllTools().Select(t => t.Name.Value).OrderBy(n => n).ToArray());
        await Assert.That(providerRegistry.GetRegisteredProviderIds())
            .IsEquivalentTo(ctx.Registries.Providers.GetRegisteredProviderIds());

        // Singleton lifetime.
        await Assert.That(toolRegistry).IsSameReferenceAs(sp.GetRequiredService<IToolRegistry>());

        // The published snapshot already includes everything registered during
        // composition → Freeze ran after registration and before publication.
        await Assert.That(toolRegistry.GetAllTools().Count).IsEqualTo(16);
    }

    // ── Storage presets ──────────────────────────────────────────────────

    [Test]
    public async Task AddHarbor_MemoryPreset_ResolvesMemorySessionStore()
    {
        await WithEnv("HARBOR_STORAGE", null, async () =>
        {
            using var sp = Compose(new HarborComposeOptions { HarborDir = TempHarborDir(), DefaultStorageBackend = "memory" });
            await Assert.That(sp.GetRequiredService<ISessionStore>()).IsTypeOf<MemorySessionStore>();
        });
    }

    [Test]
    public async Task AddHarbor_JsonlPreset_ResolvesJsonlSessionStore()
    {
        await WithEnv("HARBOR_STORAGE", null, async () =>
        {
            using var sp = Compose(new HarborComposeOptions { HarborDir = TempHarborDir(), DefaultStorageBackend = "jsonl" });
            await Assert.That(sp.GetRequiredService<ISessionStore>()).IsTypeOf<JsonlSessionStore>();
        });
    }

    [Test]
    public async Task AddHarbor_HARBOR_STORAGE_EnvOverridesPreset()
    {
        await WithEnv("HARBOR_STORAGE", "memory", async () =>
        {
            using var sp = Compose(new HarborComposeOptions { HarborDir = TempHarborDir(), DefaultStorageBackend = "jsonl" });
            await Assert.That(sp.GetRequiredService<ISessionStore>()).IsTypeOf<MemorySessionStore>();
        });
    }

    // ── TUI preset ───────────────────────────────────────────────────────

    [Test]
    public async Task AddHarbor_TuiRenderer_ResolvesPlainUnderMinimalFeatureSet()
    {
        using var sp = Compose(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory",
            DefaultTuiRenderer = "plain",
        });

        // Without the Spectre feature flag the renderer switch is forced plain;
        // with the flag, this test explicitly pins the plain choice.
        await Assert.That(sp.GetRequiredService<ITuiRenderer>()).IsTypeOf<PlainTuiRenderer>();
    }
}
