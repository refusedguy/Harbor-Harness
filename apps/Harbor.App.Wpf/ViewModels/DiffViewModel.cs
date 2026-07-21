using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace Harbor.App.Wpf.ViewModels;
/// <summary>
///     Side-by-side diff view model. Renders a list of hunks; each hunk has
///     left (before) and right (after) lines.
/// </summary>
public sealed partial class DiffViewModel : ObservableObject
{

    /// <summary>Number of added lines.</summary>
    [ObservableProperty] private int _addedLines;

    /// <summary>File path being diffed.</summary>
    [ObservableProperty] private string _filePath = "(no file)";

    /// <summary>Number of removed lines.</summary>
    [ObservableProperty] private int _removedLines;

    /// <summary>Status summary.</summary>
    [ObservableProperty] private string _status = "No changes";
    /// <summary>Construct a <see cref="DiffViewModel" />.</summary>
    public DiffViewModel()
    {
        Hunks = new ObservableCollection<DiffHunkViewModel>();
        FilePath = "(no file)";
        Status = "No changes";

        // Seed a sample diff so the panel renders something on first launch.
        Hunks.Add(new DiffHunkViewModel(
            "@@ -10,5 +10,7 @@",
            new[]
            {
                new DiffLineViewModel("public void OldMethod()", DiffLineKind.Context),
                new DiffLineViewModel("{", DiffLineKind.Context),
                new DiffLineViewModel("    return;", DiffLineKind.Removed),
                new DiffLineViewModel("    return result;", DiffLineKind.Added),
                new DiffLineViewModel("}", DiffLineKind.Context)
            }));
    }

    /// <summary>Diff hunks to render.</summary>
    public ObservableCollection<DiffHunkViewModel> Hunks { get; }

    /// <summary>Refresh the diff (placeholder).</summary>
    [RelayCommand]
    private void Refresh()
    {
        AddedLines = 0;
        RemovedLines = 0;
        foreach (var hunk in Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (line.Kind == DiffLineKind.Added) AddedLines++;
                else if (line.Kind == DiffLineKind.Removed) RemovedLines++;
            }
        }
        Status = $"+{AddedLines} / -{RemovedLines}";
    }

    /// <summary>Accept all changes.</summary>
    [RelayCommand]
    private void AcceptAll() => Status = "Applied";

    /// <summary>Reject all changes.</summary>
    [RelayCommand]
    private void RejectAll() => Status = "Rejected";
}

/// <summary>
///     A single diff hunk (a contiguous block of changed lines).
/// </summary>
/// <param name="Header">Hunk header (e.g. <c>@@ -10,5 +10,7 @@</c>).</param>
/// <param name="Lines">Lines in the hunk.</param>
public sealed record DiffHunkViewModel(string Header, IReadOnlyList<DiffLineViewModel> Lines);

/// <summary>
///     A single line in a diff hunk.
/// </summary>
/// <param name="Text">Line text.</param>
/// <param name="Kind">Line kind (added, removed, context).</param>
public sealed record DiffLineViewModel(string Text, DiffLineKind Kind)
{
    /// <summary>Background brush for the line based on its kind.</summary>
    public Brush LineBackground => Kind switch
    {
        DiffLineKind.Added => new SolidColorBrush(Color.FromArgb(0x40, 0xA6, 0xE3, 0xA1)),
        DiffLineKind.Removed => new SolidColorBrush(Color.FromArgb(0x40, 0xF3, 0x8B, 0xA8)),
        _ => Brushes.Transparent
    };

    /// <summary>Foreground brush for the line based on its kind.</summary>
    public Brush LineForeground => Kind switch
    {
        DiffLineKind.Added => new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)),
        DiffLineKind.Removed => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)),
        _ => new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0))
    };
}

/// <summary>Kind of diff line.</summary>
public enum DiffLineKind
{
    /// <summary>Unchanged context line.</summary>
    Context,

    /// <summary>Added line (appears only on the right side).</summary>
    Added,

    /// <summary>Removed line (appears only on the left side).</summary>
    Removed
}
