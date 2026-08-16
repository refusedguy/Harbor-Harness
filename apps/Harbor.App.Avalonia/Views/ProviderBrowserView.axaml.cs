using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Navigation;
using Harbor.Desktop.Abstractions.ViewModels;
using Microsoft.Extensions.DependencyInjection;
namespace Harbor.App.Avalonia.Views;
/// <summary>
///     Provider browser code-behind. Loads providers on first visibility —
///     NOT on <c>AttachedToVisualTree</c> — because this view is always in the
///     main window's visual tree (just hidden via IsProviderBrowserOpen).
///     Loading on attach would block the UI for ~30s on a missing Ollama.
/// </summary>
public partial class ProviderBrowserView : UserControl
{
    private bool _loadedOnce;

    /// <summary>Construct the provider browser.</summary>
    public ProviderBrowserView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Overrides <c>OnPropertyChanged</c> rather than subscribing to
    ///     <c>GetPropertyChangedObservable(IsVisibleProperty).Subscribe(...)</c>
    ///     because the lambda-based Subscribe hits an extension-method
    ///     resolution ambiguity under .NET 10 (the compiler picks the
    ///     <c>IObserver&lt;T&gt;</c> overload instead of
    ///     <c>Action&lt;T&gt;</c>). The override is also slightly cheaper
    ///     (no allocation for the observable subscription).
    /// </remarks>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty
            && change.NewValue is true
            && !_loadedOnce
            && this.DataContext is ProviderBrowserViewModel vm)
        {
            _loadedOnce = true;
            _ = vm.LoadProvidersCommand.ExecuteAsync(null);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => CloseModal();

    /// <summary>
    ///     Click on the backdrop (the dark scrim outside the card) closes the
    ///     modal — same behaviour as Esc and the Close button.
    /// </summary>
    private void Backdrop_Click(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender))
        {
            CloseModal();
        }
    }

    private void CloseModal()
    {
        ShellChrome.CloseOverlay("providerBrowser");
    }

    private IShellChrome? _shellChrome;
    private IShellChrome ShellChrome => _shellChrome ??= App.Services.GetRequiredService<IShellChrome>();

    private void Provider_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (this.DataContext is ProviderBrowserViewModel vm && vm.SelectedProvider is not null)
        {
            _ = vm.LoadModelsCommand.ExecuteAsync(vm.SelectedProvider);
        }
    }
}
