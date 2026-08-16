using CommunityToolkit.Mvvm.ComponentModel;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Shared, framework-agnostic view-model for a single-document code
///     editor (the "Monaco-style" editor used by the Blazor shell, and its
///     Avalonia/WPF/MAUI equivalents). Holds the editor content, language,
///     and file path. Save/Copy are expressed as methods that delegate the
///     platform-specific side effects (writing to disk, clipboard access) to
///     the caller — the VM never references Monaco, JSInterop, or any UI
///     framework, so it is reusable across every desktop/web target.
/// </summary>
public sealed partial class MonacoEditorViewModel : ObservableObject
{
    /// <summary>The current editor content.</summary>
    [ObservableProperty]
    private string _content = @"// Welcome to the Harbor code editor.
// This wraps the Monaco editor via JS interop.

using System;

Console.WriteLine(""Hello, Harbor!"");

public static class Fibonacci
{
    public static IEnumerable<int> Stream()
    {
        int a = 0, b = 1;
        while (true)
        {
            yield return a;
            (a, b) = (b, a + b);
        }
    }
}";

    /// <summary>Active language id for syntax highlighting.</summary>
    [ObservableProperty]
    private string _language = "csharp";

    /// <summary>Absolute path of the document on disk (null for an unsaved buffer).</summary>
    [ObservableProperty]
    private string? _filePath;

    /// <summary>Construct a <see cref="MonacoEditorViewModel" /> with the default demo buffer.</summary>
    public MonacoEditorViewModel()
    {
    }

    /// <summary>
    ///     Persist the current <see cref="Content" />. The actual write is
    ///     supplied by the platform (e.g. a <c>File.WriteAllText</c> call or an
    ///     <c>IFilePicker</c> save dialog) via <paramref name="persist" />.
    /// </summary>
    /// <param name="persist">Platform persistence callback (receives the content to save).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveAsync(
        Func<string, Task>? persist = null,
        CancellationToken cancellationToken = default)
    {
        if (persist is not null)
        {
            await persist(Content).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Copy the current <see cref="Content" /> to the clipboard. The actual
    ///     clipboard write is supplied by the platform (e.g. Blazor
    ///     <c>HarborJsInterop</c>) via <paramref name="copy" />.
    /// </summary>
    /// <param name="copy">Platform clipboard write (receives the content).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task CopyAsync(
        Func<string, Task>? copy = null,
        CancellationToken cancellationToken = default)
    {
        if (copy is not null)
        {
            await copy(Content).ConfigureAwait(false);
        }
    }
}
