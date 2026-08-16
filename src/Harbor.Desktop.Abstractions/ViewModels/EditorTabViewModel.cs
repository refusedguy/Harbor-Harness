using CommunityToolkit.Mvvm.ComponentModel;
namespace Harbor.Desktop.Abstractions.ViewModels;
/// <summary>One editor tab — file path, name, extension, content, dirty flag.</summary>
public partial class EditorTabViewModel : ObservableObject
{

    [ObservableProperty]
    private string _content;

    [ObservableProperty]
    private bool _isDirty;
    /// <summary>Construct an editor tab.</summary>
    public EditorTabViewModel(string filePath, string fileName, string extension, string content)
    {
        FilePath = filePath;
        FileName = fileName;
        Extension = extension;
        _content = content;
    }

    /// <summary>Absolute file path on disk.</summary>
    public string FilePath { get; set; }

    /// <summary>Short file name (no directory).</summary>
    public string FileName { get; set; }

    /// <summary>File extension (no leading dot) — drives syntax highlighting.</summary>
    public string Extension { get; }

    /// <summary>Partial-patch setter: updates content + marks the tab dirty.</summary>
    partial void OnContentChanged(string value) => IsDirty = true;
}
