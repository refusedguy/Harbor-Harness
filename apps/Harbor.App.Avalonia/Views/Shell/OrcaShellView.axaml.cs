using Avalonia.Controls;

namespace Harbor.App.Avalonia.Views.Shell;

/// <summary>
///     Orca shell root view — code-behind.
/// </summary>
/// <remarks>
///     The Orca shell is an experimental alternative to the classic
///     Catppuccin-Mocha layout in <c>MainWindow.axaml</c>. Activated by the
///     <c>HARBOR_SHELL=orca</c> env var or <c>--shell orca</c> CLI arg (parsed
///     in <c>Program.cs</c>, exposed via <c>App.ShellMode</c>).
/// </remarks>
public partial class OrcaShellView : UserControl
{
    /// <summary>Construct the Orca shell view.</summary>
    public OrcaShellView()
    {
        InitializeComponent();
    }
}
