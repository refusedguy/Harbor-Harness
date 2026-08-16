using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Harbor.App.Avalonia.Views.Dev;

public partial class ComponentGalleryView : UserControl
{
    public ComponentGalleryView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}