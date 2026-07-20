# Component Catalog

> Reusable UI components in Harbor. Each component exists in 3 platform flavors
> (Avalonia / Blazor / WPF) with identical prop names and shared logic via
> `Harbor.Ui.Framework.Converters.StatusMappers`.

## Table of contents

1. [StatusBadge](#statusbadge)
2. [ChatBubble](#chatbubble)
3. [SessionRow](#sessionrow)
4. [ToolCallCardView](#toolcallcardview)
5. [Sparkline](#sparkline)
6. [TypewriterStreamingText](#typewriterstreamingtext)
7. [CodeBlock](#codeblock)
8. [MarkdownRenderer](#markdownrenderer)
9. [ProviderModelPicker](#providermodelpicker)
10. [Platform-agnostic helpers](#platform-agnostic-helpers)

---

## StatusBadge

**Purpose:** Colored dot + label pill, used in status bars, headers, tool-call cards.

**Props:**
| Prop | Type | Default | Description |
|---|---|---|---|
| `StatusText` | `string` | `""` | Label text inside the badge |
| `BrushKey` | `string` | `"StatusIdleBrush"` | Resource key for the dot fill |
| `ShowDot` | `bool` | `true` | Toggle the leading ellipse |

**Platform implementations:**
- Avalonia: `apps/Harbor.App.Avalonia/Views/Components/StatusBadge.axaml(.cs)`
- Blazor: `apps/Harbor.App.Blazor/Components/Shared/StatusBadge.razor`
- WPF: `apps/Harbor.App.Wpf/Controls/StatusBadge.xaml(.cs)`

**Usage (Avalonia):**
```xml
<comp:StatusBadge StatusText="{Binding StatusText}"
                  BrushKey="{Binding StatusBrushKey}"/>
```

**Usage (Blazor):**
```razor
<StatusBadge StatusText="@statusText"
             BrushKey="@brushKey"
             ShowDot="true" />
```

---

## ChatBubble

**Purpose:** Chat-row component with role pill + message body + optional timestamp.

**Props:**
| Prop | Type | Default | Description |
|---|---|---|---|
| `RoleLabel` | `string` | `"user"` | Short lowercase role label |
| `Text` | `string` | `""` | Message body |
| `BrushKey` | `string` | `"ChatUserBrush"` | Resource key for role accent color |
| `Timestamp` | `string?` | `null` | Optional timestamp string (hidden when null) |
| `IsCompact` | `bool` | `false` | Toggle compact padding |

**Platform implementations:**
- Avalonia: `apps/Harbor.App.Avalonia/Views/Components/ChatBubble.axaml(.cs)`
- Blazor: `apps/Harbor.App.Blazor/Components/Shared/ChatBubble.razor`
- WPF: `apps/Harbor.App.Wpf/Controls/ChatBubble.xaml(.cs)`

**Role label values** (driven by `ChatLineViewModel.RoleLabel`):
- `user` — user prompt
- `assistant` — LLM response
- `thinking` — extended thinking
- `tool` — tool call invocation
- `tool-result` — tool call result
- `system` — system message
- `error` — error message

**Role brush keys** (driven by `ChatLineViewModel.RoleBrushKey`):
- `ChatUserBrush` — user accent
- `ChatAssistantBrush` — assistant accent
- `ChatThinkingBrush` — thinking accent (dimmed)
- `ChatToolBrush` — tool call accent
- `ChatToolResultBrush` — tool result accent
- `ChatSystemBrush` — system accent
- `ChatErrorBrush` — error accent (red)

---

## SessionRow

**Purpose:** Sidebar row for a session in the session list.

**Props:**
| Prop | Type | Default | Description |
|---|---|---|---|
| `Title` | `string` | `""` | Session title |
| `Subtitle` | `string` | `""` | Subtitle (agent + model) |
| `RelativeTime` | `string` | `""` | Pre-formatted time ("5m ago") |
| `MessageCount` | `int` | `0` | Live message count |
| `StatusColorKey` | `string` | `"MochaOverlay0"` | Resource key for status dot color |
| `IsDirty` | `bool` | `false` | Git working-tree dirty indicator |
| `IsActive` | `bool` | `false` | Currently active session |

**Platform implementations:**
- Avalonia: `apps/Harbor.App.Avalonia/Views/Components/SessionRow.axaml(.cs)`
- Blazor: `apps/Harbor.App.Blazor/Components/Shared/SessionRow.razor`
- WPF: `apps/Harbor.App.Wpf/Controls/SessionRow.xaml(.cs)`

---

## ToolCallCardView

**Purpose:** Collapsible card showing a single tool call (name + status pill + duration + args/result).

**Avalonia only** (not yet ported to Blazor/WPF):
`apps/Harbor.App.Avalonia/Views/Controls/ToolCallCardView.axaml(.cs)`

**Binds to:** `Harbor.Ui.Framework.ViewModels.ToolCallViewModel` — properties:
- `ToolName`, `IconText`, `StatusPill`, `DurationText`
- `StatusBrushKey` (resource key string — resolved by `BrushKeyConverter`)
- `IsExpanded`, `ArgsPreview`, `ResultPreview`

---

## Sparkline

**Purpose:** Compact inline chart (no axes) for token-usage history in the status bar.

**Avalonia only**:
`apps/Harbor.App.Avalonia/Views/Controls/Sparkline.axaml(.cs)`

**Props:** `Values` (`IEnumerable<double>?`), `StrokeBrush` (`IBrush?`).

---

## TypewriterStreamingText

**Purpose:** Animated streaming text with a blinking cursor — used for the live streaming buffer.

**Avalonia only**:
`apps/Harbor.App.Avalonia/Views/Controls/TypewriterStreamingText.axaml(.cs)`

**Props:** `Text` (`string`), `IsStreaming` (`bool`).

---

## CodeBlock

**Purpose:** Syntax-highlighted code block (used by `MarkdownRenderer` for fenced code).

**Avalonia only**:
`apps/Harbor.App.Avalonia/Views/Controls/CodeBlock.axaml(.cs)`

**Props:** `Code` (`string`), `Language` (`string`).

---

## MarkdownRenderer

**Purpose:** Renders a markdown string into native Avalonia controls.

**Avalonia only**:
`apps/Harbor.App.Avalonia/Views/Controls/MarkdownRenderer.axaml(.cs)` (decomposed R31)

**Internal structure (R31 decomposition):**
- `MarkdownRenderer.axaml.cs` — control surface (property + pipeline + Render entry point), 110 lines
- `Markdown/MarkdownBlockRenderer.cs` — block-level rendering (headings, paragraphs, lists, quotes, code, thematic breaks), 202 lines
- `Markdown/MarkdownInlineRenderer.cs` — inline emission (Run, LineBreak, emphasis, code, links), 173 lines
- `Markdown/MarkdownTextExtractor.cs` — pure text extraction from Markdig trees, 96 lines
- `Markdown/MarkdownResourceResolver.cs` — brush/font resource lookup, 57 lines

**Props:** `Markdown` (`string`).

**Supported elements:** ATX headings (H1-H6), paragraphs, bold/italic/strike, inline code, fenced code blocks, bullet & numbered lists, blockquotes, links, thematic breaks.

---

## ProviderModelPicker

**Purpose:** Searchable picker for provider + model selection with auth-status indicators.

**Avalonia only**:
`apps/Harbor.App.Avalonia/Views/Controls/ProviderModelPicker.axaml(.cs)`

**Binds to:** `Harbor.App.Avalonia.ViewModels.ProviderModelPickerViewModel`.

---

## Platform-agnostic helpers

All UI components call into `Harbor.Ui.Framework.Converters.StatusMappers` for formatting.
Each platform wraps these in its own `IValueConverter`:

### `Harbor.Ui.Framework.Converters.StatusMappers`

Static class with pure functions (no UI framework dependency):

| Method | Input | Output | Description |
|---|---|---|---|
| `StatusToBrushKey` | `string?` status | `string` resource key | `"running"` → `"StatusRunningBrush"` etc. |
| `ToolCallStatusToBrushKey` | `ToolCallStatus` | `string` | `Running` → `"MochaYellow"` etc. |
| `ToolCallStatusToPill` | `ToolCallStatus` | `string` | `Running` → `"running"`, `Success` → `"ok"`, `Error` → `"err"` |
| `SessionStatusToText` | `SessionStatus` | `string` | `Working` → `"working"` etc. |
| `SessionStatusToBrushKey` | `SessionStatus` | `string` | `Working` → `"MochaYellow"` etc. |
| `DurationToText` | `TimeSpan` | `string` | `234ms` / `1.5s` / `""` |
| `TimeAgo` | `DateTime?` | `string` | `"just now"` / `"5m ago"` / `"Mar 5"` |
| `TokensToCompact` | `long` | `string` | `"500"` / `"1.2K"` / `"1.4M"` |
| `CostToUsd` | `decimal` | `string` | `"$0.0123"` |

### Avalonia converter wrappers

`apps/Harbor.App.Avalonia/Views/Converters.cs` wraps each `StatusMappers` method as an
`IValueConverter` for AXAML binding:

- `BrushKeyConverter` — resolve resource-key string → `IBrush`
- `EqualityConverter`, `InequalityConverter` — value-equals-parameter for tab switching
- `FinishLabelConverter` — wizard "Finish" / "Next" label
- `StepToStepperBrushConverter` — wizard progress dots
- `StatusTextToBrushConverter` — `StatusMappers.StatusToBrushKey` → `IBrush`
- `ToolCallStatusToBrushConverter` — `StatusMappers.ToolCallStatusToBrushKey` → `IBrush`
- `SessionStatusToTextConverter` — `StatusMappers.SessionStatusToText`
- `SessionStatusToBrushConverter` — `StatusMappers.SessionStatusToBrushKey` → `IBrush`
- `TimeAgoConverter` — `StatusMappers.TimeAgo`
- `TokensToCompactConverter` — `StatusMappers.TokensToCompact`
- `CostToUsdConverter` — `StatusMappers.CostToUsd`
- `InverseBoolConverter` — invert a boolean
- `StringNullOrEmptyToBoolConverter` — null/empty → `true`

### WPF converter wrappers

`apps/Harbor.App.Wpf/Converters/Converters.cs` mirrors the Avalonia wrappers (same names,
same logic, `System.Windows.Data.IValueConverter` instead of Avalonia's). Includes
`NullToCollapsedConverter` for the `Visibility` enum.

### Blazor

Blazor components call `StatusMappers` directly from `@code` blocks — no converter layer
needed because Razor evaluates C# expressions inline.

---

## Adding a new platform

To add a new platform (e.g. MAUI WinUI):

1. Create `apps/Harbor.App.Maui/Controls/` folder.
2. Port each of `StatusBadge`, `ChatBubble`, `SessionRow` to MAUI XAML.
3. Use the SAME property names (`StatusText`, `BrushKey`, `Title`, etc.).
4. Resolve `BrushKey` via a MAUI `IValueConverter` that wraps `StatusMappers.*` lookups.
5. Add unit tests for property defaults + setters (mirror `tests/Harbor.App.Avalonia.Tests/ComponentTests.cs`).

The shared `StatusMappers` helpers + shared prop names ensure your new platform's
components look + behave identically to Avalonia / Blazor / WPF.
