using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Ui.Framework.Converters;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels.Board;

public sealed partial class SessionCardViewModel : ObservableObject
{
    private readonly ISessionManager _sessionManager;
    private readonly IDialogService _dialogs;
    private readonly IToastService _toasts;
    private readonly ILogger<SessionCardViewModel> _logger;

    [ObservableProperty] private SessionStatus _status;

    public string Id { get; }
    public string Title { get; private set; }
    public string PreviewText { get; }
    public string Duration { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }

    public SessionCardViewModel(
        string id,
        string title,
        string previewText,
        SessionStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        ISessionManager sessionManager,
        IDialogService dialogs,
        IToastService toasts,
        ILogger<SessionCardViewModel> logger)
    {
        Id = id;
        Title = title;
        PreviewText = previewText;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Duration = ComputeDuration();
        _sessionManager = sessionManager;
        _dialogs = dialogs;
        _toasts = toasts;
        _logger = logger;
    }

    public string RelativeTime => StatusMappers.TimeAgo(UpdatedAt.UtcDateTime);

    public string StatusText => StatusMappers.SessionStatusToText(Status);
    public string StatusBrushKey => StatusMappers.SessionStatusToBrushKey(Status);

    public Harbor.Desktop.Abstractions.Models.SessionDotState DotState => Status switch
    {
        SessionStatus.Working => Harbor.Desktop.Abstractions.Models.SessionDotState.Running,
        SessionStatus.Done => Harbor.Desktop.Abstractions.Models.SessionDotState.Done,
        SessionStatus.Error => Harbor.Desktop.Abstractions.Models.SessionDotState.Error,
        SessionStatus.Aborted => Harbor.Desktop.Abstractions.Models.SessionDotState.Error,
        _ => Harbor.Desktop.Abstractions.Models.SessionDotState.Idle
    };

    partial void OnStatusChanged(SessionStatus value)
    {
        this.OnPropertyChanged(nameof(StatusText));
        this.OnPropertyChanged(nameof(StatusBrushKey));
        this.OnPropertyChanged(nameof(DotState));
    }

    private string ComputeDuration()
    {
        var delta = DateTimeOffset.UtcNow - CreatedAt;
        if (delta.TotalHours < 1)
            return Convert.ToInt32(delta.TotalMinutes).ToString("F0", CultureInfo.InvariantCulture) + "m";
        if (delta.TotalDays < 1)
            return Convert.ToInt32(delta.TotalHours).ToString("F0", CultureInfo.InvariantCulture) + "h " +
                   Convert.ToInt32(delta.TotalMinutes % 60).ToString("F0", CultureInfo.InvariantCulture) + "m";
        return Convert.ToInt32(delta.TotalDays).ToString("F0", CultureInfo.InvariantCulture) + "d " +
               Convert.ToInt32(delta.TotalHours % 24).ToString("F0", CultureInfo.InvariantCulture) + "h";
    }

    [RelayCommand]
    private async Task SelectSessionAsync()
    {
        await _sessionManager.OpenSessionAsync(Id);
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (string.IsNullOrWhiteSpace(Title)) return;
        string? newTitle = await _dialogs.PromptAsync(
            "Rename session",
            "Enter a new name:",
            Title);
        if (string.IsNullOrWhiteSpace(newTitle)) return;

        bool ok = await _sessionManager.RenameSessionAsync(Id, newTitle.Trim());
        if (ok)
            Title = newTitle.Trim();
        else
            _toasts.Show("Rename failed", ToastKind.Error);
    }

    [RelayCommand]
    private async Task DuplicateAsync()
    {
        _toasts.Show("Duplicate not yet implemented", ToastKind.Info);
    }

    [RelayCommand]
    private async Task ArchiveAsync()
    {
        _toasts.Show("Archive not yet implemented", ToastKind.Info);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        bool confirmed = await _dialogs.ConfirmAsync(
            "Delete session",
            $"Delete \"{Title}\"? This cannot be undone.",
            "Delete",
            "Cancel");
        if (!confirmed) return;

        bool ok = await _sessionManager.DeleteSessionAsync(Id);
        if (ok)
            _toasts.Show($"Deleted: {Title}", ToastKind.Warning);
        else
            _toasts.Show("Delete failed", ToastKind.Error);
    }
}
