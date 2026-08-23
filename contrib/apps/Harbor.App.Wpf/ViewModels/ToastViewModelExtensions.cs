using System.Windows.Media;
using Harbor.Desktop.Abstractions.Models;
using Harbor.Desktop.Abstractions.ViewModels;
namespace Harbor.App.Wpf.ViewModels;
/// <summary>
///     Toast color extensions — partial on the WPF <see cref="ToastViewModel" />.
///     The WPF view-model derives from the shared
///     <see cref="Harbor.Desktop.Abstractions.ViewModels.ToastViewModel" /> record and
///     adds the WPF <see cref="Brush" /> accent here so the XAML can bind to
///     <c>AccentBrush</c> without a converter. The canonical severity is the
///     shared record's <see cref="Harbor.Desktop.Abstractions.Models.ToastKind" />.
/// </summary>
public sealed partial class ToastViewModel : Harbor.Desktop.Abstractions.ViewModels.ToastViewModel
{
    /// <summary>Construct an empty <see cref="ToastViewModel" /> (used by object initializers).</summary>
    public ToastViewModel()
        : base(string.Empty, string.Empty, ToastKind.Info, default, default) { }

    /// <summary>Accent brush for the toast's left border + icon.</summary>
    public Brush AccentBrush => Kind switch
    {
        ToastKind.Success => new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)),
        ToastKind.Warning => new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF)),
        ToastKind.Error => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)),
        _ => new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA))
    };
}
