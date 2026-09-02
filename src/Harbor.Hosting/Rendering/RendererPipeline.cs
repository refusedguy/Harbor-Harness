namespace Harbor.Hosting.Rendering;

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tui;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;

/// <summary>
///     Lock-free hot-swappable renderer runtime (renderer-unification sprint
///     Phase 6.3). Mirrors <see cref="UiStore"/>'s CAS discipline:
///     <list type="bullet">
///         <item><description>a <c>volatile</c> slot reference holds the
///             published (renderer, backend id) pair — readers see either the
///             old or the new renderer, never a torn state;</description></item>
///         <item><description>an <c>Interlocked.CompareExchange</c> gate
///             serializes swaps — concurrent swap requests fail fast instead of
///             blocking the agent loop;</description></item>
///         <item><description>the old renderer is disposed exactly once, after
///             the new one is published.</description></item>
///     </list>
///     The <see cref="UiStore"/> is only READ here (state snapshot restore);
///     the store itself is never modified by the pipeline — agent loop and
///     swap are fully decoupled.
/// </summary>
public sealed class RendererPipeline : IRendererPipeline
{
    /// <summary>Immutable published state — the CAS target.</summary>
    private sealed record Slot(ITuiRenderer Renderer, string BackendId);

    private readonly object _registrationsGate = new();
    private readonly Dictionary<string, Func<ITuiRenderer>> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly UiStore? _store;
    private readonly ILogger _logger;

    private volatile Slot? _current;
    private int _swapGate; // 0 = idle, 1 = swapping

    public RendererPipeline(
        ITuiRenderer initialRenderer,
        string initialBackendId,
        UiStore? store,
        ILogger<RendererPipeline> logger)
    {
        _current = new Slot(initialRenderer, initialBackendId);
        _store = store;
        _logger = logger;
        _factories[initialBackendId] = () => initialRenderer;
    }

    /// <inheritdoc />
    public event EventHandler<RendererSwappingEventArgs>? RendererSwapping;

    /// <inheritdoc />
    public event EventHandler<RendererSwappedEventArgs>? RendererSwapped;

    public ITuiRenderer Current => (_current ?? throw new InvalidOperationException("Pipeline not initialized.")).Renderer;

    public string CurrentBackendId => (_current ?? throw new InvalidOperationException("Pipeline not initialized.")).BackendId;

    public IReadOnlyCollection<string> AvailableBackends
    {
        get
        {
            lock (_registrationsGate)
            {
                return [.. _factories.Keys];
            }
        }
    }

    /// <inheritdoc />
    public void Register(string backendId, Func<ITuiRenderer> factory)
    {
        lock (_registrationsGate)
        {
            _factories[backendId] = factory;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SwapRendererAsync(string backendId, CancellationToken ct = default)
    {
        Slot? published = _current;
        if (published is null)
        {
            return false;
        }

        if (string.Equals(published.BackendId, backendId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Swap to {Backend} skipped: already active", backendId);
            return true;
        }

        Func<ITuiRenderer>? factory;
        lock (_registrationsGate)
        {
            _factories.TryGetValue(backendId, out factory);
        }

        if (factory is null)
        {
            _logger.LogWarning("Swap to {Backend} refused: backend not registered", backendId);
            return false;
        }

        // CAS gate (UiStore §PERF-007 pattern): serialize swaps without a
        // blocking lock. A concurrent swap fails fast — the caller can retry.
        if (Interlocked.CompareExchange(ref _swapGate, 1, 0) != 0)
        {
            _logger.LogWarning("Swap to {Backend} refused: another swap in flight", backendId);
            return false;
        }

        try
        {
            var swapping = new RendererSwappingEventArgs(published.BackendId, backendId);
            RendererSwapping?.Invoke(this, swapping);
            if (swapping.Cancel)
            {
                _logger.LogInformation("Swap to {Backend} cancelled by a RendererSwapping handler", backendId);
                return false;
            }

            ITuiRenderer next = factory();
            Result init = await next.InitializeAsync(ct).ConfigureAwait(false);
            if (init.IsFailure)
            {
                _logger.LogError("Swap to {Backend} aborted: initialization failed ({Error})", backendId, init.Error);
                next.Dispose();
                return false;
            }

            await RestoreStateIntoAsync(next, ct).ConfigureAwait(false);

            // Atomic publication: readers (the agent-facing RenderAsync path)
            // switch to the new renderer in one reference assignment.
            _current = new Slot(next, backendId);

            var swapped = new RendererSwappedEventArgs(published.BackendId, backendId);
            RendererSwapped?.Invoke(this, swapped);

            // Dispose the old renderer exactly once, after publication.
            published.Renderer.Dispose();

            _logger.LogInformation("Renderer swapped: {From} → {To}", published.BackendId, backendId);
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _swapGate, 0);
        }
    }

    /// <summary>
    ///     Restores the current <see cref="UiStore.State"/> chat history into
    ///     <paramref name="renderer"/> — the swap-invariant that no streamed
    ///     token is lost across a swap. The store is only read, never written.
    /// </summary>
    private async Task RestoreStateIntoAsync(ITuiRenderer renderer, CancellationToken ct)
    {
        UiState? snapshot = _store?.State;
        if (snapshot is null || snapshot.Lines.IsEmpty)
        {
            return;
        }

        foreach (ChatLine line in snapshot.Lines)
        {
            ct.ThrowIfCancellationRequested();
            Result written = await renderer.WriteLineAsync(line.Text, ct).ConfigureAwait(false);
            if (written.IsFailure)
            {
                // A failed line write is logged, not thrown — a partial
                // restore beats aborting an already-published swap.
                _logger.LogWarning("State restore line failed: {Error}", written.Error);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The pipeline itself holds only the published slot; disposing it
        // tears down the active renderer (the swap-gate is not a resource).
        Slot? published = _current;
        published?.Renderer.Dispose();
        _current = null;
    }
}
