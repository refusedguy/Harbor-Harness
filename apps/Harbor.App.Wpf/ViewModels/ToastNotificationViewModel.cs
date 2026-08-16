using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Desktop.Abstractions.Models;
using Harbor.Desktop.Abstractions.ViewModels;
namespace Harbor.App.Wpf.ViewModels;
/// <summary>
///     Toast notification queue. Toasts slide in from the top-right, auto-
///     dismiss after a timeout, and keep a bounded history.
/// </summary>
public sealed partial class ToastNotificationViewModel : ObservableObject
{
    private const int MaxVisible = 4;
    private readonly object _lock = new();
    private readonly DispatcherTimer? _timer;

    /// <summary>Construct a <see cref="ToastNotificationViewModel" />.</summary>
    public ToastNotificationViewModel()
    {
        Toasts = new ObservableCollection<ToastViewModel>();
        try
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }
        catch
        {
            // Dispatcher may not be available at design time — silently skip.
        }
    }

    /// <summary>Visible toast notifications (top-right corner).</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; }

    /// <summary>
    ///     Show a toast with the given message and kind.
    /// </summary>
    /// <param name="message">Body text.</param>
    /// <param name="kind">Toast kind (controls accent color + icon).</param>
    public void Show(string message, ToastKind kind = ToastKind.Info)
    {
        var toast = new ToastViewModel
        {
            Message = message,
            Kind = kind,
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            TimeToLive = TimeSpan.FromSeconds(kind == ToastKind.Error ? 8 : 4)
        };

        lock (_lock)
        {
            // Bound the visible queue.
            while (Toasts.Count >= MaxVisible)
            {
                Toasts.RemoveAt(0);
            }
            Toasts.Add(toast);
        }
    }

    /// <summary>Dismiss a toast by id.</summary>
    /// <param name="id">Toast id.</param>
    [RelayCommand]
    public void Dismiss(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock)
        {
            for (int i = 0; i < Toasts.Count; i++)
            {
                if (Toasts[i].Id == id)
                {
                    Toasts.RemoveAt(i);
                    return;
                }
            }
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        List<ToastViewModel>? expired = null;
        lock (_lock)
        {
            for (int i = Toasts.Count - 1; i >= 0; i--)
            {
                if (now - Toasts[i].CreatedAt >= Toasts[i].TimeToLive)
                {
                    (expired ??= new List<ToastViewModel>()).Add(Toasts[i]);
                    Toasts.RemoveAt(i);
                }
            }
        }
    }
}
