using Harbor.Desktop.Abstractions.Models;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Base for the command-palette view-model. Holds the query string,
///     the filtered item list, and the selected index. The actual fuzzy
///     search is delegated to
///     <see cref="Harbor.Desktop.Shared.Services.FuzzySearchService"/>.
/// </summary>
public abstract partial class CommandPaletteViewModelBase : ViewModelBase
{
    /// <summary>Construct a <see cref="CommandPaletteViewModelBase"/>.</summary>
    protected CommandPaletteViewModelBase(ILogger logger) : base(logger)
    {
    }

    /// <summary>All available palette items (unfiltered). Derived class populates this from BuiltInCommands + plugin commands.</summary>
    public ObservableCollection<CommandPaletteItem> AllItems { get; } = new();

    /// <summary>Filtered items, displayed in the palette list.</summary>
    public ObservableCollection<CommandPaletteItem> FilteredItems { get; } = new();

    /// <summary>User-typed query.</summary>
    [ObservableProperty]
    private string _query = string.Empty;

    /// <summary>Index of the currently highlighted item in <see cref="FilteredItems"/>.</summary>
    [ObservableProperty]
    private int _selectedIndex = -1;

    /// <summary>True when the palette is visible (Ctrl+P).</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Recompute <see cref="FilteredItems"/> from <see cref="AllItems"/> using the query.</summary>
    protected abstract void ApplyFilter();

    /// <summary>Invoke the selected item's action and close the palette.</summary>
    protected abstract void ActivateSelected();
}
