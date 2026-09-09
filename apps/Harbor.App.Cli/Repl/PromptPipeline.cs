using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering.Widgets;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Submit pipeline behind the CellForge REPL (SRP extraction from the runner):
///     composer submit → slash catalog → legacy dispatcher fallback → model turn,
///     plus claude-style queued prompts, the 30 s long-turn notify timer, the
///     retry-countdown mirror, and the token/cost footer feed. Owns all of that
///     state; the frame loop only calls the small public surface below.
///     Abort/switch queue clearing (decided: queue never survives either) goes
///     through <see cref="ClearQueue"/>.
/// </summary>
internal sealed class PromptPipeline(
    IReplHost host,
    ReplCommandCatalog catalog,
    ILogger logger,
    ITokenTracker? tokens,
    Lazy<LegacySlashRunner> legacy) : IDisposable
{
    /// <summary>Stream-retry budget mirrored from AgentLoop's C7 policy —
    /// kept in sync for the countdown's «n/3» display only.</summary>
    private const int MaxStreamRetries = 3;

    private readonly Queue<string> _pendingPrompts = new();
    private volatile bool _promptInFlight;

    private int _retryAttempt;
    private long _retryErrorMs = -1;
    private int _retryTotalSec;

    private volatile string? _pendingNotifySequence;
    private Timer? _notifyTimer;

    private volatile bool _usageDirty;

    /// <summary>True while a turn runs or a queued prompt waits on the latch.</summary>
    public bool IsBusy => _promptInFlight || host.Agent.State.IsRunning;

    /// <summary>Transport hint from the OSC 99 capability probe (frame thread).</summary>
    public DesktopNotifyKind NotifyHint { get; set; }

    /// <summary>Destructive read of the staged notify sequence (frame thread).</summary>
    public string? TakeNotifySequence()
    {
        string? seq = _pendingNotifySequence;
        _pendingNotifySequence = null;
        return seq;
    }

    /// <summary>Drop queued prompts — abort and session switch both clear.</summary>
    public void ClearQueue() => _pendingPrompts.Clear();

    public async Task SubmitAsync(CancellationToken ct)
    {
        string text = host.Composer.Buffer.TakeText().Trim();
        if (text.Length == 0)
        {
            return;
        }

        // Queued prompts (claude-style): typing while busy appends to the
        // queue instead of refusing — drained in order when the loop idles.
        // Slash commands still resolve immediately (UI ops, no model turn).
        if (host.Agent.State.IsRunning || _promptInFlight)
        {
            if (!text.StartsWith('/'))
            {
                _pendingPrompts.Enqueue(text);
                host.Bridge.AppendSystemLine($"⏳ queued ({_pendingPrompts.Count}) — will send when idle");
                host.WakeUp();
                return;
            }

            host.Bridge.AppendSystemLine("⚠ Agent is busy — wait for completion or press Esc / Ctrl+C to abort.");
            host.WakeUp();
            return;
        }

        if (text.StartsWith('/'))
        {
            string[] parts = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

            // Same catalog as palette commits — one resolution point.
            // The full slash line travels in RawInput (info commands need args).
            if (catalog.TryResolve(cmd, out var slashCmd) && slashCmd is not null)
            {
                await slashCmd.ExecuteAsync(new ReplCommandContext(host, cmd, text), ct).ConfigureAwait(false);
                return;
            }

            // Uncatalogued slash text falls through to the legacy dispatcher.
            var outcome = await legacy.Value.RunAsync(
                text,
                line => { host.Bridge.AppendSystemLine(line); host.WakeUp(); },
                prompt =>
                {
                    host.Bridge.AppendSystemLine($"{prompt} — интерактивный ввод недоступен в consoleex, используйте legacy TUI (/exit)");
                    host.WakeUp();
                    return Task.FromResult(string.Empty);
                },
                host.Agent, host.SessionModel).ConfigureAwait(false);

            if (outcome.ShouldQuit)
            {
                host.RequestQuit(outcome.ExitCode);
            }

            return;
        }

        // Fresh abort token per prompt (no-op guard while a run is active).
        StartPromptRun(text, ct);
    }

    /// <summary>Begin a model turn for already-extracted text (submit + queue drain share it).</summary>
    private void StartPromptRun(string text, CancellationToken ct)
    {
        host.Agent.ResetAbortSource();
        ResetRetryCountdown();
        host.Timeline.Append(new UserBlock(text));
        host.Status.Mode = StatusBarMode.Running;
        host.WakeUp();

        _promptInFlight = true;
        ArmLongTurnNotify();
        _ = RunPromptAsync(text, ct);
    }

    /// <summary>
    /// Long-turn desktop notification (osc-sprint §777): a run still active
    /// after 30 s fires one notification through the terminal — kitty OSC 99
    /// when the probe answered, OSC 777 for the urxvt family, nothing when
    /// the terminal gave no signal (suppression is the conservative default).
    /// </summary>
    private void ArmLongTurnNotify()
    {
        _notifyTimer ??= new Timer(OnLongTurnNotifyFire, null, Timeout.Infinite, Timeout.Infinite);
        _notifyTimer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
    }

    private void DisarmLongTurnNotify() => _notifyTimer?.Change(Timeout.Infinite, Timeout.Infinite);

    private void OnLongTurnNotifyFire(object? state)
    {
        DisarmLongTurnNotify();
        if (!host.Agent.State.IsRunning)
        {
            return; // turn finished inside the window — nothing to notify about
        }

        var kind = NotifyHint is DesktopNotifyKind.Osc99 ? NotifyHint : NotifyProbe.Detect();
        NotifyHint = kind;
        if (kind is DesktopNotifyKind.None)
        {
            return;
        }

        _pendingNotifySequence = kind == DesktopNotifyKind.Osc99
            ? Osc99Notify.Encode("Harbor", "ход всё ещё выполняется (дольше 30 с)")
            : Osc777Notify.Encode("Harbor", "ход всё ещё выполняется (дольше 30 с)");
        host.WakeUp();
    }

    /// <summary>Fire-and-forget WITH full observation: every failure lands in
    /// the timeline, cancellation is expected, the wake always fires.</summary>
    private async Task RunPromptAsync(string text, CancellationToken ct)
    {
        try
        {
            var result = await host.Agent.PromptAsync(text, ct).ConfigureAwait(false);
            if (result.IsFailure)
            {
                // Abort path surfaces as a Result failure ("…cancelled.")
                // rather than OCE — normalize to the friendly abort line.
                host.Bridge.AppendSystemLine(IsCancellation(result.Error) ? "ход прерван" : "! " + result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            host.Bridge.AppendSystemLine("ход прерван");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Prompt failed in CellForge REPL");
            host.Bridge.AppendSystemLine("! " + ex.Message);
        }
        finally
        {
            DisarmLongTurnNotify();
            _promptInFlight = false;
            if (!host.Agent.State.IsRunning)
            {
                host.Status.Mode = StatusBarMode.Idle;
                ResetRetryCountdown();
            }

            _usageDirty = true;
            DrainPromptQueue(ct);
            host.WakeUp();
        }
    }

    /// <summary>Queue drain: the next queued prompt starts when the loop is
    /// truly idle (no run in flight, agent settled).</summary>
    private void DrainPromptQueue(CancellationToken ct)
    {
        if (IsBusy)
        {
            return;
        }

        if (_pendingPrompts.TryDequeue(out string? next))
        {
            host.Bridge.AppendSystemLine($"⏵ sending queued prompt ({_pendingPrompts.Count} left)");
            StartPromptRun(next, ct);
        }
    }

    /// <summary>Token/cost footer + sidebar mirror (sprint UI-V2 P6.2/P4):
    /// pulls cumulative usage from the tracker on the render thread. Cost is
    /// preserved when a richer source already reported it.
    /// Returns true when the dirty flag drained (caller repaints chrome).</summary>
    public bool RefreshUsageIfDirty()
    {
        if (!_usageDirty)
        {
            return false;
        }

        _usageDirty = false;
        if (tokens?.GetStats() is not { } stats)
        {
            return true;
        }

        host.Status.SetUsage(stats.TotalInputTokens, stats.TotalOutputTokens);
        if (host.Screen.Sidebar is { } sidebar)
        {
            sidebar.State = sidebar.State with
            {
                TokensIn = stats.TotalInputTokens,
                TokensOut = stats.TotalOutputTokens,
                MessageCount = host.Timeline.Count,
            };
        }

        return true;
    }

    /// <summary>Retry countdown feed (sprint UI-V2 P6.3): a transient provider
    /// error while the agent runs starts the UI-side backoff clock. The agent
    /// loop retries on its own policy; the status bar only mirrors the window.</summary>
    public void ObserveEvent(AgentEvent evt)
    {
        if (evt is MessageUpdateEvent { LlmEvent: ErrorEvent { Kind: var kind } }
            && ProviderErrors.IsTransient(kind)
            && host.Agent.State.IsRunning)
        {
            _retryAttempt++;
            _retryErrorMs = Environment.TickCount64;
            _retryTotalSec = RetryCountdown.BackoffSeconds(Math.Min(_retryAttempt, MaxStreamRetries));
            host.Status.Retry = RetryCountdown.Line(_retryAttempt, MaxStreamRetries, _retryTotalSec);
        }
    }

    /// <summary>Recomputes the countdown from wall clock each frame; expires
    /// silently at zero (no timer — frames already fire on the 80 ms heartbeat).</summary>
    public void UpdateRetryCountdown()
    {
        if (_retryErrorMs < 0)
        {
            return;
        }

        int remaining = _retryTotalSec - (int)((Environment.TickCount64 - _retryErrorMs) / 1000);
        if (remaining > 0)
        {
            host.Status.Retry = RetryCountdown.Line(_retryAttempt, MaxStreamRetries, remaining);
        }
        else
        {
            _retryErrorMs = -1;
            host.Status.Retry = null;
        }
    }

    /// <summary>Clears the retry window — new prompt or finished turn.</summary>
    private void ResetRetryCountdown()
    {
        _retryAttempt = 0;
        _retryErrorMs = -1;
        host.Status.Retry = null;
    }

    /// <summary>True when the failure text represents a cancelled/aborted run
    /// (AgentLoop's "…cancelled." family) — rendered as the friendly abort line.</summary>
    private static bool IsCancellation(string error) =>
        error.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("canceled", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _notifyTimer?.Dispose();
}
