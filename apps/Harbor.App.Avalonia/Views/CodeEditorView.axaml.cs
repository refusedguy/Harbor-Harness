using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.Views.Controls;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.Views;
/// <summary>
///     Code editor view code-behind. Hosts the <see cref="TextEditor" /> from AvaloniaEdit
///     and syncs the active tab's content + syntax-highlighting definition when the
///     <see cref="CodeEditorViewModel.ActiveTab" /> changes. Published LSP diagnostics
///     surface as colored underlines via <see cref="DiagnosticsSquiggleRenderer" />.
/// </summary>
public partial class CodeEditorView : UserControl
{
    private static readonly ILogger<CodeEditorView> Logger =
        LoggerFactory.Create(b => b.AddDebug()).CreateLogger<CodeEditorView>();
    private bool _suppressTextChanged;
    private DiagnosticsSquiggleRenderer? _diagnosticsRenderer;

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
        if (Vm is { } oldVm)
        {
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }
        var vm = Vm;
        if (vm is null) return;
        vm.PropertyChanged += OnVmPropertyChanged;
        EnsureDiagnosticsRenderer();
        ApplyTab(vm.ActiveTab);
    }

    private void EnsureDiagnosticsRenderer()
    {
        if (_diagnosticsRenderer is not null) return;
        try
        {
            _diagnosticsRenderer = new DiagnosticsSquiggleRenderer(Editor.TextArea.TextView);
            Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticsRenderer);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Diagnostics squiggle renderer unavailable");
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeEditorViewModel.ActiveTab))
        {
            ApplyTab(Vm?.ActiveTab);
        }

        if (e.PropertyName == nameof(CodeEditorViewModel.ActiveDiagnostics) && _diagnosticsRenderer is not null)
        {
            _diagnosticsRenderer.SetDiagnostics(Vm?.ActiveDiagnostics ?? []);
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

    private void Editor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.Key == Key.K)
        {
            e.Handled = true;
            ShowInlineEditOverlay();
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.K)
        {
            e.Handled = true;
            ShowInlineEditOverlay();
        }
    }

    private void ShowInlineEditOverlay()
    {
        if (Vm is null || Editor.Document is null) return;

        var selection = Editor.TextArea.Selection;
        if (selection.IsEmpty) return;

        var selectedText = selection.GetText();
        int start = selection.StartOffset;
        int end = selection.EndOffset;

        if (string.IsNullOrEmpty(selectedText) || start == end) return;

        double caretTop = GetCaretPixelTop();
        if (caretTop < 0) caretTop = 0;

        Vm.OpenInlineEdit(selectedText, start, end, caretTop);

        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (InlineEditOverlay is null || Vm is null) return;

            Canvas.SetLeft(InlineEditOverlay, 0);
            Canvas.SetTop(InlineEditOverlay, Math.Max(0, caretTop - 110));
        });
    }

    private double GetCaretPixelTop()
    {
        try
        {
            var textView = Editor.TextArea.TextView;
            var caret = Editor.TextArea.Caret.CalculateCaretRectangle();
            return caret.Top;
        }
        catch
        {
            return 0;
        }
    }

    private void InlineEditPrompt_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (Vm?.AcceptInlineEditCommand.CanExecute(null) == true)
            {
                Vm.AcceptInlineEditCommand.Execute(null);
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Vm?.RejectInlineEditCommand.Execute(null);
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
