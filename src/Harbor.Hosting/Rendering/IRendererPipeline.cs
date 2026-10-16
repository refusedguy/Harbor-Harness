namespace Harbor.Hosting.Rendering;

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tui;

/// <summary>
///     Contract of the lock-free hot-swappable renderer runtime
///     (renderer-unification sprint Phase 6.3): swap the active renderer
///     backend mid-session without losing streaming state and without the
///     agent loop ever knowing.
/// </summary>
/// <remarks>
///     The agent loop keeps publishing into the same sinks; the pipeline
///     atomically republishes the current renderer, restores the
///     <c>UiState</c> snapshot into the new backend, and disposes the old one
///     exactly once — using the same CAS discipline as <c>UiStore</c>
///     (§PERF-007 pattern), never a lock around agent-facing paths.
/// </remarks>
public interface IRendererPipeline : IDisposable
{
    /// <inheritdoc cref="IDisposable.Dispose" />
    /// <summary>The currently published renderer (never null after construction).</summary>
    /// <remarks>
    ///     Compat accessor: throws <see cref="InvalidOperationException"/> when the
    ///     pipeline holds no renderer (disposed). New code prefers
    ///     <see cref="TryGetCurrent"/> — absence is <c>Maybe</c>, not an exception (#1509).
    /// </remarks>
    ITuiRenderer Current { get; }

    /// <summary>Backend id of the currently published renderer.</summary>
    /// <remarks>
    ///     Compat accessor (see <see cref="Current"/>). New code prefers
    ///     <see cref="TryGetBackendId"/> (#1509).
    /// </remarks>
    string CurrentBackendId { get; }

    /// <summary>
    ///     The currently published renderer, or <c>None</c> when the pipeline
    ///     holds none (disposed). Canonical absence API (#1509): <c>Maybe</c> for
    ///     absence, <c>Result</c> (via <c>ToResult</c>) when an error text is needed.
    /// </summary>
    Maybe<ITuiRenderer> TryGetCurrent();

    /// <summary>Backend id of the currently published renderer, or <c>None</c> when absent (#1509).</summary>
    Maybe<string> TryGetBackendId();

    /// <summary>Backend ids registered for runtime swap.</summary>
    IReadOnlyCollection<string> AvailableBackends { get; }

    /// <summary>
    ///     Raised before the swap is applied. Handlers may cancel (e.g. a
    ///     renderer that cannot safely flush right now).
    /// </summary>
    event EventHandler<RendererSwappingEventArgs>? RendererSwapping;

    /// <summary>Raised after the new renderer is published and the old one disposed.</summary>
    event EventHandler<RendererSwappedEventArgs>? RendererSwapped;

    /// <summary>Registers a swap target. The initial renderer's id is pre-registered by the pipeline.</summary>
    void Register(string backendId, Func<ITuiRenderer> factory);

    /// <summary>
    ///     Atomically replaces the active renderer. Returns false when the
    ///     backend is unknown, the swap was cancelled by a handler, or
    ///     another swap is in flight.
    /// </summary>
    Task<bool> SwapRendererAsync(string backendId, CancellationToken ct = default);
}

/// <summary>Cancelable pre-swap event payload.</summary>
public sealed class RendererSwappingEventArgs(string fromBackendId, string toBackendId) : EventArgs
{
    public string FromBackendId { get; } = fromBackendId;
    public string ToBackendId { get; } = toBackendId;

    /// <summary>Set by handlers to abort the swap (current renderer stays active).</summary>
    public bool Cancel { get; set; }
}

/// <summary>Post-swap event payload.</summary>
public sealed class RendererSwappedEventArgs(string fromBackendId, string toBackendId) : EventArgs
{
    public string FromBackendId { get; } = fromBackendId;
    public string ToBackendId { get; } = toBackendId;
}
