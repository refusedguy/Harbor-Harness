# Component Catalog

> Reusable UI components in Harbor. Each component exists in 3 platform flavors
> (Avalonia / Blazor / WPF) with identical prop names and shared logic via
> `Harbor.Ui.Framework.Converters.StatusMappers`.
>
> **Note (sprint-2):** the Blazor/WPF/MAUI apps moved to [`contrib/apps/`](../contrib/apps/) —
> paths below reflect that; Avalonia stays in `apps/Harbor.App.Avalonia`.

## Table of contents

1. [StatusBadge](#statusbadge)
2. [ChatBubble](#chatbubble)
3. [SessionRow](#sessionrow)
4. [StatusDot](#statusdot)
5. [Kbd](#kbd)
6. [SegmentedControl](#segmentedcontrol)
7. [EmptyState](#emptystate)
8. [ToolCallCardView](#toolcallcardview)
9. [Sparkline](#sparkline)
10. [TypewriterStreamingText](#typewriterstreamingtext)
11. [CodeBlock](#codeblock)
12. [MarkdownRenderer](#markdownrenderer)
13. [ProviderModelPicker](#providermodelpicker)
14. [HdsDiffCompact](#hdsdiffcompact)
15. [StatusSegmentBar](#statussegmentbar)
16. [HDS style primitives](#hds-style-primitives)
17. [Cell-renderer widgets (platform-agnostic)](#cell-renderer-widgets-platform-agnostic)
18. [Platform-agnostic helpers](#platform-agnostic-helpers)

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
- Blazor: `contrib/apps/Harbor.App.Blazor/Components/Shared/StatusBadge.razor`
- WPF: `contrib/apps/Harbor.App.Wpf/Controls/StatusBadge.xaml(.cs)`

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
- Blazor: `contrib/apps/Harbor.App.Blazor/Components/Shared/ChatBubble.razor`
- WPF: `contrib/apps/Harbor.App.Wpf/Controls/ChatBubble.xaml(.cs)`

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
- Blazor: `contrib/apps/Harbor.App.Blazor/Components/Shared/SessionRow.razor`
- WPF: `contrib/apps/Harbor.App.Wpf/Controls/SessionRow.xaml(.cs)`

---

## StatusDot

**Purpose:** Minimal colored ellipse for inline status indication (e.g., streaming indicator in chat).

**Avalonia only:**
`apps/Harbor.App.Avalonia/Views/Components/StatusDot.axaml(.cs)`

**Props:** `ColorKey` (`string`), `Size` (`double`).

---

## Kbd

**Purpose:** Keyboard shortcut chip (monospace pill) used in placeholders and hints.

**Avalonia only:**
`apps/Harbor.App.Avalonia/Views/Components/Kbd.axaml(.cs)`

**Props:** `Text` (`string`) — the key label (e.g. `"⏎"`, `"Ctrl+O"`).

---

## SegmentedControl

**Purpose:** Horizontal tab strip with single-selection semantics, backed by a `ListBox`.

**Avalonia only:**
`apps/Harbor.App.Avalonia/Views/Components/SegmentedControl.axaml(.cs)`

**Binds to:** `ItemsSource` (list of options), `SelectedItem` (two-way).

**Accessibility notes:**
- Has `AutomationProperties.AutomationId="SegmentedControl"`.
- ListBox items are non-tab-stop (`IsTabStop="False"`) to avoid trapping keyboard focus.

---

## EmptyState

**Purpose:** Centered placeholder when a panel has no content (CTA + description + optional icon).

**Avalonia only:**
`apps/Harbor.App.Avalonia/Views/Components/EmptyState.axaml(.cs)`

**Props:** `Icon` (`string`), `Title` (`string`), `Subtitle` (`string`), `Cta` (`object?`), `Suggestions` (`IEnumerable<EmptyStateSuggestion>?`).

**Usage (Avalonia):**
```xml
<comp:EmptyState Title="What are we building today?"
                 Subtitle="Harbor runs local-first agents..."
                 Icon="{StaticResource IcMessage}">
    <comp:EmptyState.Cta>
        <TextBox x:Name="HeroInputBox" ... />
    </comp:EmptyState.Cta>
</comp:EmptyState>
```

---

## ToolCallCardView

**Purpose:** Collapsible card showing a single tool call (name + status pill + duration + args/result).

**Avalonia only** (not yet ported to Blazor/WPF):
`apps/Harbor.App.Avalonia/Views/Controls/ToolCallCardView.axaml(.cs)`

**Binds to:** `Harbor.Ui.Framework.ViewModels.ToolCallViewModel` — properties:
- `ToolName`, `IconText`, `StatusPill`, `DurationText`
- `StatusBrushKey` (resource key string — resolved by `BrushKeyConverter`)
- `IsExpanded`, `ArgsPreview`, `ResultPreview`
- `DiffPreview`, `IsDiffTool` (controls inline `HdsDiffCompact` visibility)

**Accessibility notes:**
- Expander header uses static text `"details"`; consider binding to a localized string.
- No `AutomationProperties.Name` on the Expander toggle.

---

## Sparkline

**Purpose:** Compact inline chart (no axes) for token-usage history in the status bar.

**Avalonia only:**
`apps/Harbor.App.Avalonia/Views/Controls/Sparkline.axaml(.cs)`

**Props:**
| Prop | Type | Default | Description |
|---|---|---|---|
| `Values` | `IEnumerable<double>?` | `null` | Data points to render (minimum 2 required) |
| `StrokeBrush` | `IBrush?` | `null` | Line color; when a `SolidColorBrush`, a trailing gradient is auto-built |

**Behavior:**
- Kinetic animation: when values change, the line smoothly interpolates from the previous state to the new state over ~250ms using a `DispatcherTimer` at ~60fps.
- Endpoint dot pulses gently to reinforce the "live" feel.
- Auto-scales to the visible min/max range.

**Usage (Avalonia):**
```xml
<ctrl:Sparkline Values="{Binding TokenHistory}"
                Width="60" Height="16"
                StrokeBrush="{DynamicResource StateWarningBrush}"/>
```

---

## TypewriterStreamingText

**Purpose:** Animated streaming text with a blinking cursor — used for the live streaming buffer.

**Avalonia only:**
`apps/Harbor.App.Avalonia/Views/Controls/TypewriterStreamingText.axaml(.cs)`

**Props:**
| Prop | Type | Default | Description |
|---|---|---|---|
| `Text` | `string` | `""` | The streaming buffer text |
| `IsStreaming` | `bool` | `false` | True while a message is actively streaming; drives cursor visibility |

**Behavior:**
- Cursor blinks at ~1.9 Hz (530 ms on/off) while `IsStreaming` is true.
- Cursor is hidden when idle.
- Timer is started on `Loaded` and stopped on `Unloaded` — no leaks when the chat view is unloaded.
- When `AnimationPreferences.AllowAnimation` is false (headless test), the cursor stays solid rather than blinking.

**Usage (Avalonia):**
```xml
<ctrl:TypewriterStreamingText Text="{Binding StreamingBuffer}"
                              IsStreaming="{Binding IsStreaming}" />
```

---

## CodeBlock

**Purpose:** Syntax-highlighted code block (used by `MarkdownRenderer` for fenced code).

**Avalonia only:**
`apps/Harbor.App.Avalonia/Views/Controls/CodeBlock.axaml(.cs)`

**Props:**
| Prop | Type | Default | Description |
|---|---|---|---|
| `Code` | `string` | `""` | Raw code text to render |
| `Language` | `string` | `""` | Language identifier (e.g. `"csharp"`, `"js"`, `"python"`, `"go"`, `"rust"`, `"sql"`) |

**Behavior:**
- Lightweight tokenizer (keywords, strings, comments, numbers) covering C#/JS/Python/Go/Rust/SQL.
- Rebuilds `TextBlock.Inlines` synchronously on every `Code` or `Language` change — safe for streaming.
- Copy button in the header copies the raw code to the clipboard.

**Accessibility notes:**
- Copy button has no `AutomationProperties.Name`.

**Usage (Avalonia):**
```xml
<ctrl:CodeBlock Code="{Binding CodeText}"
                Language="{Binding Language}" />
```

---

## MarkdownRenderer

**Purpose:** Renders a markdown string into native Avalonia controls.

**Avalonia only:**
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

**Avalonia only:**
`apps/Harbor.App.Avalonia/Views/Controls/ProviderModelPicker.axaml(.cs)`

**Binds to:** `Harbor.App.Avalonia.ViewModels.ProviderModelPickerViewModel`.

**Behavior:**
- Auto-loads providers + models on first visibility (idempotent `LoadCommand`).
- Search box filters by provider name OR model id/name.
- Each provider row is expandable, showing auth status + model list.
- Model selection dispatches `SelectModelCommand` with the selected model as parameter.

**Accessibility notes:**
- Search `TextBox` lacks `AutomationProperties.Name`.
- Provider/model `ListBox` items lack `AutomationProperties.Name`.

**Usage (Avalonia):**
```xml
<ctrl:ProviderModelPicker DataContext="{Binding Picker}"
                          Height="380" />
```

---

## HdsDiffCompact

**Purpose:** Collapsed diff preview (max N lines) with click-to-expand semantics. Used inside `ToolCallCardView` to show tool args/result diffs without overwhelming the card.

**Avalonia only:**
`apps/Harbor.App.Avalonia/Views/Controls/HdsDiffCompact.axaml(.cs)`

**Props:**
| Prop | Type | Default | Description |
|---|---|---|---|
| `DiffText` | `string` | `""` | Raw unified diff text |
| `MaxLines` | `int` | `6` | Maximum lines to show before truncating |

**Events:**
| Event | Description |
|---|---|
| `ExpandRequested` | Raised when the user clicks the compact view to expand the full diff |

**Usage (Avalonia):**
```xml
<ctrl:HdsDiffCompact DiffText="{Binding DiffText}"
                     MaxLines="6"
                     ExpandRequested="OnDiffExpandRequested"/>
```

---

## StatusSegmentBar

**Purpose:** Width-aware status bar segment packer + renderer for the footer bar. Packs
`StatusSeg` items into a fixed-width row, dropping flexible segments right-to-left
when the row overflows, then character-cutting the widest survivor.

**File:** `src/Harbor.Ui.Framework.Rendering/Widgets/StatusSegmentBar.cs`

**Types:**

| Type | Kind | Description |
|---|---|---|
| `StatusAccent` | `enum` | Color accent: `Neutral`, `Dim`, `Accent`, `Success`, `Warning`, `Error` |
| `StatusSeg` | `record struct` | One typed segment: `Text`, `Accent`, `FixedPriority` |
| `StatusBarMode` | `enum` | Footer machine mode: `Idle`, `Running`, `AwaitingApproval`, `Compacting` |
| `SegWidth` | `internal static class` | Wide-rune-aware text width via `UnicodeWidth.Width` |
| `StatusBarLayout` | `static class` | Truncation algorithm: `Fit(Span<StatusSeg>, int width)` returns survivor count |

**Usage (cell renderer):**
```csharp
Span<StatusSeg> workspace = stackalloc StatusSeg[8];
int n = statusViewModel.BuildSegments(workspace);
StatusBarLayout.Fit(workspace, terminalWidth);
StatusBarWidget.Paint(buffer, rect, workspace[..n]);
```

---

## HDS style primitives

**Purpose:** Theme-aware Avalonia style primitives used across the desktop shell.
These are pure XAML styles (no code-behind) applied via CSS-like `Classes` on
standard Avalonia controls. All colors resolve through `DynamicResource` tokens
defined in `Themes/Hds/`.

**Files:**
- `apps/Harbor.App.Avalonia/Views/Components/HdsStyles/HdsCard.axaml`
- `apps/Harbor.App.Avalonia/Views/Components/HdsStyles/HdsButton.axaml`
- `apps/Harbor.App.Avalonia/Views/Components/HdsStyles/HdsChip.axaml`
- `apps/Harbor.App.Avalonia/Views/Components/HdsStyles/HdsPill.axaml`
- `apps/Harbor.App.Avalonia/Views/Components/HdsStyles/HdsFlyout.axaml`
- `apps/Harbor.App.Avalonia/Views/Components/HdsStyles/HdsTextBox.axaml`
- `apps/Harbor.App.Avalonia/Views/Components/HdsStyles/Tooltip.axaml`

### HdsCard

**Classes:** `hds-card`, `hds-card.hoverable`, `hds-card-elevated`

| Class | Effect |
|---|---|
| `hds-card` | `BgPanelElevated` fill, `RadiusLg`, `ShadowSm`, no border |
| `hds-card.hoverable` | `ShadowMd` on `:pointerover` |
| `hds-card-elevated` | `BgApp` fill, `ShadowLg` |

**Usage:**
```xml
<Border Classes="hds-card">
    <!-- card content -->
</Border>
```

### HdsButton

**Classes:** `hds-primary`, `hds-secondary`, `hds-ghost`, `hds-danger`, `hds-icon-28`, `hds-icon-36`

| Class | Effect |
|---|---|
| `hds-primary` | Accent fill, `TextOnAccent` foreground, `RadiusMd` |
| `hds-secondary` | Transparent + `BorderSubtle` border, hover = `BgHover` |
| `hds-ghost` | Transparent, `AccentPrimary` foreground |
| `hds-danger` | `StateError` fill, `TextOnAccent` foreground |
| `hds-icon-28` / `hds-icon-36` | Fixed 28px / 36px circular icon button |

**Focus indicator:** `:focus-visible` applies `AccentPrimaryBrush` 2px border (WCAG 2.4.7).

**Usage:**
```xml
<Button Classes="hds-primary" Content="Save"/>
<Button Classes="hds-icon-28" Content="✕"/>
```

### HdsChip

**Classes:** `hds-chip`, `hds-chip.selected`

| Class | Effect |
|---|---|
| `hds-chip` | Transparent + `BorderSubtle`, `RadiusSm`, `TextTertiary` |
| `hds-chip:hover` | `BgHover` fill, `TextPrimary` |
| `hds-chip.selected` | `AccentPrimary` fill, `TextOnAccent`, semi-bold |

**Usage:**
```xml
<Button Classes="hds-chip" Content="Filter"/>
<Button Classes="hds-chip selected" Content="Active"/>
```

### HdsPill

**Classes:** `hds-pill`

| State | Effect |
|---|---|
| Default | Transparent + `AccentPrimary` border, `RadiusFull` |
| `:hover` | `BgHover` fill |
| `:pressed` | Opacity 0.9 |
| `:disabled` | Opacity 0.5, `TextTertiary` + `BorderSubtle` |

**Usage:**
```xml
<Button Classes="hds-pill" Content="Tag"/>
```

### HdsFlyout

**Targets:** `MenuFlyoutPresenter`, `MenuItem`, `Separator`

| Selector | Effect |
|---|---|
| `MenuFlyoutPresenter` | `BgPanelElevated`, `BorderSubtle`, `Radius12`, width 200 |
| `MenuItem` | 32px min height, `Hand` cursor, `BgHover` on `:pointerover` |
| `Separator` | `BorderSubtle` 1px height |

**Usage:**
```xml
<MenuFlyout>
    <MenuItem Header="Open" InputGesture="Ctrl+O"/>
    <Separator/>
    <MenuItem Header="Exit"/>
</MenuFlyout>
```

### HdsTextBox

**Classes:** `hds-textbox`, `hds-flush`, `hds-search`

| Class | Effect |
|---|---|
| `hds-textbox` | `BgInput` fill + `BorderSubtle`, `Radius` from token, focus = `AccentPrimary` border |
| `hds-flush` | Transparent, hairline bottom border only, focus = 2px accent bottom |
| `hds-search` | Adds `PaddingInputSearch` (left icon + right cancel) |

**Usage:**
```xml
<TextBox Classes="hds-textbox" Watermark="Search..."/>
<TextBox Classes="hds-flush" Watermark="Filter"/>
```

### Tooltip

**Target:** `ToolTip`

| Property | Value |
|---|---|
| `Background` | `BgPanelElevatedBrush` |
| `Foreground` | `TextPrimaryBrush` |
| `CornerRadius` | `RadiusSm` |
| `Padding` | `PaddingTooltip` |
| `Opacity` transition | `EaseFast` (150 ms fade-in) |

**Usage:**
```xml
<TextBlock Text="Hover me">
    <ToolTipService.ToolTip>
        <ToolTip Content="Helpful hint"/>
    </ToolTipService.ToolTip>
</TextBlock>
```

---

## Cell-renderer widgets (platform-agnostic)

These types live in `src/Harbor.Ui.Framework.Rendering/Widgets/` and power the
cell-based terminal renderer (SpectreTUI / ConsoleEx). They have no Avalonia
XAML surface; the catalog lists them for completeness because they are reusable
render primitives shared across all TUI renderers.

### IChatBlock

**Purpose:** One typed cell of the chat timeline. Implemented by `UserBlock`,
`SystemBlock`, `DiffBlock`, `ApprovalGateView`, and streaming blocks.

**File:** `src/Harbor.Ui.Framework.Rendering/Widgets/ChatBlock.cs`

**Contract:**
| Member | Signature | Description |
|---|---|---|
| `Kind` | `string` | Stable kind tag ("user", "assistant", "tool-call", "system", "diff", "approval", ...) |
| `IsStreamContinuation` | `bool` | True while the block is the live streaming tail |
| `BudgetBytes` | `int` | Rough resident size for timeline eviction |
| `Measure` | `BlockMeasure Measure(int width)` | Height in rows for `width` columns |
| `CheapEstimate` | `int CheapEstimate(int width)` | O(length) off-screen layout guess |
| `Paint` | `void Paint(in BlockPaintContext ctx)` | Paints into the clip rect |
| `RawText` | `string RawText()` | Copy-friendly plain text |

### BlockMeasure / BlockPaintContext

**Purpose:** Height report and paint input for a chat block.

**File:** `src/Harbor.Ui.Framework.Rendering/Widgets/ChatBlock.cs`

```csharp
public readonly record struct BlockMeasure(int MinLines, int MaxLines, bool IsExact)
{
    public static BlockMeasure Exact(int lines) => new(lines, lines, true);
    public static BlockMeasure Estimate(int min, int max) => new(min, max, false);
    public int BestGuess => Math.Max(1, IsExact ? MinLines : (MinLines + MaxLines) / 2);
}

public readonly struct BlockPaintContext(ScreenBuffer buffer, Rect rect, long tick)
{
    public ScreenBuffer Buffer { get; }
    public Rect Rect { get; }
    public long Tick { get; }
}
```

### UserBlock / SystemBlock

**Purpose:** User prompt block (`› ` prefix + bold body) and dim italic system
notice (session events, compaction, errors).

**File:** `src/Harbor.Ui.Framework.Rendering/Widgets/BasicBlocks.cs`

```csharp
public sealed class UserBlock : IChatBlock { public UserBlock(string text); ... }
public sealed class SystemBlock : IChatBlock { public SystemBlock(string text); ... }
```

### ApprovalGateView

**Purpose:** Interactive permission card in the chat timeline. Shows which tool
wants approval, what it targets, and the key bindings. Implements
`IFocusTarget` so the host `FocusRouter` can traverse it via Tab.

**File:** `src/Harbor.Ui.Framework.Rendering/Widgets/ApprovalGateView.cs`

**Props / State:**
| Member | Type | Description |
|---|---|---|
| `ToolName` | `string` | Tool requesting approval |
| `IsPending` | `bool` | True until a decision is recorded |
| `Decision` | `ApprovalChoice` | `None` / `Approve` / `Deny` / `AlwaysAllow` |
| `PulseBirthTick` | `long` | Frame tick for warn-glow pulse (-1 when inactive) |

**Events:**
| Event | Description |
|---|---|
| `DecisionRecorded` | Raised exactly once when a decision is recorded |

**Key bindings:** `y`/`Enter` approve, `n`/`Escape` deny, `a` always-allow.

### DiffBlock / UnifiedDiffParser

**Purpose:** Strict unified-diff reader + chat block renderer. Right-aligned
gutter numbers + per-kind color + word-level emphasis for paired changes.

**File:** `src/Harbor.Ui.Framework.Rendering/Widgets/DiffBlock.cs`

```csharp
public enum DiffLineKind : byte { Context, Add, Delete, HunkHeader, FileHeader }
public readonly record struct DiffLine(DiffLineKind Kind, int OldNo, int NewNo, string Text);
public static class UnifiedDiffParser { public static IReadOnlyList<DiffLine> Parse(string diffText); }
```

### WordDiff

**Purpose:** Whitespace-token intraline diff between a removed and an added row
(git `--word-diff` equivalent). Projects each side independently around matched
anchors.

**File:** `src/Harbor.Ui.Framework.Rendering/Widgets/WordDiff.cs`

```csharp
public enum WordSegKind : byte { Equal, Deleted, Added }
public readonly record struct WordSeg(WordSegKind Kind, string Text);
public sealed record WordDiffSides(IReadOnlyList<WordSeg> Removed, IReadOnlyList<WordSeg> Inserted);
public static class WordDiff { public static WordDiffSides Segment(string oldLine, string newLine); }
```

### StatusViewModel / StatusBarWidget

**Purpose:** Typed status payload for the footer bar. `BuildSegments` packs a
reusable workspace span; `StatusBarWidget.Paint` blits fitted segments with
accent colors.

**File:** `src/Harbor.Ui.Framework.Rendering/Widgets/StatusViewModel.cs`

```csharp
public sealed class StatusViewModel
{
    public string Model { get; set; }
    public string? Cost { get; set; }
    public string? Tokens { get; set; }
    public string? Retry { get; set; }
    public StatusBarMode Mode { get; set; }
    public AgentPhase Phase { get; set; }
    public int BuildSegments(Span<StatusSeg> workspace);
}

public static class StatusBarWidget
{
    public static void Paint(ScreenBuffer buffer, Rect rect, ReadOnlySpan<StatusSeg> segs);
}
```

### PanelFx

**Purpose:** HDS v1 motion primitives for the cell renderer: entrance fades/slides,
approval warn-glow pulse, status-accent crossfades. Pure functions of monotonic
frame ticks — no timers, no allocations.

**File:** `src/Harbor.Ui.Framework.Rendering/Widgets/PanelFx.cs`

```csharp
public static class PanelFx
{
    public const int FadeMs = 150;
    public const int SlideMs = 300;
    public static readonly int FadeFrames = 9;
    public static readonly int SlideFrames = 18;
    public static double EaseOut(double t);
    public static double Progress(long startTick, long nowTick, int durationFrames);
    public static double WarnPulse(long birthTick, long nowTick);
    public static CellStyle WarnTone(long birthTick, long nowTick);
    public static CellStyle WithAlpha(CellStyle style, double alpha);
    public static void BlendRegion(ScreenBuffer buffer, Rect region, double alpha);
}
```

### MascotPhases (AgentPhase / MascotReaction)

**Purpose:** Fine-grained agent phase and one-shot event reaction enums for the
mascot renderer.

**File:** `src/Harbor.Ui.Framework.Rendering/Widgets/MascotPhases.cs`

```csharp
public enum AgentPhase : byte { Auto, Thinking, ToolCall, Errored, Succeeded }
public enum MascotReaction : byte { None, ErrorBlink, SuccessBounce, ApprovalWiggle }
```

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

`contrib/apps/Harbor.App.Wpf/Converters/Converters.cs` mirrors the Avalonia wrappers (same names,
same logic, `System.Windows.Data.IValueConverter` instead of Avalonia's). Includes
`NullToCollapsedConverter` for the `Visibility` enum.

### Blazor

Blazor components call `StatusMappers` directly from `@code` blocks — no converter layer
needed because Razor evaluates C# expressions inline.

---

## Adding a new platform

To add a new platform (e.g. MAUI WinUI):

1. Create `contrib/apps/Harbor.App.Maui/Controls/` folder.
2. Port each of `StatusBadge`, `ChatBubble`, `SessionRow` to MAUI XAML.
3. Use the SAME property names (`StatusText`, `BrushKey`, `Title`, etc.).
4. Resolve `BrushKey` via a MAUI `IValueConverter` that wraps `StatusMappers.*` lookups.
5. Add unit tests for property defaults + setters (mirror `tests/Harbor.App.Avalonia.Tests/ComponentTests.cs`).

The shared `StatusMappers` helpers + shared prop names ensure your new platform's
components look + behave identically to Avalonia / Blazor / WPF.
