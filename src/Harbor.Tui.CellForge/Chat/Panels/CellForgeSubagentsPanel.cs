using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Tui.CellForge.Panels;

// ── subagents (Right/48, Up/Down/Enter/Esc/r, read-only) ─────────────────────

/// <summary>
///     Cell-native running-subagents panel (opencode Subagents-overlay
///     equivalent): lists the active session's children (by
///     <c>ParentSessionId</c>) plus every sub-agent-kind session with
///     agent/status/age columns, and opens a read-only transcript view on
///     <c>Enter</c>.
/// </summary>
/// <remarks>
///     <para>
///         Rows come from an <see cref="ISessionStore" /> snapshot (resolved per
///         frame from <c>ctx.Services</c>), refreshed on first paint, on the
///         explicit <c>r</c> key, and whenever <see cref="UiState.Revision" />
///         moves (every <c>AgentEvent</c> the host dispatches bumps it, so the
///         list tracks live sub-agent activity; revision-triggered refreshes
///         are throttled to one store listing per 1.5s). A live
///         <see cref="ISessionManager" /> context overrides the stored status
///         with its real-time <c>StatusText</c> when the sub-session was opened
///         in this app run.
///     </para>
///     <para>
///         Read-only by construction: <see cref="OnKey" /> only moves the
///         cursor, switches between list and transcript modes, and refreshes
///         snapshots — it never dispatches <c>UiMsg.KeyInput</c> and never
///         calls any mutating store method. The transcript has no composer and
///         accepts no input at all.
///     </para>
///     <para>
///         Async loads use the completed-task fast path (fake stores in tests
///         apply synchronously) with a <c>ContinueWith</c> fallback that
///         observes its exception (§FP-006). All provider-local mutable state
///         is guarded by a small lock (same compromise as
///         <see cref="CellForgeFileTreePanel" />) so <c>Build</c> (render
///         thread) and <c>OnKey</c> (input thread) stay thread-safe.
///     </para>
/// </remarks>
public sealed class CellForgeSubagentsPanel : IPanelProvider
{
    /// <summary>Minimum interval between revision-triggered store re-listings.</summary>
    internal const long RefreshThrottleMs = 1500;

    private readonly object _gate = new();
    private List<Session> _snapshot = new();
    private bool _loaded;
    private long _lastRevision = -1;
    private long _lastRefreshTicks;
    private int _cursor;
    private bool _refreshRequested;
    private string? _loadError;

    private string? _transcriptSessionId;
    private string _transcriptTitle = string.Empty;
    private List<string> _transcriptBody = new();
    private int _transcriptWidth = -1;
    private int _transcriptScroll;
    private bool _transcriptLoading;
    private List<AgentMessage> _transcriptMessages = new();

    /// <inheritdoc />
    public string Id => OverlayIds.Subagents;

    /// <inheritdoc />
    public string Title => "Subagents";

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;

    /// <inheritdoc />
    public int DefaultSize => 48;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        lock (_gate)
        {
            if (_transcriptSessionId is not null)
                return PanelText.Clip(TranscriptRowsLocked(ctx), ctx.Width, ctx.Height);
        }

        List<SubagentRow> rows = ListRows(ctx);
        List<string> list = new(rows.Count + 4);
        lock (_gate)
        {
            _cursor = rows.Count == 0 ? 0 : Math.Clamp(_cursor, 0, rows.Count - 1);
            int cursor = _cursor;
            list.Add($"Subagents ({rows.Count})");
            list.Add(PanelText.Separator);
            if (_loadError is not null)
            {
                list.Add(_loadError);
            }
            else if (rows.Count == 0)
            {
                list.Add(_loaded ? "(no subagents)" : "loading…");
                if (_loaded)
                    list.Add("Sub-agents spawned by the task tool appear here.");
            }
            else
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    string marker = i == cursor ? "▸" : " ";
                    list.Add($"{marker} {rows[i].RowText}");
                }
            }

            list.Add(PanelText.Separator);
            list.Add("↑↓ move · Enter view · r refresh · Esc close");
        }

        return PanelText.Clip(list, ctx.Width, ctx.Height);
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        lock (_gate)
        {
            if (_transcriptSessionId is not null)
                return OnTranscriptKeyLocked(key, ctx);
        }

        switch (key.Code)
        {
            case UiKeyCode.Up:
                lock (_gate)
                {
                    _cursor = Math.Max(0, _cursor - 1);
                }

                return true;
            case UiKeyCode.Down:
                lock (_gate)
                {
                    _cursor = Math.Min(Math.Max(0, ListRowsCountLocked(ctx)), _cursor + 1);
                }

                return true;
            case UiKeyCode.Enter:
                lock (_gate)
                {
                    EnterTranscriptLocked(ctx);
                }

                return true;
            case UiKeyCode.Escape:
                HideViaStore(ctx);
                return true;
            case UiKeyCode.Char when key.Character is 'r' or 'R':
                lock (_gate)
                {
                    _refreshRequested = true;
                    KickRefreshLocked(ctx);
                }

                return true;
            default:
                return false;
        }
    }

    private List<SubagentRow> ListRows(PanelContext ctx)
    {
        var store = ctx.Services?.GetService<ISessionStore>();
        List<Session> snapshot;
        lock (_gate)
        {
            ObserveRevisionLocked(ctx, store);
            snapshot = new List<Session>(_snapshot);
        }

        var rows = new List<SubagentRow>(SubagentsModel.BuildRows(
            snapshot, ctx.State.ActiveSessionId?.Value, DateTimeOffset.UtcNow));

        // Live override: a session opened in this app run reports its
        // real-time status instead of the last persisted one.
        var manager = ctx.Services?.GetService<ISessionManager>();
        if (manager is not null)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                string? live = manager.GetContext(rows[i].SessionId)?.StatusText;
                if (live is not null)
                    rows[i] = rows[i] with { Status = live };
            }
        }

        return rows;
    }

    private int ListRowsCountLocked(PanelContext ctx)
    {
        // Row count without manager enrichment (cursor clamping only).
        var snapshot = new List<Session>(_snapshot);
        return SubagentsModel.BuildRows(snapshot, ctx.State.ActiveSessionId?.Value, DateTimeOffset.UtcNow).Count - 1;
    }

    /// <summary>Note a new store revision and kick a refresh when due. Call only under <c>_gate</c>.</summary>
    private void ObserveRevisionLocked(PanelContext ctx, ISessionStore? store)
    {
        long revision = ctx.State.Revision;
        bool moved = revision != _lastRevision;
        _lastRevision = revision;
        if (store is null)
        {
            if (!_loaded)
                _loadError = "session store unavailable";
            return;
        }
        if (!_loaded || _refreshRequested
            || (moved && Environment.TickCount64 - _lastRefreshTicks >= RefreshThrottleMs))
        {
            KickRefreshLocked(ctx, store);
        }
    }

    /// <summary>Kick a store re-listing (completed-task fast path applies inline). Call only under <c>_gate</c>.</summary>
    private void KickRefreshLocked(PanelContext ctx, ISessionStore? store = null)
    {
        store ??= ctx.Services?.GetService<ISessionStore>();
        if (store is null)
            return;
        _refreshRequested = false;
        _lastRefreshTicks = Environment.TickCount64;
        var task = store.ListAsync();
        if (task.IsCompletedSuccessfully)
        {
            ApplyListResult(task.Result);
            return;
        }

        _ = task.ContinueWith(t =>
        {
            lock (_gate)
            {
                if (t.IsFaulted || t.IsCanceled)
                {
                    _ = t.Exception; // observe (§FP-006)
                    _loadError = "store listing failed";
                }
                else
                {
                    ApplyListResult(t.Result);
                }
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Apply a finished listing to the snapshot. Call only under <c>_gate</c>.</summary>
    private void ApplyListResult(CSharpFunctionalExtensions.Result<IReadOnlyList<Session>> result)
    {
        if (result.IsFailure)
        {
            _loadError = $"store listing failed: {result.Error}";
            return;
        }

        _snapshot = new List<Session>(result.Value);
        _loaded = true;
        _loadError = null;
    }

    /// <summary>Enter transcript mode for the cursor row. Call only under <c>_gate</c> (list mode).</summary>
    private void EnterTranscriptLocked(PanelContext ctx)
    {
        var snapshot = new List<Session>(_snapshot);
        var rows = SubagentsModel.BuildRows(snapshot, ctx.State.ActiveSessionId?.Value, DateTimeOffset.UtcNow);
        if (_cursor < 0 || _cursor >= rows.Count)
            return;
        var row = rows[_cursor];
        _transcriptSessionId = row.SessionId;
        _transcriptTitle = row.Title;
        _transcriptBody = new List<string> { "loading transcript…" };
        _transcriptMessages = new List<AgentMessage>();
        _transcriptScroll = 0;
        _transcriptLoading = true;
        _transcriptWidth = -1;

        var store = ctx.Services?.GetService<ISessionStore>();
        if (store is null)
        {
            _transcriptBody = new List<string> { "(session store unavailable)" };
            _transcriptLoading = false;
            return;
        }

        string sessionId = row.SessionId;
        var task = store.GetMessagesAsync(sessionId);
        if (task.IsCompletedSuccessfully)
        {
            ApplyTranscriptResult(task.Result, ctx.Width);
            return;
        }

        _ = task.ContinueWith(t =>
        {
            lock (_gate)
            {
                if (_transcriptSessionId != sessionId)
                    return; // user navigated away meanwhile
                if (t.IsFaulted || t.IsCanceled)
                {
                    _ = t.Exception; // observe (§FP-006)
                    _transcriptBody = new List<string> { "(transcript load failed)" };
                    _transcriptLoading = false;
                }
                else
                {
                    // Width unknown on the pool thread — rows rebuild on next Build.
                    _transcriptWidth = -1;
                    ApplyTranscriptResult(t.Result, -1);
                }
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Apply a finished message history to the transcript cache. Call only under <c>_gate</c>.</summary>
    private void ApplyTranscriptResult(CSharpFunctionalExtensions.Result<IReadOnlyList<AgentMessage>> result, int width)
    {
        _transcriptLoading = false;
        if (result.IsFailure)
        {
            _transcriptBody = new List<string> { "(transcript load failed)" };
            return;
        }

        _transcriptMessages = new List<AgentMessage>(result.Value);
        if (width > 0)
        {
            _transcriptBody = BodyOf(SubagentsModel.TranscriptRows(_transcriptTitle, _transcriptMessages, width));
            _transcriptWidth = width;
        }
    }

    private static List<string> BodyOf(List<string> rows)
    {
        // TranscriptRows emits [title, separator, ...body] — the panel owns
        // the title/separator chrome and the scroll window.
        return rows.Count > 2 ? rows.GetRange(2, rows.Count - 2) : new List<string>();
    }

    /// <summary>Transcript-mode rows with a scroll window. Call only under <c>_gate</c>.</summary>
    private List<string> TranscriptRowsLocked(PanelContext ctx)
    {
        if (_transcriptLoading)
        {
            return new List<string>
            {
                $"{_transcriptTitle} (read-only)",
                PanelText.Separator,
                "loading transcript…",
            };
        }

        if (_transcriptWidth != ctx.Width && _transcriptMessages.Count > 0)
        {
            _transcriptBody = BodyOf(SubagentsModel.TranscriptRows(_transcriptTitle, _transcriptMessages, ctx.Width));
            _transcriptWidth = ctx.Width;
        }

        var body = _transcriptBody;
        int maxBody = Math.Max(1, ctx.Height - 5);
        int start = body.Count == 0 ? 0 : Math.Clamp(_transcriptScroll, 0, Math.Max(0, body.Count - maxBody));
        _transcriptScroll = start;
        int end = Math.Min(body.Count, start + maxBody);

        var rows = new List<string>(end - start + 5);
        rows.Add($"{_transcriptTitle} (read-only)");
        rows.Add(PanelText.Separator);
        if (body.Count == 0)
        {
            rows.Add("(no messages yet)");
        }
        else
        {
            if (start > 0)
                rows.Add("  ↑ more above");
            for (int i = start; i < end; i++)
                rows.Add(body[i]);
            if (end < body.Count)
                rows.Add("  ↓ more below");
        }

        rows.Add(PanelText.Separator);
        rows.Add("↑↓/j/k scroll · r reload · Esc back");
        return rows;
    }

    /// <summary>Transcript-mode keys. Call only under <c>_gate</c>. Everything is consumed; nothing is dispatched.</summary>
    private bool OnTranscriptKeyLocked(UiKey key, PanelContext ctx)
    {
        switch (key.Code)
        {
            case UiKeyCode.Escape:
                _transcriptSessionId = null;
                _transcriptBody = new List<string>();
                _transcriptMessages = new List<AgentMessage>();
                _transcriptScroll = 0;
                return true;
            case UiKeyCode.Up:
                _transcriptScroll = Math.Max(0, _transcriptScroll - 1);
                return true;
            case UiKeyCode.Down:
                _transcriptScroll = Math.Min(Math.Max(0, _transcriptBody.Count - 1), _transcriptScroll + 1);
                return true;
            case UiKeyCode.Enter:
                return true; // consumed: read-only view never submits
            case UiKeyCode.Char when key.Character is 'j' or 'J':
                _transcriptScroll = Math.Min(Math.Max(0, _transcriptBody.Count - 1), _transcriptScroll + 1);
                return true;
            case UiKeyCode.Char when key.Character is 'k' or 'K':
                _transcriptScroll = Math.Max(0, _transcriptScroll - 1);
                return true;
            case UiKeyCode.Char when key.Character is 'r' or 'R':
                ReloadTranscriptLocked(ctx);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Re-read the transcript snapshot (live appends arrive via refresh). Call only under <c>_gate</c>.</summary>
    private void ReloadTranscriptLocked(PanelContext ctx)
    {
        if (_transcriptSessionId is not { } sessionId)
            return;
        var store = ctx.Services?.GetService<ISessionStore>();
        if (store is null)
            return;
        _transcriptLoading = true;
        var task = store.GetMessagesAsync(sessionId);
        if (task.IsCompletedSuccessfully)
        {
            ApplyTranscriptResult(task.Result, ctx.Width);
            return;
        }

        _ = task.ContinueWith(t =>
        {
            lock (_gate)
            {
                if (_transcriptSessionId != sessionId)
                    return;
                if (t.IsFaulted || t.IsCanceled)
                {
                    _ = t.Exception; // observe (§FP-006)
                    _transcriptBody = new List<string> { "(transcript load failed)" };
                    _transcriptLoading = false;
                }
                else
                {
                    _transcriptWidth = -1;
                    ApplyTranscriptResult(t.Result, -1);
                }
            }
        }, TaskScheduler.Default);
    }

    private static void HideViaStore(PanelContext ctx)
    {
        if ((ctx.Store ?? ctx.Services?.GetService<UiStore>()) is UiStore store)
        {
            _ = store.Dispatch(new UiMsg.TogglePanel(OverlayIds.Subagents));
        }
    }
}
