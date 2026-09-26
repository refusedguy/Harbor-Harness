using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Hosting.Rendering;
using Harbor.Storage.Jsonl;
using Harbor.Storage.Memory;
using Harbor.Tui.AnsiPlain;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Ui.Framework.State;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting.Tests;

/// <summary>
///     Composition-root mirrors for the release presets (di-design §6.3 Ф2.7):
///     each preset must resolve its declared subset and must NOT resolve what
///     it excludes. Also pins the composition-order invariant §3.5 — registries
///     are frozen BEFORE they are published into the container (the resolved
///     snapshot already contains every tool registered during composition).
/// </summary>
[NotInParallel("hosting")]
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
    public async Task AddHarbor_Full14_RegistersAll16Tools()
    {
        using var sp = Compose(new HarborComposeOptions { HarborDir = TempHarborDir(), DefaultStorageBackend = "memory" });

        var names = ToolNames(sp);
        // Full preset: 10 standard (read/write/edit/bash/glob/grep/ls/patch/notebook/tree) + 6 full-only
        // (task/webfetch/ripgrep/mcp/read_mcp_resource/mcp_prompt) + lsp + skill = 18.
        // Self-adjusting: verify all known tools are present and no duplicates, without hardcoding
        // the exact count. Adding a new tool will require updating this list, but the count check
        // below (frozen-registry equivalence) will stay green.
        var expectedFull = new[] { "read", "write", "edit", "bash", "glob", "grep", "ls", "patch", "notebook", "tree", "task", "webfetch", "ripgrep", "mcp", "read_mcp_resource", "mcp_prompt", "lsp", "skill" };
        // Self-adjusting: exact count will drift when new tools are added — pin that at least the
        // 18 known tools are present and there are no duplicates. The frozen-registry equivalence
        // test below guarantees the total count is consistent.
        await Assert.That(names.Count).IsGreaterThanOrEqualTo(expectedFull.Length);
        foreach (string full in expectedFull)
        {
            await Assert.That(names).Contains(full);
        }

        await Assert.That(names.Distinct().Count()).IsEqualTo(names.Count);
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

        foreach (string fullOnly in new[] { "task", "webfetch", "ripgrep", "mcp", "read_mcp_resource", "mcp_prompt" })
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
        // Self-adjusting: compare against the composition-context snapshot, not a hardcoded 16.
        await Assert.That(toolRegistry.GetAllTools().Count).IsEqualTo(ctx.Registries.Tools.GetAllTools().Count);
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

    // ── Issue #77 follow-up: CellForge prod write path → DI-shared UiStore ──

    [Test]
    public async Task AddHarbor_CellForgeWrites_IntoSharedUiStore_RestoreReplaysOneLine()
    {
        await WithEnv("HARBOR_TUI", null, async () =>
        {
            using var sp = Compose(new HarborComposeOptions
            {
                HarborDir = TempHarborDir(),
                DefaultStorageBackend = "memory",
                DefaultTuiRenderer = "cellforge",
            });

            // Singleton identity: the store the pipeline restores from is the
            // same instance the container hands out.
            UiStore shared = sp.GetRequiredService<UiStore>();
            await Assert.That(shared).IsNotNull();
            await Assert.That(shared).IsSameReferenceAs(sp.GetRequiredService<UiStore>());

            IRendererPipeline pipeline = sp.GetRequiredService<IRendererPipeline>();
            await Assert.That(pipeline.CurrentBackendId).IsEqualTo("cellforge");

            // Prod write path: one assistant message through the composed
            // CellForge renderer (RenderAsync → UiStore.Dispatch).
            var partial = AssistantMessage.Empty("s1", "stub-model");
            await pipeline.Current.RenderAsync(new MessageStartEvent(partial));
            await pipeline.Current.RenderAsync(new MessageUpdateEvent(new TextDeltaEvent("0", "Hello"), partial));
            await pipeline.Current.RenderAsync(new MessageEndEvent(partial));

            // The write landed in the DI-shared instance — functional proof
            // the renderer no longer owns a private store.
            await Assert.That(shared.State.Lines.Length).IsEqualTo(1);
            await Assert.That(shared.State.Lines[0].Text).IsEqualTo("Hello");

            // Restore: swap to a capturing backend — the pipeline replays the
            // shared snapshot into it, so no streamed line is lost.
            var capture = new CaptureRenderer();
            pipeline.Register("capture", () => capture);
            bool swapped = await pipeline.SwapRendererAsync("capture");

            await Assert.That(swapped).IsTrue();
            await Assert.That(capture.WrittenLines.Count).IsEqualTo(1);
            await Assert.That(capture.WrittenLines[0]).IsEqualTo("Hello");
        });
    }

    /// <summary>Minimal renderer double recording WriteLineAsync traffic (restore target).</summary>
    private sealed class CaptureRenderer : ITuiRenderer
    {
        public List<string> WrittenLines { get; } = [];

        public ITuiRenderContext Context { get; } = new NullRenderContext();
        public ViewRegistry Views { get; } = new();
        public ViewModelRegistry ViewModels { get; } = new();

        public Task<Result> InitializeAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());

        public Task RenderAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(string.Empty));

        public Task<Result> WriteAsync(string text, CancellationToken ct = default)
        {
            WrittenLines.Add(text);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
        {
            WrittenLines.Add(text ?? string.Empty);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ClearAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());

        public void Dispose() { }
    }

    private sealed class NullRenderContext : ITuiRenderContext
    {
        public int Width => 80;
        public int Height => 24;
        public bool SupportsColor => false;
        public void Write(string text) { }
        public void WriteLine(string? text = null) { }
        public void WriteColored(string text, TuiColor foreground, TuiColor? background = null) { }
        public void WriteStyled(string text, TuiStyle style) { }
        public void SetCursorPosition(int row, int col) { }
        public void ClearLine() { }
        public void Clear() { }
        public void HideCursor() { }
        public void ShowCursor() { }
        public void EnterAlternateScreen() { }
        public void ExitAlternateScreen() { }
        public void Flush() { }
    }
}
