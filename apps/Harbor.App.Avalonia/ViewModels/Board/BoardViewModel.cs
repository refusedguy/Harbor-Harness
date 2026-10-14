using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.Sessions;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels.Board;

public sealed partial class BoardViewModel : ObservableObject
{
    private readonly ISessionStore _sessionStore;
    private readonly ISessionManager _sessionManager;
    private readonly IDispatcherAdapter _dispatcher;
    private readonly IDialogService _dialogs;
    private readonly IToastService _toasts;
    private readonly ILogger<BoardViewModel> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public ObservableCollection<SessionCardViewModel> Cards { get; } = new();

    public BoardViewModel(
        ISessionStore sessionStore,
        ISessionManager sessionManager,
        IDispatcherAdapter dispatcher,
        IDialogService dialogs,
        IToastService toasts,
        ILogger<BoardViewModel> logger,
        ILoggerFactory loggerFactory)
    {
        _sessionStore = sessionStore;
        _sessionManager = sessionManager;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _toasts = toasts;
        _logger = logger;
        _loggerFactory = loggerFactory;

        _sessionManager.StatusChanged += OnSessionStatusChanged;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var result = await _sessionStore.ListAsync().ConfigureAwait(false);
            if (result.IsFailure)
            {
                BoardViewModelLog.ListFailed(_logger, result.Error);
                return;
            }

            _dispatcher.Post(() =>
            {
                Cards.Clear();
                #pragma warning disable CFE0001
                // CFE0001 baseline: docs/ROP-API-INVENTORY.md 5.
                // Guard upstream is an early return.
                foreach (var s in result.Value)
                #pragma warning restore CFE0001
                {
                    var status = _sessionManager.GetStatus(s.Id);
                    var preview = $"{s.Agent} · {s.Model}";
                    var card = new SessionCardViewModel(
                        s.Id,
                        s.Title,
                        preview,
                        status,
                        s.CreatedAt,
                        s.UpdatedAt,
                        _sessionManager,
                        _dialogs,
                        _toasts,
                        _loggerFactory.CreateLogger<SessionCardViewModel>());
                    Cards.Add(card);
                }
            });
        }
        catch (Exception ex)
        {
            BoardViewModelLog.RefreshCrashed(_logger, ex);
        }
    }

    private void OnSessionStatusChanged(string sessionId, SessionStatus status)
    {
        _dispatcher.Post(() =>
        {
            var card = Cards.FirstOrDefault(c => c.Id == sessionId);
            if (card is not null)
                card.Status = status;
        });
    }
}

/// <summary>
///     SG12: BCL <c>[LoggerMessage]</c> delegates for <see cref="BoardViewModel" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class BoardViewModelLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "List sessions failed: {Error}")]
    public static partial void ListFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Refresh sessions crashed")]
    public static partial void RefreshCrashed(ILogger logger, Exception ex);
}
