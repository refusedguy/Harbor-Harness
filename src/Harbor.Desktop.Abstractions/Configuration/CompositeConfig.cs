// CompositeConfig.cs — pairs a CommonConfig snapshot with an app-specific
// AppConfigBase snapshot so consumers can read either layer through a single
// resolved service.
//
// Some services (e.g. AgentLoop's CompactionService, the
// ProviderRegistry's default-provider selector) need fields from BOTH layers
// at once: compaction tuning comes from CommonConfig, the TUI renderer comes
// from CliConfig. Resolving two singletons (CommonConfig + TAppConfig) works,
// but it pushes the "which layer holds this field?" decision onto the
// consumer — and that decision is the very thing the user wanted to be
// invisible ("if I set the API key in the CLI, the desktop apps see it").
//
// CompositeConfig is a thin immutable pair: a CommonConfig + a TAppConfig.
// It is a `sealed record` so it composes via `with` expressions and is
// trivially deep-equality-comparable in tests. Each app's composition root
// constructs one after both stores have been eagerly loaded.

using System.Collections.Immutable;

namespace Harbor.Desktop.Abstractions.Configuration;

/// <summary>
///     Immutable pair of the shared <see cref="CommonConfig"/> snapshot and
///     an app-specific <typeparamref name="TAppConfig"/> snapshot. Resolved
///     from DI as a singleton so services that need fields from BOTH layers
///     can take a single dependency.
/// </summary>
/// <typeparam name="TAppConfig">
///     The app-specific config record type. Must be a <c>sealed record</c>
///     deriving from <see cref="AppConfigBase"/>.
/// </typeparam>
/// <remarks>
///     <para>
///         <b>Lifetime:</b> this is a snapshot of the two underlying configs
///         at the moment the composition root built it. It does NOT auto-refresh
///         when either file changes on disk. To pick up external writes,
///         re-resolve from the relevant store via LoadAsync.
///     </para>
///     <para>
///         <b>Why not generics-free?</b> the app-specific half is generic so
///         consumers get the strongly-typed <c>TAppConfig</c> (e.g.
///         <c>CliConfig.DefaultTuiRenderer</c>) instead of having to cast from
///         <c>AppConfigBase</c>. Each app registers its own
///         <c>CompositeConfig&lt;TAppConfig&gt;</c> in DI.
///     </para>
/// </remarks>
public sealed record CompositeConfig<TAppConfig> where TAppConfig : AppConfigBase
{
    /// <summary>
    ///     Construct a composite from its two halves.
    /// </summary>
    /// <param name="common">Shared cross-app config snapshot.</param>
    /// <param name="app">App-specific config snapshot.</param>
    public CompositeConfig(CommonConfig common, TAppConfig app)
    {
        Common = common ?? throw new ArgumentNullException(nameof(common));
        App = app ?? throw new ArgumentNullException(nameof(app));
    }

    /// <summary>
    ///     The shared cross-app config (API keys, default provider/model,
    ///     storage, logging, permissions, plugins, network, compaction).
    /// </summary>
    public CommonConfig Common { get; init; }

    /// <summary>
    ///     The app-specific config (window size, fonts, TUI renderer, listen
    ///     port, …). One of: <c>CliConfig</c>, <c>AvaloniaConfig</c>,
    ///     <c>WpfConfig</c>, <c>MauiConfig</c>, <c>BlazorConfig</c>.
    /// </summary>
    public TAppConfig App { get; init; }

    /// <summary>
    ///     Convenience accessor: the <see cref="AppConfigBase.AppId"/> of the
    ///     app-specific half. Equivalent to <c>App.AppId</c> but lets
    ///     logging / telemetry code read it without caring which app type
    ///     is in the snapshot.
    /// </summary>
    public string AppId => App.AppId;

    /// <summary>
    ///     Convenience accessor: the effective storage backend, honouring an
    ///     env-var override if the caller passes one. Used by composition
    ///     roots that still support <c>HARBOR_STORAGE</c> (CLI, Avalonia).
    /// </summary>
    /// <param name="envOverride">
    ///     Optional env-var value. When non-null + non-empty, wins over
    ///     <see cref="CommonConfig.StorageBackend"/>.
    /// </param>
    /// <returns>
    ///     The effective storage backend ID (<c>"jsonl"</c>,
    ///     <c>"sqlite"</c>, or <c>"memory"</c>).
    /// </returns>
    public string EffectiveStorageBackend(string? envOverride = null)
    {
        if (!string.IsNullOrEmpty(envOverride))
        {
            return envOverride!;
        }
        return string.IsNullOrEmpty(Common.StorageBackend) ? "jsonl" : Common.StorageBackend;
    }
}
