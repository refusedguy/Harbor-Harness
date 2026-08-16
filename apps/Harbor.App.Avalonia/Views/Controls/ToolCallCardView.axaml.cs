using Avalonia.Controls;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Navigation;
using Microsoft.Extensions.DependencyInjection;
using ToolCallVm = Harbor.Ui.Framework.ViewModels.ToolCallViewModel;
namespace Harbor.App.Avalonia.Views.Controls;
/// <summary>
///     Collapsible tool-call card. Pure view — all state lives in
///     <c>ToolCallViewModel</c>. Slide-in animation is defined in
///     <c>AppStyles.axaml</c> via the <c>Border.ToolCallCard</c> style.
/// </summary>
public partial class ToolCallCardView : UserControl
{
    /// <summary>Construct the tool-call card.</summary>
    public ToolCallCardView()
    {
        InitializeComponent();
    }

    private void DiffCompact_ExpandRequested(object? sender, EventArgs e)
    {
        if (DataContext is not ToolCallVm vm || vm.DiffFull is null)
            return;

        ShellChrome.OpenOverlay("diff");
    }

    private IShellChrome? _shellChrome;
    private IShellChrome ShellChrome => _shellChrome ??= App.Services.GetRequiredService<IShellChrome>();
}
