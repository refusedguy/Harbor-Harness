using System.Windows.Media;
using Harbor.App.Wpf.ViewModels;

namespace Harbor.App.Wpf.ViewModels;

/// <summary>
///     Toast color extensions — partial on <see cref="ToastViewModel" /> so
///     the XAML can bind to <c>AccentBrush</c> without a converter.
/// </summary>
public sealed partial class ToastViewModel
{
    /// <summary>Accent brush for the toast's left border + icon.</summary>
    public Brush AccentBrush => Kind switch
    {
        ToastKind.Success => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA6, 0xE3, 0xA1)),
        ToastKind.Warning => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF9, 0xE2, 0xAF)),
        ToastKind.Error => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0x8B, 0xA8)),
        _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x89, 0xB4, 0xFA))
    };
}
