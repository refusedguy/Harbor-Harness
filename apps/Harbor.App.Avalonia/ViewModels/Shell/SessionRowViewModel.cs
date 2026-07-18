using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Harbor.App.Avalonia.ViewModels.Shell;

/// <summary>
///     Dense projection of one session for the Orca left rail.
/// </summary>
/// <remarks>
///     <para>
///         Wraps the underlying <see cref="SessionItemViewModel"/> fields and
///         adds the derived display strings the Orca rail template needs:
///         <see cref="RelativeTime"/>, <see cref="MetaLine"/>,
///         <see cref="StatusLine"/>, plus the resource-key strings
///         (<see cref="StatusBrush"/>, <see cref="RowBackground"/>,
///         <see cref="RowBorder"/>) that the XAML resolves to brushes via
///         <see cref="global::Harbor.App.Avalonia.Views.BrushKeyConverter"/>.
///     </para>
///     <para>
///         Returning resource-key strings (rather than <c>IBrush</c> directly)
///         keeps the VM testable without an Avalonia <c>Application</c>
///         instance, and matches the pattern used by the classic
///         <see cref="MainViewModel.StatusBrushKey"/>.
///     </para>
/// </remarks>
public sealed partial class SessionRowViewModel : ObservableObject
{
    /// <summary>Construct a dense session row projection.</summary>
    public SessionRowViewModel(
        string id,
        string title,
        string agent,
        string modelName,
        string providerId,
        DateTimeOffset updatedAt,
        int messageCount,
        string status = "idle",
        string? workdir = null,
        string? mode = null,
        decimal? costTotal = null)
    {
        Id = id;
        Title = title;
        Agent = agent;
        ModelName = modelName;
        ProviderId = providerId;
        UpdatedAt = updatedAt;
        MessageCount = messageCount;
        Status = status;
        Workdir = workdir ?? string.Empty;
        Mode = mode ?? "Chat";
        CostTotal = costTotal;
    }

    /// <summary>Stable session identifier (from <see cref="SessionItemViewModel.Id"/>).</summary>
    public string Id { get; init; }

    /// <summary>Session title (first user prompt or "New session").</summary>
    public string Title { get; init; }

    /// <summary>Agent name (code/plan/explore).</summary>
    public string Agent { get; init; }

    /// <summary>Model display name (e.g. <c>qwen2.5-coder:7b</c>).</summary>
    public string ModelName { get; init; }

    /// <summary>Provider id (e.g. <c>ollama</c>).</summary>
    public string ProviderId { get; init; }

    /// <summary>Last-updated timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Total messages in the session.</summary>
    public int MessageCount { get; init; }

    /// <summary>
    ///     Session status: <c>idle</c> | <c>running</c> | <c>error</c> |
    ///     <c>completed</c>. Drives the status dot color + status-line text.
    /// </summary>
    public string Status { get; init; }

    /// <summary>Optional workdir short label (basename of the session root).</summary>
    public string Workdir { get; init; }

    /// <summary>Active mode label: <c>Chat</c> | <c>Code</c>.</summary>
    public string Mode { get; init; }

    /// <summary>Accumulated session cost in USD, if known.</summary>
    public decimal? CostTotal { get; init; }

    /// <summary>
    ///     Relative time-ago label (e.g. <c>now</c>, <c>5m</c>, <c>3h</c>,
    ///     <c>2d</c>, <c>MM/dd</c>).
    /// </summary>
    public string RelativeTime
    {
        get
        {
            var delta = DateTimeOffset.UtcNow - UpdatedAt;
            if (delta.TotalMinutes < 1) return "now";
            if (delta.TotalHours < 1) return ((int)delta.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
            if (delta.TotalDays < 1) return ((int)delta.TotalHours).ToString(CultureInfo.InvariantCulture) + "h";
            if (delta.TotalDays < 7) return ((int)delta.TotalDays).ToString(CultureInfo.InvariantCulture) + "d";
            return UpdatedAt.ToString("MM/dd", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Meta line under the title: <c>model · mode</c> (or model · workdir).</summary>
    public string MetaLine
    {
        get
        {
            string second = !string.IsNullOrEmpty(Mode) ? Mode
                : (!string.IsNullOrEmpty(Workdir) ? Workdir : string.Empty);
            return string.IsNullOrEmpty(second)
                ? (ModelName ?? string.Empty)
                : $"{ModelName} · {second}";
        }
    }

    /// <summary>Status text shown beneath the meta line (only when non-empty).</summary>
    public string StatusLine => Status switch
    {
        "running" => string.Empty,
        "error" => "error",
        "completed" => string.Empty,
        _ => string.Empty,
    };

    /// <summary>True when <see cref="StatusLine"/> has visible content.</summary>
    public bool HasStatusLine => !string.IsNullOrEmpty(StatusLine);

    /// <summary>
    ///     Brush resource key for the status dot. Resolved by
    ///     <see cref="global::Harbor.App.Avalonia.Views.BrushKeyConverter"/>.
    /// </summary>
    public string StatusBrushKey => Status switch
    {
        "running" => "StateRunningBrush",
        "error" => "StateErrorBrush",
        "completed" => "StateSuccessBrush",
        _ => "StateIdleBrush",
    };

    /// <summary>
    ///     Brush resource key for the row background. Active row → amber-tinted
    ///     <c>BgActiveBrush</c>; otherwise transparent (the ListBox provides
    ///     hover via its own chrome).
    /// </summary>
    public string RowBackgroundKey => IsActive ? "BgActiveBrush" : "TransparentBrush";

    /// <summary>
    ///     Brush resource key for the row's left accent border. Active row →
    ///     <c>AccentPrimaryBrush</c>; otherwise transparent.
    /// </summary>
    public string RowBorderKey => IsActive ? "AccentPrimaryBrush" : "TransparentBrush";

    /// <summary>True when this row is the currently selected session.</summary>
    [ObservableProperty]
    private bool _isActive;
}
