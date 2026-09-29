using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Notifications;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.Views;
using Microsoft.Extensions.Logging;
namespace Harbor.Tui.Notifications;
/// <summary>
///     Non-interactive renderer that fires desktop OS notifications on key
///     agent events. No terminal output — designed for long-running agents in the
///     background (CI, dev server, watch loop) where the user wants to be notified
///     when the agent finishes, errors, or runs into compaction.
/// </summary>
/// <remarks>
///     <para>
///         <b>When to use:</b> you started Harbor in the background with
///         <c>harbor ask "refactor this entire folder"</c> and switched to another
///         window. The notification fires when the agent finishes (or fails) so you
///         don't have to poll the terminal.
///     </para>
///     <para>
///         Select with <c>HARBOR_TUI=notifications</c>. Combine with
///         <c>harbor ask "&lt;prompt&gt;"</c> for one-shot background runs.
///     </para>
/// </remarks>
public sealed class NotificationTuiRenderer : BaseTuiRenderer
{
    private readonly INotificationBackend _backend;
    private readonly ILogger<NotificationTuiRenderer> _logger;

    /// <summary>Construct a <see cref="NotificationTuiRenderer" /> using the platform-default backend.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="processRunner">
    ///     Who starts the OS notifier. Injected, and deliberately not defaulted:
    ///     a Presentation assembly that owns its own <c>Process</c> is the #665
    ///     defect, and a default would quietly put it back. The production
    ///     implementation is <c>Harbor.Application.Notifications.ProcessNotificationRunner</c>.
    /// </param>
    public NotificationTuiRenderer(
        ILogger<NotificationTuiRenderer> logger,
        INotificationProcessRunner processRunner) : base(logger)
    {
        _logger = logger;
        Context = new NotificationRenderContext();
        _backend = DetectBackend(processRunner);
        RegisterHandler(new AgentErrorNotificationHandler(_backend));
        RegisterHandler(new AgentEndNotificationHandler(_backend));
        RegisterHandler(new CompactionNotificationHandler(_backend));
        RegisterHandler(new ToolErrorNotificationHandler(_backend));
    }

    /// <inheritdoc />
    public override ITuiRenderContext Context { get; }

    /// <inheritdoc />
    public override Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("NotificationTuiRenderer using backend: {Backend}", _backend.Name);
        return base.InitializeAsync(ct);
    }

    /// <inheritdoc />
    public override async Task RenderAsync(AgentEvent @event, CancellationToken ct = default)
    {
        await base.RenderAsync(@event, ct).ConfigureAwait(false);

        try
        {
            // Desktop alerts are fired by the registered IAgentEventHandlers
            // (issue #185 visitor registry) — no per-renderer switch here.
            await DispatchToHandlersAsync(@event, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fire notification for {EventType}", @event.GetType().Name);
        }
    }

    /// <summary>Error notification on unrecoverable agent failures.</summary>
    private sealed class AgentErrorNotificationHandler(INotificationBackend backend) : IAgentEventHandler
    {
        public bool CanHandle(AgentEvent @event) => @event is AgentErrorEvent;

        public Task HandleAsync(AgentEvent @event, ITuiRenderContext context, CancellationToken ct = default)
        {
            var err = (AgentErrorEvent)@event;
            backend.Notify("Harbor — error", err.Message, true, ct);
            return Task.CompletedTask;
        }
    }

    /// <summary>Done notification when the run completes.</summary>
    /// <remarks>
    ///     Skips nothing by itself: on errors both AgentErrorEvent and
    ///     AgentEndEvent arrive in sequence, so an error run may double-fire —
    ///     the same heuristic trade-off as before the visitor refactor.
    /// </remarks>
    private sealed class AgentEndNotificationHandler(INotificationBackend backend) : IAgentEventHandler
    {
        public bool CanHandle(AgentEvent @event) => @event is AgentEndEvent;

        public Task HandleAsync(AgentEvent @event, ITuiRenderContext context, CancellationToken ct = default)
        {
            backend.Notify("Harbor — done", "Agent finished.", false, ct);
            return Task.CompletedTask;
        }
    }

    /// <summary>Notification when compaction reclaims context.</summary>
    private sealed class CompactionNotificationHandler(INotificationBackend backend) : IAgentEventHandler
    {
        public bool CanHandle(AgentEvent @event) => @event is CompactionCompletedEvent;

        public Task HandleAsync(AgentEvent @event, ITuiRenderContext context, CancellationToken ct = default)
        {
            var cc = (CompactionCompletedEvent)@event;
            backend.Notify("Harbor — compacted",
                $"Pruned {cc.PrunedMessageCount} messages, saved ~{cc.TokensSaved} tokens.",
                false, ct);
            return Task.CompletedTask;
        }
    }

    /// <summary>Error notification on failed tool calls (successes are too noisy).</summary>
    /// <remarks>
    ///     ToolExecutionEndEvent carries ToolCallId (not the friendly ToolName —
    ///     that's on ToolExecutionStartEvent). The call id is the identifier.
    /// </remarks>
    private sealed class ToolErrorNotificationHandler(INotificationBackend backend) : IAgentEventHandler
    {
        public bool CanHandle(AgentEvent @event) =>
            @event is ToolExecutionEndEvent { IsError: true };

        public Task HandleAsync(AgentEvent @event, ITuiRenderContext context, CancellationToken ct = default)
        {
            var tee = (ToolExecutionEndEvent)@event;
            string preview = tee.Result.Output ?? string.Empty;
            if (preview.Length > 200) preview = preview[..200] + "…";
            backend.Notify($"Harbor — tool {tee.ToolCallId} failed", preview, true, ct);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public override Task<Maybe<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        // Non-interactive renderer: cannot read input, so no line was read.
        // Maybe.None, not Success("") — ask-mode completes the same way, and a
        // line-buffered consumer now sees "no input" instead of spinning (#589).
        return Task.FromResult(Maybe<string>.None);
    }

    /// <inheritdoc />
    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    /// <inheritdoc />
    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    /// <inheritdoc />
    public override Task<Result> ClearAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    /// <inheritdoc />
    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event)
        => false;

    /// <summary>
    ///     Picks the notifier for the current OS. The choice is the only thing
    ///     left that varies per platform: what actually gets started is the
    ///     injected <paramref name="processRunner" />, in every branch (#665).
    /// </summary>
    private static INotificationBackend DetectBackend(INotificationProcessRunner processRunner)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new LinuxNotifySendBackend(processRunner);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new MacOsascriptBackend(processRunner);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsToastBackend(processRunner);
        return new NullNotificationBackend();
    }
}

/// <summary>Abstraction over the OS's notification mechanism.</summary>
/// <remarks>
///     This interface abstracts the SHAPE of a notification — title, body,
///     severity — and, since #665, its EXECUTION goes through
///     <see cref="INotificationProcessRunner" /> instead of a per-backend
///     <c>Process</c>. An interface over shape with no seam underneath it is
///     what let three Presentation types fork a process each; the seam is what
///     makes these backends injectable, cancellable and testable.
/// </remarks>
public interface INotificationBackend
{
    /// <summary>Backend display name (for logging).</summary>
    public string Name { get; }

    /// <summary>Fire a desktop notification.</summary>
    /// <remarks>
    ///     Best effort. A missing notifier, a timeout or a non-zero exit is
    ///     logged by the runner and does not throw: a toast announces a finished
    ///     run, and failing the run because the toast did not appear is never
    ///     the right trade.
    /// </remarks>
    /// <param name="title">Notification title.</param>
    /// <param name="body">Notification body text.</param>
    /// <param name="isError">Hint to style the notification as an error.</param>
    /// <param name="cancellationToken">Cancels the launch.</param>
    public void Notify(string title, string body, bool isError, CancellationToken cancellationToken = default);
}

/// <summary>Linux: <c>notify-send</c> (libnotify), via the injected runner.</summary>
internal sealed class LinuxNotifySendBackend(INotificationProcessRunner runner) : INotificationBackend
{
    public string Name => "notify-send (libnotify)";

    public void Notify(string title, string body, bool isError, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { title, body };
        if (isError) { args.Insert(0, "--urgency=critical"); }
        runner.Run("notify-send", args, cancellationToken);
    }
}

/// <summary>macOS: <c>osascript</c> against Notification Center, via the injected runner.</summary>
internal sealed class MacOsascriptBackend(INotificationProcessRunner runner) : INotificationBackend
{
    public string Name => "osascript (macOS Notification Center)";

    public void Notify(string title, string body, bool isError, CancellationToken cancellationToken = default)
    {
        // Escape double quotes in body to keep the AppleScript valid.
        string safeTitle = title.Replace("\"", "\\\"");
        string safeBody = body.Replace("\"", "\\\"");
        string script = $"display notification \"{safeBody}\" with title \"{safeTitle}\"";
        runner.Run("osascript", ["-e", script], cancellationToken);
    }
}

/// <summary>
///     Windows: <c>msg</c> (built-in) or the user can swap in
///     <c>snoretoast</c> / <c>burnttoast</c> for proper Action Center toasts.
/// </summary>
internal sealed class WindowsToastBackend(INotificationProcessRunner runner) : INotificationBackend
{
    public string Name => "msg.exe (Windows)";

    public void Notify(string title, string body, bool isError, CancellationToken cancellationToken = default)
    {
        // msg.exe shows a modal dialog; for proper toasts, swap in snoretoast.exe.
        runner.Run("msg", ["*", "/TIME:10", $"{title}\n{body}"], cancellationToken);
    }
}

/// <summary>No-op backend for unsupported platforms (e.g. unknown Unix).</summary>
internal sealed class NullNotificationBackend : INotificationBackend
{
    public string Name => "null (no notifications)";
    public void Notify(string title, string body, bool isError, CancellationToken cancellationToken = default) { }
}

/// <summary>Render context shim — the notification renderer doesn't paint.</summary>
internal sealed class NotificationRenderContext : ITuiRenderContext
{
    /// <inheritdoc />
    public int Width => 80;
    /// <inheritdoc />
    public int Height => 24;
    /// <inheritdoc />
    public bool SupportsColor => false;

    /// <inheritdoc />
    public void Write(string text) { }
    /// <inheritdoc />
    public void WriteLine(string? text = null) { }
    /// <inheritdoc />
    public void WriteColored(string text, TuiColor foreground, TuiColor? background = null) { }
    /// <inheritdoc />
    public void WriteStyled(string text, TuiStyle style) { }
    /// <inheritdoc />
    public void SetCursorPosition(int row, int col) { }
    /// <inheritdoc />
    public void ClearLine() { }
    /// <inheritdoc />
    public void Clear() { }
    /// <inheritdoc />
    public void HideCursor() { }
    /// <inheritdoc />
    public void ShowCursor() { }
    /// <inheritdoc />
    public void EnterAlternateScreen() { }
    /// <inheritdoc />
    public void ExitAlternateScreen() { }
    /// <inheritdoc />
    public void Flush() { }
}
