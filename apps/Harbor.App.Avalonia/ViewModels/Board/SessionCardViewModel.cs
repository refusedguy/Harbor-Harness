using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Ui.Framework.Converters;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.ViewModels.Board;

public sealed class SessionCardViewModel : Harbor.Desktop.Abstractions.ViewModels.SessionCardViewModel
{
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
        : base(id, title, previewText, status, createdAt, updatedAt, sessionManager, dialogs, toasts, logger)
    {
    }
}
