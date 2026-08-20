using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Harbor.Ui.Framework.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Avalonia.Views.Overlays;

public partial class ModalHostView : UserControl
{
    public ModalHostView()
    {
        if (global::Avalonia.Application.Current is not null)
            InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnScrim_Click(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender)
        {
            ShellChrome.CloseTopOverlay();
        }
    }

    private IShellChrome? _shellChrome;
    private IShellChrome ShellChrome => _shellChrome ??= App.Services.GetRequiredService<IShellChrome>();
}
