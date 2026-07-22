using System.Globalization;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.ViewModels;
namespace Harbor.Ui.Framework.Converters;
/// <summary>
///     Platform-agnostic helpers that map view-model state to resource
///     keys / display strings. UI frameworks (Avalonia / WPF / MAUI /
///     Blazor) wrap these in their own <c>IValueConverter</c> /
///     <c>MarkupExtension</c> adapters — the lookups themselves don't
///     touch any UI framework types, so they're trivially reusable.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists:</b> before this class, every UI framework
///         had its own copy of <c>StatusToBrushKey</c>,
///         <c>SessionStatusToText</c>, etc. Each copy drifted (different
///         keys for the same status, missing cases, etc.). Centralising
///         the lookups here means adding a new status or label is a
///         one-line change.
///     </para>
///     <para>
///         <b>Convention:</b> every method returns a <see cref="string" />
///         that's either a resource key (suffix <c>BrushKey</c>) or a
///         human-readable label. Resource keys are resolved to actual
///         brushes by the framework's <c>BrushKeyConverter</c>-equivalent.
///     </para>
/// </remarks>
public static class StatusMappers
{
    /// <summary>
    ///     Map a chat <c>StatusText</c> string (e.g. "idle", "running",
    ///     "compacting", "error") to the resource key for the status-bar
    ///     accent brush.
    /// </summary>
    /// <param name="statusText">Raw status text from <c>UiState.Status</c>.</param>
    /// <returns>Resource key string. Defaults to <c>StatusIdleBrush</c>.</returns>
    public static string StatusToBrushKey(string? statusText) => statusText switch
    {
        "running" => "StatusRunningBrush",
        "compacting" => "StatusCompactBrush",
        "error" => "StatusErrorBrush",
        _ => "StatusIdleBrush"
    };

    /// <summary>
    ///     Map a <see cref="ViewModels.ToolCallStatus" /> to the resource
    ///     key for the tool-call pill background brush.
    /// </summary>
    public static string ToolCallStatusToBrushKey(ToolCallStatus status) => status switch
    {
        ToolCallStatus.Running => "MochaYellow",
        ToolCallStatus.Success => "MochaGreen",
        ToolCallStatus.Error => "MochaRed",
        _ => "MochaOverlay2"
    };

    /// <summary>
    ///     Map a <see cref="ViewModels.ToolCallStatus" /> to a short pill
    ///     label ("running" / "ok" / "err").
    /// </summary>
    public static string ToolCallStatusToPill(ToolCallStatus status) => status switch
    {
        ToolCallStatus.Running => "running",
        ToolCallStatus.Success => "ok",
        ToolCallStatus.Error => "err",
        _ => "?"
    };

    /// <summary>
    ///     Map a session <c>SessionStatus</c> enum (idle / working / done
    ///     / error / aborted) to a short display label.
    /// </summary>
    public static string SessionStatusToText(SessionStatus status) => status switch
    {
        SessionStatus.Working => "working",
        SessionStatus.Done => "done",
        SessionStatus.Error => "error",
        SessionStatus.Aborted => "aborted",
        _ => "idle"
    };

    /// <summary>
    ///     Map a session <c>SessionStatus</c> to the resource key for the
    ///     status-dot brush (used by the session list row).
    /// </summary>
    public static string SessionStatusToBrushKey(SessionStatus status) => status switch
    {
        SessionStatus.Working => "MochaYellow",
        SessionStatus.Done => "MochaGreen",
        SessionStatus.Error => "MochaRed",
        SessionStatus.Aborted => "MochaOverlay2",
        _ => "MochaOverlay0"
    };

    /// <summary>
    ///     Format a duration as a compact ms/s string. Returns
    ///     <see cref="string.Empty" /> for sub-millisecond values (so the
    ///     duration column hides for instantaneous tool calls).
    /// </summary>
    public static string DurationToText(TimeSpan duration) => duration.TotalMilliseconds < 1
        ? string.Empty
        : duration.TotalSeconds < 1
            ? $"{duration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}ms"
            : $"{duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s";

    /// <summary>
    ///     Format a UTC timestamp as a relative "time ago" string
    ///     ("just now" / "5m ago" / "2h ago" / "3d ago" / "Mar 5").
    ///     Returns <see cref="string.Empty" /> if <paramref name="utc" />
    ///     is null or <see cref="DateTime.MinValue" />.
    /// </summary>
    public static string TimeAgo(DateTime? utc)
    {
        if (utc is null || utc == DateTime.MinValue) return string.Empty;
        var now = DateTime.UtcNow;
        var delta = now - utc.Value;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays}d ago";
        return utc.Value.ToLocalTime().ToString("MMM d");
    }

    /// <summary>
    ///     Format a token count with K/M suffix for compact display
    ///     ("1.2K" / "12K" / "1.4M"). Returns "0" for zero/negative.
    /// </summary>
    public static string TokensToCompact(long tokens)
    {
        if (tokens <= 0) return "0";
        if (tokens < 1000) return tokens.ToString();
        if (tokens < 1_000_000) return $"{(tokens / 1000.0).ToString("F1", CultureInfo.InvariantCulture)}K";
        return $"{(tokens / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture)}M";
    }

    /// <summary>
    ///     Format a USD cost as a 4-decimal string ("$0.0123"). Returns
    ///     "$0.0000" for zero/negative.
    /// </summary>
    public static string CostToUsd(decimal costUsd) =>
        (costUsd < 0 ? 0m : costUsd).ToString("C4", CultureInfo.InvariantCulture)
        .Replace("¤", "$");
}
