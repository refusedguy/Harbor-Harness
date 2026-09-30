# Harbor.Desktop.Shared

Cross-platform implementations built on top of `Harbor.Desktop.Abstractions`.
No UI-framework references — depends only on `Markdig` (already used by every
desktop app).

## What's shared

- **`Services/FuzzySearchService`**: subsequence-match scoring for the command
  palette. Same algorithm as Sublime Text / VS Code — no external deps.
- **`Services/MarkdownToPlainTextService`**: Markdig-based Markdown → plain
  text. Used by the command palette to fuzzy-search chat messages and by
  toast notifications to render a one-line summary.
- **`Locators/ViewModelLocator` (+ `IViewModelLocator`, `LocatorRegistration`,
  `IShowPlaceholderFactory`)**: design-time-friendly VM resolution used by
  platform views that construct view-models by contract.
- **`Commands/BuiltInCommands`**: catalog of built-in command-palette item
  templates (Open Session, New Session, Branch, Toggle Theme, etc.).
- **`Commands/SlashCommands`**: catalog of slash commands with descriptions and
  aliases, projected from the shared `SlashCommandCatalog`
  (`Harbor.Ui.Framework.Abstractions`) — the same registry the CLI slash
  dispatcher binds handlers to. It used to be a private 10-entry copy that had
  drifted: desktop apps offered `/tokens`, `/theme` and `/editor`, which the
  dispatcher cannot run, and missed `/permissions`, `/plugins` and `/skills`.
  See issue #462.

## Dependency rules

✅ **Allowed**: `Harbor.Desktop.Abstractions`, `Harbor.Ui.Framework`,
`Harbor.Ui.Framework.Abstractions` (slash-command registry, #462),
`Markdig`, `Microsoft.Extensions.DependencyInjection.Abstractions`.

`Microsoft.Extensions.Logging.Abstractions` used to be on this list. #535
deleted `Services/RecentItemsService`, the only type here that logged, and #754
dropped the reference that outlived it — so this assembly now names no logging
type at all. Don't add the package back for a single `ILogger`: take the
constructor parameter from the composition root's existing logging stack.

❌ **Forbidden**: any UI framework (`Avalonia*`, `System.Windows.*`,
`Microsoft.Maui.*`, `Microsoft.AspNetCore.Components.*`).

❌ **Forbidden: the filesystem and the user's home directory.** This project is
Presentation, so it must not persist anything — and it does not: there is no
`File.*`/`Directory.*` call and no `Environment.GetFolderPath` in it. That was
not free. `Services/RecentItemsService` used to write `~/.harbor/recent.json`
from here, nothing ever constructed it, and #535 removed it. The guard is
`DesktopSharedTakesNoIoRules`.

If you need to persist shell state (an MRU list, window geometry, pinned views),
put the bytes behind a port declared in Domain, implement it in Infrastructure
next to the `Harbor.Storage.*` family, and let a composition root wire it. Do not
add it here, and do not reach for a `filePath` constructor parameter to keep the
I/O in this assembly — that is the same defect with a test hook on it.

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
