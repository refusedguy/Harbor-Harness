using Avalonia.Controls;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.ViewModels;
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
        if (DataContext is not ToolCallViewModel vm || vm.DiffFull is null)
            return;

        if (TopLevel.GetTopLevel(this) is not Window window)
            return;

        if (window.DataContext is not MainViewModel main)
            return;

        main.ActiveDiffText = vm.DiffFull;
        main.ActiveDiffTitle = vm.DiffFilePath ?? "diff";
        main.RightDrawerTab = "diff";
        main.IsRightDrawerOpen = true;
    }
}
