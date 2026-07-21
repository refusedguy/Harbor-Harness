using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Wpf.Services;
namespace Harbor.App.Wpf.ViewModels;
/// <summary>
///     AvalonEdit-backed code editor view model. Owns the file path, dirty
///     state, and delegates open/save to <see cref="WpfFilePicker" />.
/// </summary>
public sealed partial class CodeEditorViewModel : ObservableObject
{
    private readonly WpfFilePicker? _picker;

    /// <summary>Editor content.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _content = string.Empty;

    /// <summary>Currently open file path (or <see langword="null" /> for untitled).</summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>Whether the buffer has unsaved changes.</summary>
    [ObservableProperty] private bool _isDirty;

    /// <summary>Syntax language name (C#, JSON, Markdown, etc.).</summary>
    [ObservableProperty] private string _syntaxLanguage = "C#";

    /// <summary>Construct a <see cref="CodeEditorViewModel" />.</summary>
    public CodeEditorViewModel() : this(picker: null) { }

    /// <summary>Construct a <see cref="CodeEditorViewModel" /> with a file picker.</summary>
    /// <param name="picker">Optional file picker for open/save dialogs.</param>
    public CodeEditorViewModel(WpfFilePicker? picker)
    {
        _picker = picker;
        FilePath = null;
        Content = "// Welcome to Harbor code editor.\n// Open a file with Ctrl+O or start typing.\n";
        IsDirty = false;
        SyntaxLanguage = "C#";
    }

    /// <summary>Display title for the tab.</summary>
    public string DisplayTitle =>
        (string.IsNullOrEmpty(FilePath) ? "untitled" : Path.GetFileName(FilePath)) +
        (IsDirty ? " *" : string.Empty);

    partial void OnFilePathChanged(string? value) => this.OnPropertyChanged(nameof(DisplayTitle));

    partial void OnContentChanged(string value)
    {
        IsDirty = true;
        this.OnPropertyChanged(nameof(DisplayTitle));
    }

    /// <summary>Open a file via the file picker.</summary>
    [RelayCommand]
    private void OpenFile()
    {
        if (_picker is null) return;
        string? path = _picker.PickOpenFile("Open file", WpfFilePicker.FilterAll);
        if (path is null) return;
        try
        {
            Content = File.ReadAllText(path);
            FilePath = path;
            IsDirty = false;
            SyntaxLanguage = GuessLanguageFromExtension(path);
        }
        catch
        {
            // Swallow — a real app would surface this via a toast.
        }
    }

    /// <summary>Save the buffer (Save As if no path yet).</summary>
    [RelayCommand]
    private void SaveFile()
    {
        if (_picker is null) return;
        if (string.IsNullOrEmpty(FilePath))
        {
            string? path = _picker.PickSaveFile("Save file", WpfFilePicker.FilterAll, "cs", "untitled.cs");
            if (path is null) return;
            FilePath = path;
        }
        try
        {
            File.WriteAllText(FilePath, Content);
            IsDirty = false;
            this.OnPropertyChanged(nameof(DisplayTitle));
        }
        catch
        {
            // Swallow.
        }
    }

    /// <summary>
    ///     Format the buffer (placeholder — full implementation would
    ///     call into a Roslyn formatter).
    /// </summary>
    [RelayCommand]
    private void Format()
    {
        // No-op stub; real implementation would invoke a formatter service.
    }

    private static string GuessLanguageFromExtension(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "C#",
            ".json" => "JSON",
            ".md" => "Markdown",
            ".xml" => "XML",
            ".html" => "HTML",
            ".ts" => "TypeScript",
            ".js" => "JavaScript",
            ".py" => "Python",
            _ => "Text"
        };
    }
}
