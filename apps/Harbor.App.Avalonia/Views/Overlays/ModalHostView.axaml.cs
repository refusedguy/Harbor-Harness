using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Harbor.App.Avalonia.Views.Overlays;

public partial class ModalHostView : UserControl
{
    public ModalHostView()
    {
        if (Application.Current is not null)
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
            if (DataContext is ViewModels.MainViewModel vm)
                vm.OverlayPopCommand?.Execute(null);
        }
    }
}
