using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Harbor.App.Wpf.Services;

/// <summary>
///     Thin wrapper over WPF <see cref="OpenFileDialog" /> /
///     <see cref="SaveFileDialog" /> / folder browser so view-models can
///     prompt the user without taking a direct dependency on
///     <c>Microsoft.Win32</c>.
/// </summary>
public sealed class WpfFilePicker
{
    /// <summary>Filter constant for C# source files.</summary>
    public const string FilterCSharp = "C# source (*.cs)|*.cs|All files (*.*)|*.*";

    /// <summary>Filter constant for markdown files.</summary>
    public const string FilterMarkdown = "Markdown (*.md;*.markdown)|*.md;*.markdown|All files (*.*)|*.*";

    /// <summary>Filter constant for JSON files.</summary>
    public const string FilterJson = "JSON (*.json)|*.json|All files (*.*)|*.*";

    /// <summary>Filter constant for any file.</summary>
    public const string FilterAll = "All files (*.*)|*.*";

    /// <summary>Prompt the user to pick an existing file.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="filter">File filter (see <see cref="FilterCSharp" />).</param>
    /// <param name="initialDirectory">Optional initial directory.</param>
    /// <returns>The selected file path, or <see langword="null" /> if cancelled.</returns>
    public string? PickOpenFile(string title, string filter, string? initialDirectory = null)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false
        };
        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
            dlg.InitialDirectory = initialDirectory;

        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>Prompt the user to pick multiple existing files.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="filter">File filter.</param>
    /// <returns>A read-only list of selected paths (may be empty).</returns>
    public IReadOnlyList<string> PickOpenFiles(string title, string filter)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = true,
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return Array.Empty<string>();
        return dlg.FileNames;
    }

    /// <summary>Prompt the user to choose a save path.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="filter">File filter.</param>
    /// <param name="defaultExt">Default extension (without dot).</param>
    /// <param name="suggestedName">Optional suggested file name.</param>
    /// <returns>The chosen path, or <see langword="null" /> if cancelled.</returns>
    public string? PickSaveFile(string title, string filter, string defaultExt, string? suggestedName = null)
    {
        var dlg = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            DefaultExt = defaultExt,
            AddExtension = true,
            OverwritePrompt = true
        };
        if (!string.IsNullOrEmpty(suggestedName))
            dlg.FileName = suggestedName;

        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>
    ///     Prompt the user to pick a folder using the modern
    ///     <c>OpenFolderDialog</c> (.NET 10+ on Windows).
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="initialDirectory">Optional initial directory.</param>
    /// <returns>The chosen folder path, or <see langword="null" /> if cancelled.</returns>
    public string? PickFolder(string title, string? initialDirectory = null)
    {
        // OpenFolderDialog was added in .NET 8 Preview for WPF — prefer it
        // when available, fall back to a save-file hack with a dummy filter
        // for older runtimes.
        try
        {
            var folderType = Type.GetType("Microsoft.Win32.OpenFolderDialog, PresentationFramework, Version=8.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
            if (folderType is not null)
            {
                dynamic dlg = Activator.CreateInstance(folderType)!;
                dlg.Title = title;
                if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
                    dlg.InitialDirectory = initialDirectory;
                bool? result = dlg.ShowDialog();
                return result == true ? (string)dlg.FolderName : null;
            }
        }
        catch
        {
            // Fall through to the legacy fallback.
        }

        // Fallback: use a SaveFileDialog with "Pick folder" semantics by
        // taking the directory of the chosen path. Not ideal but functional.
        var fallback = new SaveFileDialog
        {
            Title = title + " (pick any file in the folder)",
            Filter = "Folder sentinel (*.folder)|*.folder",
            FileName = "select"
        };
        if (fallback.ShowDialog() != true) return null;
        return Path.GetDirectoryName(fallback.FileName);
    }
}
