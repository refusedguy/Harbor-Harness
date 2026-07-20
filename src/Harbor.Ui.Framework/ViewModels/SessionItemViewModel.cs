using System.Collections.Immutable;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.ViewModels;

/// <summary>
///     One row in the session sidebar list. Shows title, model, relative time,
///     status (idle/working/done/error), and git info (branch + dirty).
/// </summary>
public sealed partial class SessionItemViewModel : ObservableObject
{
    public string Id { get; }
    public string Title { get; }
    public string Agent { get; }
    public string Model { get; }
    public string ProviderId { get; }
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>
    ///     Live message count for this session. Originally populated from the
    ///     persisted <c>SessionMetadata.MessageCount</c> at refresh time, but
    ///     also updated in real time by <see cref="SessionListViewModel"/>
    ///     (subscribed to <see cref="Services.SessionManager.MessageCountChanged"/>)
    ///     so the count tracks new messages without a full RefreshAsync round-trip
    ///     (Task S2 / Problem 2: “stale message count after send”).
    /// </summary>
    [ObservableProperty]
    private int _messageCount;

    [ObservableProperty] private SessionStatus _status = SessionStatus.Idle;
    [ObservableProperty] private string? _gitBranch;
    [ObservableProperty] private bool _gitIsDirty;
    [ObservableProperty] private string _workingDirectory = "";

    public SessionItemViewModel(string id, string title, string agent, string model,
        string providerId, DateTimeOffset updatedAt, int messageCount,
        string workingDirectory = "")
    {
        Id = id;
        Title = title;
        Agent = agent;
        Model = model;
        ProviderId = providerId;
        UpdatedAt = updatedAt;
        _messageCount = messageCount;
        WorkingDirectory = workingDirectory;
    }

    /// <summary>Relative time: "now", "5m", "3h", "2d", "07/18".</summary>
    public string RelativeTime => UpdatedAt switch
    {
        var t when (DateTimeOffset.UtcNow - t).TotalMinutes < 1 => "now",
        var t when (DateTimeOffset.UtcNow - t).TotalHours < 1 => $"{(int)(DateTimeOffset.UtcNow - t).TotalMinutes}m",
        var t when (DateTimeOffset.UtcNow - t).TotalDays < 1 => $"{(int)(DateTimeOffset.UtcNow - t).TotalHours}h",
        var t when (DateTimeOffset.UtcNow - t).TotalDays < 7 => $"{(int)(DateTimeOffset.UtcNow - t).TotalDays}d",
        _ => UpdatedAt.ToString("MM/dd"),
    };

    /// <summary>Model · folder short name for the meta line.</summary>
    public string MetaLine
    {
        get
        {
            var branch = GitBranch ?? "";
            var dirty = GitIsDirty ? " *" : "";
            var folder = !string.IsNullOrEmpty(WorkingDirectory)
                ? System.IO.Path.GetFileName(WorkingDirectory)
                : "";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(branch))
                parts.Add($"{branch}{dirty}");
            if (!string.IsNullOrEmpty(folder))
                parts.Add(folder);
            parts.Add(Model);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Status display text.</summary>
    public string StatusText => Status switch
    {
        SessionStatus.Working => "working",
        SessionStatus.Done => "done",
        SessionStatus.Error => "error",
        SessionStatus.Aborted => "aborted",
        _ => "idle",
    };

    /// <summary>Status dot color key.</summary>
    /// <remarks>
    ///     <b>Task D2 / Problem 1</b>: returns the brush resource key for the
    ///     status dot. Working → amber (<c>AccentPrimaryBrush</c>), Done →
    ///     green (<c>StateSuccessBrush</c>), Error → red
    ///     (<c>StateErrorBrush</c>), Idle → grey (<c>TextTertiaryBrush</c>).
    ///     The dot's <c>Fill</c> binds to this property via
    ///     <see cref="Views.BrushKeyConverter"/> — and because <see cref="Status"/>
    ///     is an <c>[ObservableProperty]</c>, the binding only re-evaluates if
    ///     we explicitly raise <see cref="INotifyPropertyChanged.PropertyChanged"/>
    ///     for <c>StatusColor</c> (and <c>StatusText</c>) in
    ///     <see cref="OnStatusChanged"/>. Without that, the dot colour was
    ///     frozen at the initial value (Idle → grey, or Done → green) and
    ///     never reflected live agent state — the "always green" symptom.
    /// </remarks>
    public string StatusColor => Status switch
    {
        SessionStatus.Working => "AccentPrimaryBrush",
        SessionStatus.Done => "StateSuccessBrush",
        SessionStatus.Error => "StateErrorBrush",
        SessionStatus.Aborted => "StateWarningBrush",
        _ => "TextTertiaryBrush",
    };

    /// <summary>
    ///     Source-generated partial invoked by <c>[ObservableProperty]</c>
    ///     whenever <see cref="Status"/> changes. Raises
    ///     <see cref="INotifyPropertyChanged.PropertyChanged"/> for the
    ///     derived <see cref="StatusColor"/> + <see cref="StatusText"/>
    ///     properties so the status dot's <c>Fill</c> binding + the
    ///     "working/done/error/idle" label binding refresh live (Task D2 /
    ///     Problem 1: status indicator always green). Without this, the
    ///     computed getters would never re-evaluate after the first
    ///     binding — the dot would be stuck at whatever colour was
    ///     resolved when the row was first projected.
    /// </summary>
    /// <param name="value">The new SessionStatus value.</param>
    partial void OnStatusChanged(SessionStatus value)
    {
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(StatusText));
    }
}
