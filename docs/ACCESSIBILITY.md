# Avalonia Accessibility Audit — Harbor Desktop

Date: 2026-09-02
Scope: `apps/Harbor.App.Avalonia/` + shared `Harbor.Ui.Framework.Rendering/Widgets/`
Standard: WCAG 2.1 AA

## Summary

| Area | Status | Notes |
|------|--------|-------|
| AutomationProperties.AutomationId | Partial | 22 controls have IDs; ~130 interactive controls lack any automation properties |
| AutomationProperties.Name | Missing | TextBox, Button, RadioButton, ComboBox, CheckBox, ListBox items lack `Name` |
| Keyboard navigation | Partial | Tab order follows visual layout; no explicit `TabIndex` or `IsTabStop` overrides |
| Focus indicators | Unknown | No explicit focus visual overrides found; relies on Avalonia defaults |
| Color contrast | Pass | Catppuccin palette (Mocha/Latte) meets WCAG AA for text ≥ 4.5:1 |
| Screen reader announcements | Partial | Status changes rely on visual indicators; no live-region announcements |

## Critical Gaps

### 1. Missing `AutomationProperties.Name` on primary controls
- `ChatView.axaml`: `HeroInputBox`, `InputBox`, Send button, Stop button
- `MainWindow.axaml`: Chat/Code/Sessions tab RadioButtons
- `SettingsView.axaml`: Theme ComboBox, Storage ComboBox, LogLevel ComboBox, all TextBox inputs
- `OnboardingWindow.axaml`: Provider ComboBox, Model TextBox, Theme RadioButtons
- `CommandPaletteView.axaml`: Search TextBox, results ListBox
- `CodeEditorView.axaml`: Editor TextBox, Open/Save buttons
- `DiffView.axaml`: Left/Right TextBox, Compute button
- `FloatingTerminalPaneWindow.axaml`: Single/Split/Stack/Close buttons

### 2. Missing `AutomationProperties.HelpText` on complex controls
- SegmentedControl (custom ListBox)
- ToolCallCardView (expandable tool call UI)
- MarkdownRenderer (rich text region)
- ProviderModelPicker (search + list)

### 3. Keyboard navigation gaps
- No `TabIndex` set on any control (relies on visual order)
- No `IsTabStop="False"` on decorative/interactive-only elements (e.g., BlinkingCursor Ellipse)
- Modal overlays (CommandPalette, Settings) trap focus? Unknown — need FocusManager audit

### 4. Live regions / dynamic announcements
- Agent status ("Agent is running…", streaming, thinking) not announced to screen readers
- Toast notifications not announced
- Token usage changes not announced

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

1. Add `AutomationProperties.Name` to all interactive controls (P0)
2. Add `AutomationProperties.HelpText` to complex widgets (P1)
3. Add `TabIndex` where visual order differs from logical order (P1)
4. Add `IsTabStop="False"` to decorative elements (P2)
5. Implement live-region announcements for agent status + toasts (P2)

## Test Commands

```bash
dotnet test tests/Harbor.App.Avalonia.Tests/ -c Release --no-build
dotnet test tests/Harbor.E2E.App.Avalonia/ -c Release --no-build
```
