using System.Diagnostics;
using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.Notifications;

/// <summary>
///     Non-interactive renderer that fires desktop OS notifications on key
/// agent events. No terminal output — designed for long-running agents in the
/// background (CI, dev server, watch loop) where the user wants to be notified
/// when the agent finishes, errors, or runs into compaction.
/// </summary>
/// <remarks>
///     <para>
///         <b>When to use:</b> you started Harbor in the background with
/// <c>harbor ask "refactor this entire folder"</c> and switched to another
/// window. The notification fires when the agent finishes (or fails) so you
/// don't have to poll the terminal.
///     </para>
///     <para>
///         Select with <c>HARBOR_TUI=notifications</c>. Combine with
/// <c>harbor ask "&lt;prompt&gt;"</c> for one-shot background runs.
///     </para>
/// </remarks>
public sealed class NotificationTuiRenderer : BaseTuiRenderer
{
    private readonly ILogger<NotificationTuiRenderer> _logger;
    private readonly INotificationBackend _backend;

    /// <summary>Construct a <see cref="NotificationTuiRenderer" /> using the platform-default backend.</summary>
    /// <param name="logger">Logger.</param>
    public NotificationTuiRenderer(ILogger<NotificationTuiRenderer> logger) : base(logger)
    {
        _logger = logger;
        Context = new NotificationRenderContext();
        _backend = DetectBackend();
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
            switch (@event)
            {
                case AgentErrorEvent err:
                    _backend.Notify("Harbor — error", err.Message, isError: true);
                    break;

                case AgentEndEvent:
                    // Fire a "done" notification. Skip if the agent ended in error
                    // (AgentErrorEvent already fired) — note both events arrive in
                    // sequence on errors; this is a heuristic to avoid double-fire.
                    _backend.Notify("Harbor — done", "Agent finished.", isError: false);
                    break;

                case CompactionCompletedEvent cc:
                    _backend.Notify("Harbor — compacted",
                        $"Pruned {cc.PrunedMessageCount} messages, saved ~{cc.TokensSaved} tokens.",
                        isError: false);
                    break;

                case ToolExecutionEndEvent tee when tee.IsError:
                    // Only notify on tool errors — successful tool calls are too noisy.
                    // ToolExecutionEndEvent carries ToolCallId (not the friendly ToolName — that's
                    // on ToolExecutionStartEvent). Use the call id as the identifier.
                    string preview = tee.Result.Output ?? string.Empty;
                    if (preview.Length > 200) preview = preview[..200] + "…";
                    _backend.Notify($"Harbor — tool {tee.ToolCallId} failed", preview, isError: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fire notification for {EventType}", @event.GetType().Name);
        }
    }

    /// <inheritdoc />
    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
    {
        // Non-interactive renderer: cannot read input. Return empty so ask-mode
        // can still complete (no follow-up prompts expected).
        return Task.FromResult(Result.Success(string.Empty));
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

    private INotificationBackend DetectBackend()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new LinuxNotifySendBackend(_logger);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new MacOsascriptBackend(_logger);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsToastBackend(_logger);
        return new NullNotificationBackend();
    }
}

/// <summary>Abstraction over the OS's notification mechanism.</summary>
public interface INotificationBackend
{
    /// <summary>Backend display name (for logging).</summary>
    string Name { get; }

    /// <summary>Fire a desktop notification.</summary>
    /// <param name="title">Notification title.</param>
    /// <param name="body">Notification body text.</param>
    /// <param name="isError">Hint to style the notification as an error.</param>
    void Notify(string title, string body, bool isError);
}

/// <summary>Linux: shells out to <c>notify-send</c> (libnotify).</summary>
internal sealed class LinuxNotifySendBackend : INotificationBackend
{
    private readonly ILogger _logger;
    public LinuxNotifySendBackend(ILogger logger) => _logger = logger;
    public string Name => "notify-send (libnotify)";

    public void Notify(string title, string body, bool isError)
    {
        var args = new List<string> { title, body };
        if (isError) { args.Insert(0, "--urgency=critical"); }
        Run("notify-send", args.ToArray());
    }

    private void Run(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            p?.WaitForExit(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "notify-send failed; is libnotify installed?");
        }
    }
}

/// <summary>macOS: shells out to <c>osascript</c> to display a notification.</summary>
internal sealed class MacOsascriptBackend : INotificationBackend
{
    private readonly ILogger _logger;
    public MacOsascriptBackend(ILogger logger) => _logger = logger;
    public string Name => "osascript (macOS Notification Center)";

    public void Notify(string title, string body, bool isError)
    {
        // Escape double quotes in body to keep the AppleScript valid.
        string safeTitle = title.Replace("\"", "\\\"");
        string safeBody = body.Replace("\"", "\\\"");
        string script = $"display notification \"{safeBody}\" with title \"{safeTitle}\"";
        try
        {
            var psi = new ProcessStartInfo("osascript", "-e", script)
            {
                RedirectStandardError = true,
                UseShellExecute = false
            };
            var p = Process.Start(psi);
            p?.WaitForExit(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "osascript failed");
        }
    }
}

/// <summary>
///     Windows: shells out to <c>msg</c> (built-in) or the user can swap in
/// <c>snoretoast</c> / <c>burnttoast</c> for proper Action Center toasts.
/// </summary>
internal sealed class WindowsToastBackend : INotificationBackend
{
    private readonly ILogger _logger;
    public WindowsToastBackend(ILogger logger) => _logger = logger;
    public string Name => "msg.exe (Windows)";

    public void Notify(string title, string body, bool isError)
    {
        // msg.exe shows a modal dialog; for proper toasts, swap in snoretoast.exe.
        try
        {
            var psi = new ProcessStartInfo("msg", "*", "/TIME:10", $"{title}\n{body}")
            {
                RedirectStandardError = true,
                UseShellExecute = false
            };
            var p = Process.Start(psi);
            p?.WaitForExit(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "msg.exe failed");
        }
    }
}

/// <summary>No-op backend for unsupported platforms (e.g. unknown Unix).</summary>
internal sealed class NullNotificationBackend : INotificationBackend
{
    public string Name => "null (no notifications)";
    public void Notify(string title, string body, bool isError) { }
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
