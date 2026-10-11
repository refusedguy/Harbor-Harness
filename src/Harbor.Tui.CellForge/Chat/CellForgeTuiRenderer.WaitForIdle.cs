namespace Harbor.Tui.CellForge;

/// <summary>
/// Headless-test idle seam (#1183, textual Pilot steal).
/// <para />
/// Textual's pilot waits for the message pump to drain
/// (<c>_wait_for_screen</c>: broadcast <c>call_later</c> decrement to all
/// widgets, wait for zero) instead of sleeping fixed delays. The CellForge
/// equivalent of "pump work outstanding" is the parked store projection:
/// every <c>UiStore</c> notification parks the newest <c>UiState</c> and the
/// frame tick drains it via <see cref="PumpProjection" /> (#466). When
/// nothing is parked, every widget already shows the newest state — the
/// driver is idle and the frame is safe to read.
/// </para>
/// </summary>
public sealed partial class CellForgeTuiRenderer
{
    /// <summary>True while a store notification is parked awaiting the frame tick.</summary>
    public bool HasPendingWork => HasPendingProjection;

    /// <summary>
    /// Drain parked projections until none remains (or the timeout elapses).
    /// Returns true when idle, false on timeout. Polls on a short quantum and
    /// exits early the moment the pump drains — a fixed
    /// <c>Task.Delay(n)</c> always pays <c>n</c>, this pays only what the pump
    /// needed.
    /// </summary>
    /// <param name="timeout">Maximum wait. Null means 5 seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> WaitForIdleAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan deadline = timeout ?? TimeSpan.FromSeconds(5);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            _ = PumpProjection();
            if (!HasPendingProjection)
                return true;
            if (sw.Elapsed >= deadline)
                return false;
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }
}
