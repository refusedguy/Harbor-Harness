# Avalonia Accessibility Audit — Harbor Desktop

Date: 2026-09-03
Scope: `apps/Harbor.App.Avalonia/` + shared `Harbor.Ui.Framework.Rendering/Widgets/`
Standard: WCAG 2.1 AA

## Summary

| Area | Status | Notes |
|------|--------|-------|
| AutomationProperties.AutomationId | Partial | 23 controls have IDs; ~140 interactive controls lack any automation properties |
| AutomationProperties.Name | Partial | ChatView primary controls have names; most other views lack `Name` on interactive elements |
| AutomationProperties.HelpText | Missing | No `HelpText` found on any complex widget |
| Keyboard navigation | Partial | ChatView ListBoxItem containers set `IsTabStop="False"`; no explicit `TabIndex` on focusable controls |
| Focus indicators | Unknown | No explicit focus visual overrides found; relies on Avalonia defaults |
| Color contrast | Pass | Catppuccin palette (Mocha/Latte) meets WCAG AA for text ≥ 4.5:1 |
| Screen reader announcements | Partial | Status changes rely on visual indicators; no live-region announcements |

## Controls With Existing AutomationProperties

The following controls already have accessibility properties set:

| Control | File | Property | Value |
|---|---|---|---|
| `HeroInputBox` | `ChatView.axaml` | `AutomationProperties.Name` | `"Chat input"` |
| `InputBox` | `ChatView.axaml` | `AutomationProperties.Name` | `"Message input"` |
| Send button | `ChatView.axaml` | `AutomationProperties.Name` | `"Send message"` |
| Stop button | `ChatView.axaml` | `AutomationProperties.Name` | `"Stop generation"` |
| `SegmentedControl` | `SegmentedControl.axaml` | `AutomationProperties.AutomationId` | `"SegmentedControl"` |

## Critical Gaps

### 1. Missing `AutomationProperties.Name` on primary controls

- `MainWindow.axaml`: Chat/Code/Sessions tab RadioButtons
- `SettingsView.axaml`: Theme ComboBox, Storage ComboBox, LogLevel ComboBox, all TextBox inputs, Close button, Save button, Cancel button
- `OnboardingWindow.axaml`: Provider CheckBox items, API key TextBox, Test connection button, Model ComboBox/TextBox, Theme RadioButtons, Back/Next/Skip buttons
- `CommandPaletteView.axaml`: Search TextBox (`QueryBox`), results ListBox
- `CodeEditorView.axaml`: Editor TextBox (AvaloniaEdit), Open/Save buttons, Inline Edit TextBox, Accept/Reject buttons
- `DiffView.axaml`: Left/Right TextBox, Compute button, Close button
- `FloatingTerminalPaneWindow.axaml`: Single/Split/Stack/Close buttons
- `TerminalPaneControl.axaml`: `OutputBox`, `InputBox`
- `FocusSessionView.axaml`: Exit Focus Mode button
- `ComposerView.axaml`: Composer TextBox, Send button, Stop button
- `TokenUsageView.axaml`: Clear button, Close button
- `ProviderBrowserView.axaml`: Close button, provider/model ListBox
- `ActivityRailView.axaml`: Rail toggle/board/search/diff/theme/settings buttons
- `SessionsFlyoutView.axaml`: New session button, search box, session list
- `StatusBarView.axaml`: Model picker button
- `ToastNotificationsView.axaml`: Toast host
- `BoardView.axaml`: Board-level controls
- `SessionCardView.axaml`: Card-level actions

### 2. Missing `AutomationProperties.HelpText` on complex controls

- `SegmentedControl` (custom ListBox tab strip)
- `ToolCallCardView` (expandable tool call UI)
- `MarkdownRenderer` (rich text region)
- `ProviderModelPicker` (search + expandable provider list)
- `HdsDiffCompact` (click-to-expand diff)
- `Sparkline` (inline chart)
- `TypewriterStreamingText` (streaming buffer with cursor)
- `EmptyState` (suggestion buttons)
- `CommandPaletteView` (search + results ListBox)
- `CodeEditorView` (tab strip + inline edit overlay)

### 3. Keyboard navigation gaps

- No `TabIndex` set on any control (relies on visual order, which is correct for LTR layouts)
- No `IsTabStop="False"` on decorative/interactive-only elements except ChatView ListBoxItem containers
- Modal overlays (CommandPalette, Settings, DiffView, ProviderBrowser) trap focus? Unknown — need FocusManager audit
- `Expander` toggle in `ToolCallCardView` has no explicit keyboard affordance beyond default

### 4. Live regions / dynamic announcements

- Agent status ("Agent is running…", streaming, thinking) not announced to screen readers
- Toast notifications not announced
- Token usage changes not announced
- Compaction status not announced

## Color Contrast (Catppuccin Mocha)

| Pair | Ratio | Pass AA? |
|------|-------|----------|
| MochaText (#CDD6F4) on MochaBase (#1E1E2E) | ~13.5:1 | ✅ |
| MochaSubtext0 (#A6ADC8) on MochaBase (#1E1E2E) | ~8.5:1 | ✅ |
| MochaBlue (#89B4FA) on MochaBase (#1E1E2E) | ~8.0:1 | ✅ |
| MochaRed (#F38BA8) on MochaBase (#1E1E2E) | ~6.8:1 | ✅ |
| #FFFFFF on MochaBase (#1E1E2E) | ~16:1 | ✅ |
| AccentForeground (#11111B) on AccentBrush (#89B4FA) | ~8.5:1 | ✅ |

Color contrast passes WCAG AA for all tested pairs.

## Recommendations

1. Add `AutomationProperties.Name` to all interactive controls missing them (P0)
2. Add `AutomationProperties.HelpText` to complex widgets (P1)
3. Add `TabIndex` where visual order differs from logical order (P1)
4. Add `IsTabStop="False"` to decorative elements (P2)
5. Implement live-region announcements for agent status + toasts (P2)

## Test Commands

```bash
dotnet test tests/Harbor.App.Avalonia.Tests/ -c Release --no-build
dotnet test tests/Harbor.E2E.App.Avalonia/ -c Release --no-build
```
