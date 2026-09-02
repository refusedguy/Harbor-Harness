namespace Harbor.Hosting;

using Harbor.Abstractions.Tui;
using Harbor.Hosting.Rendering;
using Microsoft.Extensions.Logging;

/// <summary>
///     Guard + resolver for runtime renderer swap requests
///     (renderer-unification sprint Phase 6.3).
/// </summary>
/// <remarks>
///     <para>
///         Enabled when the config flag <c>runtimeSwappable</c> (the CLI's
///         <c>ui.runtime_swappable</c> toggle) is set, or when the
///         <c>HARBOR_TUI_RUNTIME_SWAP</c> env var is <c>1</c>/<c>true</c>
///         (all registered backends) / a comma-separated backend allow-list.
///         Env wins over config, matching HARBOR_TUI semantics.
///     </para>
///     <para>
///         Registered as a singleton next to <see cref="IRendererPipeline"/>:
///         the slash command resolves a request through here, so the swap
///         policy lives in exactly one place.
///     </para>
/// </remarks>
public sealed class RuntimeRendererSwapMiddleware
{
    private readonly IRendererPipeline _pipeline;
    private readonly ILogger _logger;
    private readonly HashSet<string>? _allowList;

    public RuntimeRendererSwapMiddleware(
        IRendererPipeline pipeline,
        bool configEnabled,
        ILogger<RuntimeRendererSwapMiddleware> logger)
    {
        _pipeline = pipeline;
        _logger = logger;

        string? env = Environment.GetEnvironmentVariable("HARBOR_TUI_RUNTIME_SWAP")?.Trim();
        if (!string.IsNullOrEmpty(env) && env.Equals("1", StringComparison.OrdinalIgnoreCase) || env?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
        {
            IsEnabled = true;
            _allowList = null; // all registered backends
            return;
        }

        if (!string.IsNullOrEmpty(env))
        {
            IsEnabled = true;
            _allowList = ParseList(env);
            return;
        }

        IsEnabled = configEnabled;
        _allowList = null;
    }

    /// <summary>Whether runtime swap is allowed at all.</summary>
    public bool IsEnabled { get; }

    /// <summary>
    ///     Resolves a swap request. Returns false (with an error message)
    ///     when runtime swap is disabled, the backend is unknown, or it is
    ///     not on the allow-list.
    /// </summary>
    public bool TryResolve(string requested, out string backendId, out string? error)
    {
        backendId = requested.Trim().ToLowerInvariant();
        error = null;

        if (!IsEnabled)
        {
            error = "Runtime renderer swap is disabled (set runtimeSwappable in cli.json or HARBOR_TUI_RUNTIME_SWAP=1).";
            return false;
        }

        if (_allowList is { Count: > 0 } && !_allowList.Contains(backendId))
        {
            error = $"Backend '{backendId}' is not on the HARBOR_TUI_RUNTIME_SWAP allow-list.";
            return false;
        }

        if (!_pipeline.AvailableBackends.Contains(backendId, StringComparer.OrdinalIgnoreCase))
        {
            error = $"Unknown renderer backend '{backendId}'. Available: {string.Join(", ", _pipeline.AvailableBackends)}";
            return false;
        }

        _logger.LogDebug("Runtime swap to {Backend} resolved", backendId);
        return true;
    }

    private static HashSet<string> ParseList(string csv) =>
        [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static s => s.ToLowerInvariant())];
}
