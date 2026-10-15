namespace Harbor.Hosting.Rendering;

using System.Collections.Concurrent;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tui;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
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

    /// <summary>Lock-free backend registry: writes are startup-only
    /// (<c>TuiModule</c> composition), reads are request-path
    /// (<c>AvailableBackends</c>, swap lookup) — same discipline as
    /// <c>LspClient._pending</c> (#1402).</summary>
    private readonly ConcurrentDictionary<string, Func<ITuiRenderer>> _factories = new(StringComparer.OrdinalIgnoreCase);
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
        // ConcurrentDictionary.Keys enumerates a moment-in-time snapshot —
        // no lock needed on this request-path read.
        get => [.. _factories.Keys];
    }

    /// <inheritdoc />
    public void Register(string backendId, Func<ITuiRenderer> factory)
    {
        _factories[backendId] = factory;
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
            RendererPipelineLog.SwapSkippedAlreadyActive(_logger, backendId);
            return true;
        }

        Func<ITuiRenderer>? factory;
        _factories.TryGetValue(backendId, out factory);

        if (factory is null)
        {
            RendererPipelineLog.SwapRefusedNotRegistered(_logger, backendId);
            return false;
        }

        // CAS gate (UiStore §PERF-007 pattern): serialize swaps without a
        // blocking lock. A concurrent swap fails fast — the caller can retry.
        if (Interlocked.CompareExchange(ref _swapGate, 1, 0) != 0)
        {
            RendererPipelineLog.SwapRefusedInFlight(_logger, backendId);
            return false;
        }

        try
        {
            var swapping = new RendererSwappingEventArgs(published.BackendId, backendId);
            RendererSwapping?.Invoke(this, swapping);
            if (swapping.Cancel)
            {
                RendererPipelineLog.SwapCancelledByHandler(_logger, backendId);
                return false;
            }

            ITuiRenderer next = factory();
            Result init = await next.InitializeAsync(ct).ConfigureAwait(false);
            if (init.IsFailure)
            {
                RendererPipelineLog.SwapAbortedInitFailed(_logger, backendId, init.Error);
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

            RendererPipelineLog.RendererSwapped(_logger, published.BackendId, backendId);
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
        if (snapshot is null || snapshot.Chat.Lines.IsEmpty)
        {
            return;
        }

        foreach (ChatLine line in snapshot.Chat.Lines)
        {
            ct.ThrowIfCancellationRequested();
            Result written = await renderer.WriteLineAsync(line.Text, ct).ConfigureAwait(false);
            if (written.IsFailure)
            {
                // A failed line write is logged, not thrown — a partial
                // restore beats aborting an already-published swap.
                RendererPipelineLog.StateRestoreLineFailed(_logger, written.Error);
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

/// <summary>
///     SG4: BCL <c>[LoggerMessage]</c> delegates for <see cref="RendererPipeline" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class RendererPipelineLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Swap to {Backend} skipped: already active")]
    public static partial void SwapSkippedAlreadyActive(ILogger logger, string backend);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Swap to {Backend} refused: backend not registered")]
    public static partial void SwapRefusedNotRegistered(ILogger logger, string backend);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Swap to {Backend} refused: another swap in flight")]
    public static partial void SwapRefusedInFlight(ILogger logger, string backend);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Swap to {Backend} cancelled by a RendererSwapping handler")]
    public static partial void SwapCancelledByHandler(ILogger logger, string backend);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Swap to {Backend} aborted: initialization failed ({Error})")]
    public static partial void SwapAbortedInitFailed(ILogger logger, string backend, string error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Renderer swapped: {From} → {To}")]
    public static partial void RendererSwapped(ILogger logger, string from, string to);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "State restore line failed: {Error}")]
    public static partial void StateRestoreLineFailed(ILogger logger, string error);
}
