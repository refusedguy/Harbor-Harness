using Avalonia.Controls;

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
}
