using Harbor.Abstractions.Models;

namespace Harbor.Tui.CellForge.Streaming;

/// <summary>
/// Context-window feed extracted from <see cref="ChatScreenBridge"/> (#172,
/// #75 follow-up): AgentStartEvent carries the resolved ModelInfo (resolved
/// up front by the agent loop, so no provider/session lookup from the TUI
/// layer) and StepFinishEvent carries the prompt tokens of the request the
/// provider just accepted; together they feed StatusViewModel.SetContext so
/// the ctx bar segment lights up. SessionStatsEvent is deliberately NOT a
/// source here — its totals are session-cumulative spend (#623).
/// </summary>
internal sealed class ContextSegmentSync
{
    private readonly StatusViewModel _status;
    private int _contextWindow;
    private long _usedTokens;
    private bool _hasUsage;

    public ContextSegmentSync(StatusViewModel status)
    {
        _status = status ?? throw new ArgumentNullException(nameof(status));
    }

    public void RememberContextWindow(ModelInfo? model)
    {
        if (model is null)
        {
            return; // legacy emitter — keep the last known window
        }

        if (model.ContextWindow <= 0)
        {
            _contextWindow = 0;
            _status.ClearContext();
            return;
        }

        _contextWindow = model.ContextWindow;
        RefreshContextSegment();
    }

    /// <summary>
    ///     <paramref name="requestTokens" /> is the size of the request just sent
    ///     (one provider call's prompt tokens), never session-cumulative spend —
    ///     feeding the running total pinned the bar at 100% by turn 4 on a 128k
    ///     window while the payload never grew. Fixes #623.
    /// </summary>
    public void NoteRequestSize(long requestTokens)
    {
        _usedTokens = requestTokens;
        _hasUsage = true;
        RefreshContextSegment();
    }

    /// <summary>
    ///     The size of the request the provider last accepted, or 0 when this
    ///     process has seen none (#651). The same figure the bar is drawn from —
    ///     which is why the token cell and the bar cannot disagree: the cell says
    ///     how full the window is, the sum says what six requests cost.
    /// </summary>
    public long LastRequestTokens => _hasUsage ? _usedTokens : 0;

    private void RefreshContextSegment()
    {
        if (_contextWindow <= 0 || !_hasUsage)
        {
            return;
        }

        _status.SetContext(_usedTokens > int.MaxValue ? int.MaxValue : (int)_usedTokens, _contextWindow);
    }
}
