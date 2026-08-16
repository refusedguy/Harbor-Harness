# Harbor.Desktop.Shared

Cross-platform implementations built on top of `Harbor.Desktop.Abstractions`.
No UI-framework references — depends only on `Markdig` (already used by every
desktop app) and `Microsoft.Extensions.Logging.Abstractions`.

## What's shared

- **`Services/FuzzySearchService`**: subsequence-match scoring for the command
  palette. Same algorithm as Sublime Text / VS Code — no external deps.
- **`Services/MarkdownToPlainTextService`**: Markdig-based Markdown → plain
  text. Used by the command palette to fuzzy-search chat messages and by
  toast notifications to render a one-line summary.
- **`Services/RecentItemsService`**: most-recently-used items list persisted
  to `~/.harbor/recent.json`. Used by the command palette and the recent-files
  menu.
- **`Commands/BuiltInCommands`**: catalog of built-in command-palette item
  templates (Open Session, New Session, Branch, Toggle Theme, etc.).
- **`Commands/SlashCommands`**: catalog of slash commands (`/help`, `/clear`,
  `/quit`, etc.) with descriptions and aliases.

## Dependency rules

✅ **Allowed**: `Harbor.Desktop.Abstractions`, `Markdig`,
`Microsoft.Extensions.Logging.Abstractions`.

❌ **Forbidden**: any UI framework (`Avalonia*`, `System.Windows.*`,
`Microsoft.Maui.*`, `Microsoft.AspNetCore.Components.*`).

These rules are enforced by `tests/Harbor.Architecture.Tests`.

## Usage example

```csharp
// In apps/Harbor.App.Avalonia/ViewModels/CommandPaletteViewModel.cs
using Harbor.Desktop.Abstractions.Models;
using Harbor.Desktop.Shared.Commands;
using Harbor.Desktop.Shared.Services;

public sealed partial class CommandPaletteViewModel : CommandPaletteViewModelBase
{
    private readonly FuzzySearchService _fuzzy = new();

    public CommandPaletteViewModel(ILogger<CommandPaletteViewModel> logger) : base(logger)
    {
        foreach (var template in BuiltInCommands.Templates())
            AllItems.Add(template);
        ApplyFilter();
    }

    protected override void ApplyFilter()
    {
        FilteredItems.Clear();
        var ranked = _fuzzy.Rank(AllItems, Query, item => item.Title);
        foreach (var (item, _) in ranked)
            FilteredItems.Add(item);
    }

    protected override void ActivateSelected()
    {
        if (SelectedIndex >= 0 && SelectedIndex < FilteredItems.Count)
        {
            FilteredItems[SelectedIndex].Action.Invoke();
            IsOpen = false;
        }
    }
}
```
