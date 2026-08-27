# Harbor.Ui.Framework.ViewModels

Shared ViewModels, converters, and animation helpers for the Harbor UI Framework. Renderer-agnostic — Avalonia, WPF, MAUI, Blazor, CLI, and terminal hosts all consume these.

## Layer

**Presentation (framework view models).** Depends on `Harbor.Ui.Framework.State`, `Harbor.Ui.Framework.Services`, `Harbor.Ui.Framework.Abstractions`, and `Harbor.Abstractions`.

## What's in it

| Subfolder | Contents |
|-----------|----------|
| `ViewModels/` | `ChatLineViewModel`, `ToolCallViewModel`, `DiffViewModel`, `SessionItemViewModel`, `SessionRowViewModel`, `TokenUsageViewModel`, `StoreSubscriberViewModel` (base class that binds a `UiStore` selector to a property). |
| `Converters/` | `StatusMappers` — static brush-key/text/duration/currency mappers for `SessionStatus`, `ToolCallStatus`, cost, time-ago. |
| `Animation/` | `CostAnimator` — animates cost display from base to target value. |

## Public API summary

- **`StoreSubscriberViewModel<T>`**: base class that subscribes to a `UiStore`, selects a slice of state, and applies it to a property.
- **`ChatLineViewModel`**: wraps a `ChatLine` with `RoleBrushKey`, `BrushKey`, `RoleLabel`, `TimestampText`, `Preview`.
- **`ToolCallViewModel`**: tool-call card with `StatusPill`, `DurationText`, `StatusBrushKey`, `Complete(status, result, duration)`.
- **`DiffViewModel`**: `ObservableCollection<DiffRowViewModel>` with per-row `BrushKey`.
- **`SessionItemViewModel` / `SessionRowViewModel`**: session list items with `RelativeTime`, `MetaLine`, tooltip metadata.
- **`TokenUsageViewModel`**: `RecordUsage(UiState)` populates `Bars` and `RecentOutputTokens`; `Reset`/`Clear`.
- **`StatusMappers`**: static methods converting status → brush key, duration → text, cost → USD string, tokens → compact string, time → relative text.
- **`CostAnimator`**: `Start(baseCost)`, `Advance()`, `Stop()` — tick-based cost animation.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.Logging.Abstractions` | Logging |
| `CommunityToolkit.Mvvm` | `ObservableObject`, source generators |

| Project | Purpose |
|---------|---------|
| `Harbor.Abstractions` | Domain types |
| `Harbor.Ui.Framework.State` | State records |
| `Harbor.Ui.Framework.Services` | `SessionStatusTracker` |
| `Harbor.Ui.Framework.Abstractions` | Contracts |

## Tests

No dedicated test project. Validated by `tests/Harbor.Ui.Framework.Tests/` and app-level rendering tests.

## Build

```bash
dotnet build src/Harbor.Ui.Framework.ViewModels/Harbor.Ui.Framework.ViewModels.csproj
```

## Known limitations

- `StoreSubscriberViewModel` uses `IEqualityComparer<T>` for selector deduplication — incorrect comparers can cause missed updates or infinite loops.
- `CostAnimator` is tick-based with no `DispatcherTimer` abstraction; renderers must drive `Advance()`.
