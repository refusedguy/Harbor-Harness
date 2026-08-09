using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Harbor.App.Avalonia.Views.Components;

public partial class Kbd : UserControl
{
    public static readonly StyledProperty<string> KeysProperty =
        AvaloniaProperty.Register<Kbd, string>(nameof(Keys), string.Empty);

    public string Keys
    {
        get => GetValue(KeysProperty);
        set => SetValue(KeysProperty, value);
    }

    public Kbd()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}