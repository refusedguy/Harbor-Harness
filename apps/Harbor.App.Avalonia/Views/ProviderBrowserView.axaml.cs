using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.Hosting;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Navigation;
using Harbor.Abstractions.Tools;
using Harbor.Desktop.Abstractions.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.Views;
/// <summary>
///     Provider browser code-behind. Loads providers on first visibility —
///     NOT on <c>AttachedToVisualTree</c> — because this view is always in the
///     main window's visual tree (just hidden via IsProviderBrowserOpen).
///     Loading on attach would block the UI for ~30s on a missing Ollama.
/// </summary>
public partial class ProviderBrowserView : UserControl
{
    // #569: Avalonia instantiates this control, so the code-behind cannot take
    // a constructor dependency. The logger is a lazily-created static,
    // matching CodeEditorView and DiagnosticsSquiggleRenderer in this project.
    private static readonly ILogger<ProviderBrowserView> Logger =
        LoggerFactory.Create(b => b.AddDebug()).CreateLogger<ProviderBrowserView>();

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

            // #569: OnPropertyChanged is an Avalonia lifecycle override and
            // cannot await. The VM's LoadProvidersAsync has its own catch that
            // surfaces the message via ErrorMessage, but the command Task was
            // discarded bare — anything raised outside that catch vanished.
            TaskFireAndForget.Forget(
                vm.LoadProvidersCommand.ExecuteAsync(null),
                ex => Logger.LogError(ex, "Provider-browser initial load failed"));
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
        ShellChrome.CloseOverlay(OverlayIds.ProviderBrowser);
    }

    private IShellChrome? _shellChrome;
    private IShellChrome ShellChrome => _shellChrome ??= ShellLocator.Of(this).Get<IShellChrome>();

    private void Provider_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (this.DataContext is ProviderBrowserViewModel vm && vm.SelectedProvider is not null)
        {
            // #569: same synchronous-callback reasoning as the initial load.
            TaskFireAndForget.Forget(
                vm.LoadModelsCommand.ExecuteAsync(vm.SelectedProvider),
                ex => Logger.LogError(ex, "Provider-browser model load failed for {Provider}", vm.SelectedProvider.Id));
        }
    }
}
