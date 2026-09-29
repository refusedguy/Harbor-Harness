using Avalonia;
using Avalonia.Controls;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.Views.Controls;
/// <summary>
///     Reusable provider + model picker. Hosts a search box, a scrollable
///     list of providers (each expandable to show its models with auth
///     status), and dispatches model clicks to the bound
///     <see cref="ProviderModelPickerViewModel.SelectModelCommand" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Hosts:</b> embedded in the MainWindow status-bar flyout (click
///         the model label) and in the Settings "Provider Configuration"
///         section. The host sets <see cref="UserControl.DataContext" /> to a
///         fresh <see cref="ProviderModelPickerViewModel" /> resolved from DI
///         and is responsible for calling <c>LoadCommand</c> on open.
///     </para>
///     <para>
///         <b>Auto-load:</b> when the control becomes visible for the first
///         time, it kicks off <see cref="ProviderModelPickerViewModel.LoadCommand" />
///         so the host doesn't have to wire that up itself. Subsequent
///         visibility toggles don't re-trigger the load (the command is
///         idempotent anyway).
///     </para>
/// </remarks>
public partial class ProviderModelPicker : UserControl
{
    // #569: Avalonia instantiates this control, so it cannot take a constructor
    // dependency — the logger is a lazily-created static, matching
    // CodeEditorView's code-behind.
    private static readonly ILogger<ProviderModelPicker> Logger =
        LoggerFactory.Create(b => b.AddDebug()).CreateLogger<ProviderModelPicker>();

    private bool _loadedOnce;

    /// <summary>Construct the picker.</summary>
    public ProviderModelPicker()
    {
        InitializeComponent();
    }

    /// <summary>
    ///     Kick off <see cref="ProviderModelPickerViewModel.LoadCommand" /> the
    ///     first time the control becomes visible. We defer to visibility
    ///     rather than <c>AttachedToVisualTree</c> because the picker is
    ///     always in the visual tree (just hidden) — loading on attach would
    ///     fan out a 5s network call on app startup.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_loadedOnce && this.DataContext is ProviderModelPickerViewModel vm)
        {
            _loadedOnce = true;

            // #569: OnAttachedToVisualTree is synchronous by contract. The VM's
            // Load surfaces failures through ErrorMessage, but the command Task
            // was discarded bare, so a fault outside that catch was lost.
            TaskFireAndForget.Forget(
                vm.LoadCommand.ExecuteAsync(null),
                ex => Logger.LogError(ex, "Model picker auto-load on attach failed"));
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty
            && change.NewValue is true
            && !_loadedOnce
            && this.DataContext is ProviderModelPickerViewModel vm)
        {
            _loadedOnce = true;

            // #569: same synchronous-callback reasoning as the attach hook.
            TaskFireAndForget.Forget(
                vm.LoadCommand.ExecuteAsync(null),
                ex => Logger.LogError(ex, "Model picker auto-load on visibility failed"));
        }
    }
}
