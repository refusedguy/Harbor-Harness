using Harbor.Core.Configuration;
using Harbor.Core.Events;
using Harbor.Desktop.Abstractions.Configuration;

using Harbor.Abstractions.Events;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting;

/// <summary>Builtin tool catalog size (§3.3).</summary>
public enum HarborToolSetKind
{
    /// <summary>Read/Write/Edit/Bash/Glob/Grep/Ls/Patch/Notebook/Tree — desktop-safe subset.</summary>
    Standard10,

    /// <summary>All 14 builtins (+ Task, WebFetch, RipGrep, McpTool).</summary>
    Full14,
}

/// <summary>Where the agent registry takes its default provider/model from.</summary>
public enum HarborAgentModelSource
{
    /// <summary>config.json → HarborConfig.EffectiveModel (CLI).</summary>
    HarborConfig,

    /// <summary>HARBOR_MODEL env or CommonConfig.DefaultProvider/DefaultModel (desktop).</summary>
    CommonConfig,
}

/// <summary>Provider registry construction flavor.</summary>
public enum HarborProviderFlavor
{
    /// <summary>Ollama via IHttpClientFactory + native Anthropic/OpenAI + AuthStore-backed json discovery.</summary>
    CliFull,

    /// <summary>Ollama direct (OLLAMA_HOST) + json discovery over injected resolver/catalog.</summary>
    Desktop,
}

/// <summary>
///     App-provided composition specifics. Presets (CliFull / Desktop) are plain
///     data — release variants are options, not <c>#if</c> scatter (§3.3).
/// </summary>
public sealed class HarborComposeOptions
{
    /// <summary>~/.harbor equivalent; defaults to the real user home.</summary>
    public string HarborDir { get; init; } = DefaultHarborDir();

    /// <summary>Default storage backend when neither config nor HARBOR_STORAGE set. CLI: jsonl, desktop: memory.</summary>
    public string DefaultStorageBackend { get; init; } = "jsonl";

    /// <summary>
    ///     Default TUI renderer name from app config (CLI reads cli.json).
    ///     <c>null</c>/empty/"auto" means the Hosting default ("spectre-tui" under
    ///     the Spectre feature flag, "plain" otherwise).
    /// </summary>
    public string? DefaultTuiRenderer { get; init; }

    /// <summary>
    ///     Extra event-bus middlewares (CLI adds TypeFilterMiddleware, Avalonia
    ///     passes none). Factory receives the bootstrap logger factory.
    /// </summary>
    public Func<ILoggerFactory, IReadOnlyList<IEventBusMiddleware>>? EventBusMiddlewares { get; init; }

    /// <summary>Event bus scrollback capacity (CLI: 1000, Avalonia: library default).</summary>
    public int? EventBusScrollback { get; init; }

    /// <summary>Override for config.json location; defaults to &lt;HarborDir&gt;/config.json.</summary>
    public string? ConfigPath { get; init; }

    /// <summary>
    ///     Host configuration handed to plugins (CLI passes
    ///     builder.Configuration). Null → empty configuration.
    /// </summary>
    public Microsoft.Extensions.Configuration.IConfiguration? Configuration { get; init; }

    /// <summary>
    ///     App-provided bootstrap logger factory (already wired to the app's
    ///     logging providers). When null, Hosting creates a quiet internal one.
    /// </summary>
    public Func<ILoggerFactory>? BootstrapLoggerFactory { get; init; }

    /// <summary>Compile-time feature set (single mapping point: HarborBuildFeatures).</summary>
    public HarborFeatureSet Features { get; init; } = HarborBuildFeatures.Detect;

    /// <summary>Builtin tool catalog size (default: Full14).</summary>
    public HarborToolSetKind ToolSet { get; init; } = HarborToolSetKind.Full14;

    /// <summary>Load mcp.json overlays and register McpToolTool (default: true).</summary>
    public bool IncludeMcpTools { get; init; } = true;

    /// <summary>Where the agent registry reads its default model (default: HarborConfig).</summary>
    public HarborAgentModelSource ModelSource { get; init; } = HarborAgentModelSource.HarborConfig;

    /// <summary>Provider registry construction flavor (default: CliFull).</summary>
    public HarborProviderFlavor Providers { get; init; } = HarborProviderFlavor.CliFull;

    /// <summary>Desktop flavor only: auth resolver handed to OpenAI-compatible clients.</summary>
    public Harbor.Abstractions.Providers.IAuthResolver? DesktopAuthResolver { get; init; }

    /// <summary>Desktop flavor only: model catalog handed to OpenAI-compatible clients.</summary>
    public Harbor.Providers.OpenAiCompatible.IModelCatalog? DesktopModelCatalog { get; init; }

    /// <summary>
    ///     Register the Hosting-owned ICommonConfigStore + CommonConfig singleton.
    ///     Desktop apps bring their own async-loaded store — set false there.
    /// </summary>
    public bool RegisterCommonConfigStore { get; init; } = true;

    /// <summary>Hook invoked right after the configuration module ran (e.g. override ctx.Common).</summary>
    public Action<HarborCompositionContext>? AfterConfiguration { get; init; }

    /// <summary>Full CLI preset: jsonl storage by default, env overrides apply at runtime.</summary>
    public static HarborComposeOptions CliDefault() => new()
    {
        DefaultStorageBackend = "jsonl",
        EventBusScrollback = 1000,
    };

    /// <summary>Desktop preset: memory storage by default, no middlewares, no scrollback override.</summary>
    public static HarborComposeOptions DesktopDefault() => new()
    {
        DefaultStorageBackend = "memory",
    };

    private static string DefaultHarborDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbor");
}
