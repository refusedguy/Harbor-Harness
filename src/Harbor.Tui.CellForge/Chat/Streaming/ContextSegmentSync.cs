using Harbor.Abstractions.Models;

namespace Harbor.Tui.CellForge.Streaming;

/// <summary>
/// Context-window feed extracted from <see cref="ChatScreenBridge"/> (#172,
/// #75 follow-up): AgentStartEvent carries the resolved ModelInfo (resolved
/// up front by the agent loop, so no provider/session lookup from the TUI
/// layer), SessionStatsEvent carries cumulative totals; together they feed
/// StatusViewModel.SetContext so the ctx bar segment lights up.
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

    public void NoteUsage(long tokensInput, long tokensOutput)
    {
        _usedTokens = tokensInput + tokensOutput;
        _hasUsage = true;
        RefreshContextSegment();
    }

    private void RefreshContextSegment()
    {
        if (_contextWindow <= 0 || !_hasUsage)
        {
            return;
        }

        _status.SetContext(_usedTokens > int.MaxValue ? int.MaxValue : (int)_usedTokens, _contextWindow);
    }
}
