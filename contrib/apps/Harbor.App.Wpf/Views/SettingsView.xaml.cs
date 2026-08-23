using System.Windows;
namespace Harbor.App.Wpf.Views;
/// <summary>
///     Settings modal — theme, default agent/model, UI options.
/// </summary>
public partial class SettingsView : Window
{
    /// <summary>Construct a <see cref="SettingsView" />.</summary>
    public SettingsView()
    {
        InitializeComponent();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = true;
        this.Close();
    }
}
