using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Converters;

namespace Harbor.Ui.Framework.ViewModels;
/// <summary>
///     One row in the session sidebar list. Shows title, model, relative time,
///     status (idle/working/done/error), and git info (branch + dirty).
/// </summary>
public sealed partial class SessionItemViewModel : ObservableObject
{
    [ObservableProperty] private string? _gitBranch;
    [ObservableProperty] private bool _gitIsDirty;
    [ObservableProperty] private int _gitDirtyCount;
    [ObservableProperty] private string? _gitLastCommit;

    /// <summary>
    ///     Live message count for this session. Originally populated from the
    ///     persisted <c>SessionMetadata.MessageCount</c> at refresh time, but
    ///     also updated in real time by <see cref="SessionListViewModel" />
    ///     (subscribed to <see cref="Services.SessionManager.MessageCountChanged" />)
    ///     so the count tracks new messages without a full RefreshAsync round-trip
    ///     (Task S2 / Problem 2: “stale message count after send”).
    /// </summary>
    [ObservableProperty]
    private int _messageCount;

    [ObservableProperty] private SessionStatus _status = SessionStatus.Idle;
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
    public string Id { get; }
    public string Title { get; }
    public string Agent { get; }
    public string Model { get; }
    public string ProviderId { get; }
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>Relative time: "now", "5m", "3h", "2d", "07/18".</summary>
    public string RelativeTime => UpdatedAt switch
    {
        var t when (DateTimeOffset.UtcNow - t).TotalMinutes < 1 => "now",
        var t when (DateTimeOffset.UtcNow - t).TotalHours < 1 => $"{(int)(DateTimeOffset.UtcNow - t).TotalMinutes}m",
        var t when (DateTimeOffset.UtcNow - t).TotalDays < 1 => $"{(int)(DateTimeOffset.UtcNow - t).TotalHours}h",
        var t when (DateTimeOffset.UtcNow - t).TotalDays < 7 => $"{(int)(DateTimeOffset.UtcNow - t).TotalDays}d",
        _ => UpdatedAt.ToString("MM/dd")
    };

    /// <summary>Model · folder short name for the meta line.</summary>
    public string MetaLine
    {
        get
        {
            string branch = GitBranch ?? "";
            string dirty = GitIsDirty ? $" +{GitDirtyCount}" : "";
            string folder = !string.IsNullOrEmpty(WorkingDirectory)
                ? Path.GetFileName(WorkingDirectory)
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

    public string GitInfoLine
    {
        get
        {
            if (!GitIsDirty) return string.Empty;
            var parts = new List<string>();
            parts.Add($"+{GitDirtyCount} files");
            if (!string.IsNullOrEmpty(GitLastCommit))
                parts.Add(GitLastCommit);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    ///     Status display text ("working" / "done" / "error" / "aborted" /
    ///     "idle"). The vocabulary is the framework's single table, not this
    ///     row's — see <see cref="StatusMappers.SessionStatusToText" /> (#663).
    /// </summary>
    public string StatusText => StatusMappers.SessionStatusToText(Status);

    /// <summary>
    ///     Brush resource key for the status dot. The colour is the THEME's
    ///     answer, not this view model's, so the mapping lives in
    ///     <see cref="StatusMappers.SessionStatusToBrushKey" /> (#663).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This property used to carry its own <c>switch</c> and its own
    ///         resource keys, and a remark here asserted that Working was
    ///         <em>meant</em> to be <c>AccentPrimaryBrush</c> while
    ///         <see cref="StatusMappers" /> painted the same status
    ///         <c>MochaYellow</c>. Two UI layers, two answers, each documented
    ///         as correct — the divergence was recorded as a norm instead of as
    ///         a bug. There is one answer now, and it is the theme's: the key
    ///         travels, and the framework's <c>BrushKeyConverter</c>-equivalent
    ///         resolves it against the merged theme resources, so the palette
    ///         follows the theme instead of being frozen into a view model.
    ///     </para>
    ///     <para>
    ///         Because <see cref="Status" /> is an <c>[ObservableProperty]</c>,
    ///         the dot's <c>Fill</c> binding only re-evaluates when
    ///         <see cref="OnStatusChanged" /> explicitly raises
    ///         <see cref="INotifyPropertyChanged.PropertyChanged" /> for this
    ///         property (and <see cref="StatusText" />). That method documents
    ///         the "always green" symptom the raising prevents.
    ///     </para>
    /// </remarks>
    public string StatusColor => StatusMappers.SessionStatusToBrushKey(Status);

    /// <summary>
    ///     Source-generated partial invoked by <c>[ObservableProperty]</c>
    ///     whenever <see cref="Status" /> changes. Raises
    ///     <see cref="INotifyPropertyChanged.PropertyChanged" /> for the
    ///     derived <see cref="StatusColor" /> + <see cref="StatusText" />
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
        this.OnPropertyChanged(nameof(StatusColor));
        this.OnPropertyChanged(nameof(StatusText));
    }
}
