using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Abstractions.Models;

namespace Harbor.App.Avalonia.ViewModels;

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
    public int MessageCount { get; }

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
        MessageCount = messageCount;
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
    public string StatusColor => Status switch
    {
        SessionStatus.Working => "AccentPrimaryBrush",
        SessionStatus.Done => "StateSuccessBrush",
        SessionStatus.Error => "StateErrorBrush",
        SessionStatus.Aborted => "StateWarningBrush",
        _ => "TextTertiaryBrush",
    };
}
