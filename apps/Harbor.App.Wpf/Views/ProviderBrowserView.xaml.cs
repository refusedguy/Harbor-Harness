using System.Windows;
namespace Harbor.App.Wpf.Views;
/// <summary>
///     Provider browser modal — pick a provider + model.
/// </summary>
public partial class ProviderBrowserView : Window
{
    /// <summary>Construct a <see cref="ProviderBrowserView" />.</summary>
    public ProviderBrowserView()
    {
        InitializeComponent();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = true;
        this.Close();
    }
}
