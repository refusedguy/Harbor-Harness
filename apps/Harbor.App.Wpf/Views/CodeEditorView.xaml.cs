using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Harbor.App.Wpf.ViewModels;
using ICSharpCode.AvalonEdit.Highlighting;
namespace Harbor.App.Wpf.Views;
/// <summary>
///     AvalonEdit-backed code editor view.
/// </summary>
public partial class CodeEditorView : UserControl
{
    /// <summary>Construct a <see cref="CodeEditorView" />.</summary>
    public CodeEditorView()
    {
        InitializeComponent();
        Editor.TextChanged += OnTextChanged;
        this.DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is CodeEditorViewModel old)
        {
            old.PropertyChanged -= OnVmPropertyChanged;
        }
        if (e.NewValue is CodeEditorViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            Editor.Text = vm.Content;
            ApplySyntax(vm.SyntaxLanguage);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (this.DataContext is not CodeEditorViewModel vm) return;
        if (e.PropertyName == nameof(CodeEditorViewModel.SyntaxLanguage))
        {
            ApplySyntax(vm.SyntaxLanguage);
        }
        else if (e.PropertyName == nameof(CodeEditorViewModel.Content))
        {
            if (Editor.Text != vm.Content)
            {
                Editor.Text = vm.Content;
            }
        }
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (this.DataContext is CodeEditorViewModel vm)
        {
            vm.Content = Editor.Text;
        }
    }

    private void ApplySyntax(string language)
    {
        string name = language switch
        {
            "C#" => "C#",
            "JSON" => "Json",
            "XML" => "XML",
            "HTML" => "HTML",
            "TypeScript" or "JavaScript" => "JavaScript",
            _ => "Text"
        };

        try
        {
            var def = HighlightingManager.Instance.GetDefinition(name);
            Editor.SyntaxHighlighting = def;
        }
        catch
        {
            Editor.SyntaxHighlighting = null;
        }
    }
}
