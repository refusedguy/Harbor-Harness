using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using Harbor.App.Avalonia.ViewModels;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.Views;
/// <summary>
///     Code editor view code-behind. Hosts the <see cref="TextEditor" /> from AvaloniaEdit
///     and syncs the active tab's content + syntax-highlighting definition when the
///     <see cref="CodeEditorViewModel.ActiveTab" /> changes.
/// </summary>
public partial class CodeEditorView : UserControl
{
    private static readonly ILogger<CodeEditorView> Logger =
        LoggerFactory.Create(b => b.AddDebug()).CreateLogger<CodeEditorView>();
    private bool _suppressTextChanged;

    /// <summary>Construct the code editor view. Avalonia's generated InitializeComponent runs first.</summary>
    public CodeEditorView()
    {
        InitializeComponent();
        Editor.TextChanged += OnEditorTextChanged;
        this.DataContextChanged += OnDataContextChanged;
    }

    private CodeEditorViewModel? Vm => this.DataContext as CodeEditorViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe from old VM.
        if (Vm is { } oldVm)
        {
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }
        var vm = Vm;
        if (vm is null) return;
        vm.PropertyChanged += OnVmPropertyChanged;
        ApplyTab(vm.ActiveTab);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeEditorViewModel.ActiveTab))
        {
            ApplyTab(Vm?.ActiveTab);
        }
    }

    private void ApplyTab(EditorTabViewModel? tab)
    {
        if (tab is null)
        {
            Editor.Document.Text = string.Empty;
            return;
        }

        _suppressTextChanged = true;
        try
        {
            Editor.Document.Text = tab.Content;
            // Look up the syntax-highlighting definition by name.
            var def = HighlightingManager.Instance.GetDefinition(tab.SyntaxName);
            Editor.SyntaxHighlighting = def;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "ApplyTab failed for {FileName}", tab.FileName);
        }
        finally
        {
            _suppressTextChanged = false;
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressTextChanged) return;
        if (Vm?.ActiveTab is { } tab)
        {
            tab.Content = Editor.Document.Text;
        }
    }
}

/// <summary>Converts null to true (inverse of ObjectNotNullConverter).</summary>
public sealed class ObjectIsNullConverter : IValueConverter
{
    /// <summary>Singleton instance.</summary>
    public static readonly ObjectIsNullConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts non-null to true.</summary>
public sealed class ObjectNotNullConverter : IValueConverter
{
    /// <summary>Singleton instance.</summary>
    public static readonly ObjectNotNullConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
