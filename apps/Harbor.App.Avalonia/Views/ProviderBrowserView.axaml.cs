using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.ViewModels;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Provider browser code-behind. Loads providers on first visibility —
/// NOT on <c>AttachedToVisualTree</c> — because this view is always in the
/// main window's visual tree (just hidden via IsProviderBrowserOpen).
/// Loading on attach would block the UI for ~30s on a missing Ollama.
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

        // Only fire on the false→true transition of IsVisible, and only once
        // per session (the user can re-trigger by closing+reopening the
        // browser — the VM's LoadProvidersCommand is idempotent enough to
        // re-run safely). This is the fix for the "UI hangs when Ollama
        // isn't running" bug: the previous AttachedToVisualTree handler
        // fired on app startup (the view is in the visual tree even when
        // hidden), kicked off GetAllModelsAsync, and blocked for ~30s on
        // the default HttpClient timeout. With this change, the fetch is
        // deferred until the user opens the browser AND is bounded to 5s
        // by the VM's CancellationTokenSource.
        if (change.Property == IsVisibleProperty
            && change.NewValue is true
            && !_loadedOnce
            && DataContext is ProviderBrowserViewModel vm)
        {
            _loadedOnce = true;
            _ = vm.LoadProvidersCommand.ExecuteAsync(null);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (this.VisualRoot is Window window && window.DataContext is MainViewModel main)
        {
            main.IsProviderBrowserOpen = false;
        }
    }

    private void Provider_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ProviderBrowserViewModel vm && vm.SelectedProvider is not null)
        {
            _ = vm.LoadModelsCommand.ExecuteAsync(vm.SelectedProvider);
        }
    }
}
