using Avalonia.Controls;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Toast container view. No code-behind logic — purely bound to
/// <c>MainViewModel.Toasts</c>.
/// </summary>
public partial class ToastNotificationsView : UserControl
{
    /// <summary>Construct the toast container.</summary>
    public ToastNotificationsView()
    {
        InitializeComponent();
    }
}
