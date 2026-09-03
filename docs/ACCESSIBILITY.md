# Avalonia Accessibility Audit — Harbor Desktop

Date: 2026-09-03
Scope: `apps/Harbor.App.Avalonia/` + shared `Harbor.Ui.Framework.Rendering/Widgets/`
Standard: WCAG 2.1 AA

## Summary

| Area | Status | Notes |
|------|--------|-------|
| AutomationProperties.Name | Partial | 8 controls have explicit names; ~70 interactive controls lack `Name` |
| AutomationProperties.HelpText | Missing | No `HelpText` found on any complex widget |
| AutomationProperties.AutomationId | Partial | 20+ overlays/panels have IDs; most interactive controls lack them |
| Keyboard navigation | Partial | ListBoxItem containers set `IsTabStop="False"`; no explicit `TabIndex` on focusable controls |
| Focus indicators | Partial | HDS button styles define `:focus-visible` 2px accent border (WCAG 2.4.7); other controls rely on Avalonia defaults |
| Color contrast | Partial | Catppuccin Mocha palette meets WCAG AA; HarborDark theme has 2 contrast failures |
| Screen reader announcements | Missing | Status changes, toasts, streaming state changes have no live-region announcements |
| Semantic roles | Partial | Most controls use default Avalonia roles; no custom `AutomationProperties.ControlType` overrides |

## Controls With Existing Accessibility Properties

The following controls already have accessibility properties set:

| Control | File | Property | Value |
|---|---|---|---|
| `HeroInputBox` | `ChatView.axaml` | `AutomationProperties.Name` | `"Chat input"` |
| `InputBox` (docked) | `ChatView.axaml` | `AutomationProperties.Name` | `"Message input"` |
| Send button | `ChatView.axaml` | `AutomationProperties.Name` | `"Send message"` |
| Stop button | `ChatView.axaml` | `AutomationProperties.Name` | `"Stop generation"` |
| Chat/Code/Sessions tabs | `MainWindow.axaml` | `AutomationProperties.Name` | `"Chat tab"`, `"Code tab"`, `"Sessions tab"` |
| `SegmentedControl` | `SegmentedControl.axaml` | `AutomationProperties.AutomationId` | `"SegmentedControl"` |
| Overlay panels | `MainWindow.axaml` | `AutomationProperties.AutomationId` | `Overlay_CommandPalette`, `Overlay_SettingsPanel`, etc. |
| Activity rail buttons | `ActivityRailView.axaml` | `AutomationProperties.AutomationId` | `Rail_ToggleButton`, `Rail_BoardButton`, etc. |
| Sessions flyout controls | `SessionsFlyoutView.axaml` | `AutomationProperties.AutomationId` | `SessionsFlyout_NewSessionButton`, `SessionsFlyout_SearchBox`, `SessionsFlyout_List` |
| Toast container | `ToastNotificationsView.axaml` | `AutomationProperties.AutomationId` | `ToastNotifications` |
| Status bar model picker | `StatusBarView.axaml` | `AutomationProperties.AutomationId` | `StatusBar_ModelPickerButton` |

## Critical Gaps

### 1. Missing `AutomationProperties.Name` on primary interactive controls

**Priority: P0 — Required for WCAG 2.1.1 (Keyboard) and 4.1.2 (Name, Role, Value)**

| View | Control | Recommended Name |
|---|---|---|
| `MainWindow.axaml` | Tab RadioButtons | Already have names — no action needed |
| `SettingsView.axaml` | Theme ComboBox | `"Theme"` |
| `SettingsView.axaml` | Storage backend ComboBox | `"Storage backend"` |
| `SettingsView.axaml` | Log level ComboBox | `"Log level"` |
| `SettingsView.axaml` | Default provider TextBox | `"Default provider"` |
| `SettingsView.axaml` | Default model TextBox | `"Default model"` |
| `SettingsView.axaml` | Font family TextBox | `"Font family"` |
| `SettingsView.axaml` | Ollama host TextBox | `"Ollama host"` |
| `SettingsView.axaml` | API key TextBox (per-provider) | `"API key for {provider}"` |
| `SettingsView.axaml` | Close button | `"Close settings"` |
| `SettingsView.axaml` | Save button | `"Save settings"` |
| `SettingsView.axaml` | Cancel button | `"Cancel"` |
| `OnboardingWindow.axaml` | Provider CheckBox items | `"Select {provider} provider"` |
| `OnboardingWindow.axaml` | API key TextBox | `"API key"` |
| `OnboardingWindow.axaml` | Test connection button | `"Test connection"` |
| `OnboardingWindow.axaml` | Model ComboBox | `"Default model"` |
| `OnboardingWindow.axaml` | Model TextBox (fallback) | `"Model ID"` |
| `OnboardingWindow.axaml` | Theme RadioButtons | `"Dark theme"`, `"Light theme"`, `"System theme"` |
| `OnboardingWindow.axaml` | Back button | `"Back"` |
| `OnboardingWindow.axaml` | Next/Finish button | `"Next"` / `"Finish"` |
| `OnboardingWindow.axaml` | Skip button | `"Skip onboarding"` |
| `CommandPaletteView.axaml` | Search TextBox (`QueryBox`) | `"Command search"` |
| `CommandPaletteView.axaml` | Results ListBox | `"Command results"` |
| `CodeEditorView.axaml` | Editor TextBox (AvaloniaEdit) | `"Code editor"` |
| `CodeEditorView.axaml` | Open button | `"Open file"` |
| `CodeEditorView.axaml` | Save button | `"Save file"` |
| `CodeEditorView.axaml` | Inline edit TextBox | `"Describe the change"` |
| `CodeEditorView.axaml` | Accept button | `"Accept inline edit"` |
| `CodeEditorView.axaml` | Reject button | `"Reject inline edit"` |
| `DiffView.axaml` | Left TextBox | `"Before text"` |
| `DiffView.axaml` | Right TextBox | `"After text"` |
| `DiffView.axaml` | Compute button | `"Compute diff"` |
| `DiffView.axaml` | Close button | `"Close diff viewer"` |
| `ComposerView.axaml` | Input TextBox (`InputBox`) | `"Composer input"` |
| `ComposerView.axaml` | Send button | `"Send message"` |
| `ComposerView.axaml` | Stop button | `"Stop generation"` |
| `TokenUsageView.axaml` | Clear button | `"Clear token usage"` |
| `TokenUsageView.axaml` | Close button | `"Close token usage"` |
| `ProviderBrowserView.axaml` | Close button | `"Close provider browser"` |
| `ProviderBrowserView.axaml` | Providers ListBox | `"Provider list"` |
| `ProviderBrowserView.axaml` | Models ItemsControl | `"Model list"` |
| `ActivityRailView.axaml` | Toggle button | `"Toggle sidebar"` |
| `ActivityRailView.axaml` | Board/Sessions button | `"Sessions"` |
| `ActivityRailView.axaml` | Search button | `"Search"` |
| `ActivityRailView.axaml` | Diff button | `"Diff drawer"` |
| `ActivityRailView.axaml` | Theme button | `"Toggle theme"` |
| `ActivityRailView.axaml` | Settings button | `"Settings"` |
| `ActivityRailView.axaml` | Refresh button | `"Refresh file tree"` |
| `SessionsFlyoutView.axaml` | New session button | `"New session"` |
| `SessionsFlyoutView.axaml` | Search TextBox | `"Search sessions"` |
| `SessionsFlyoutView.axaml` | Session ListBox | `"Session list"` |
| `StatusBarView.axaml` | Model picker button | `"Select model"` |
| `SessionCardView.axaml` | Kebab menu button | `"Session actions"` |
| `ProviderModelPicker.axaml` | Search TextBox | `"Search models or providers"` |
| `ProviderModelPicker.axaml` | Provider Expander headers | `"{provider} models, auth: {status}"` |
| `ProviderModelPicker.axaml` | Model selection buttons | `"Select {model}"` |
| `ToolCallCardView.axaml` | Expander toggle | `"Tool call details for {tool}"` |
| `HdsDiffCompact.axaml` | Click-to-expand border | `"Diff preview, click to expand"` |
| `CodeBlock.axaml` | Copy button | `"Copy code"` |
| `TypewriterStreamingText.axaml` | Streaming text region | `"Streaming response"` |
| `Sparkline.axaml` | Chart canvas | `"Token usage chart"` |
| `MarkdownRenderer.axaml` | Markdown region | `"Message content"` |
| `FocusSessionView.axaml` | Exit Focus Mode button | `"Exit focus mode"` |

### 2. Missing `AutomationProperties.HelpText` on complex controls

**Priority: P1 — Required for WCAG 2.1.1 (Keyboard) and 4.1.2 (Name, Role, Value)**

| Control | Recommended HelpText |
|---|---|
| `SegmentedControl` | `"Tab strip: {count} sections. Use arrow keys to navigate."` |
| `ToolCallCardView` | `"Expandable card for tool call {tool}. Press Enter to expand details."` |
| `MarkdownRenderer` | `"Formatted message content. Use screen reader navigation to read headings and links."` |
| `ProviderModelPicker` | `"Search and select a provider and model. Expand a provider to see its models."` |
| `HdsDiffCompact` | `"Collapsed diff preview. Press Enter or click to expand the full diff."` |
| `Sparkline` | `"Inline chart showing token usage history over recent turns."` |
| `TypewriterStreamingText` | `"Streaming text response from the agent. Cursor indicates active streaming."` |
| `EmptyState` | `"Empty panel. {suggestions_count} suggestions available. Use Tab to navigate."` |
| `CommandPaletteView` | `"Command palette. Type to search commands, use arrow keys to navigate results, Enter to execute."` |
| `CodeEditorView` | `"Code editor. Open a file with Ctrl+O, save with Ctrl+S. Use Tab to switch panels."` |
| `TerminalPaneControl` | `"Terminal pane. Type commands in the input field. Output is read-only."` |

### 3. Keyboard navigation gaps

**Priority: P1 — Required for WCAG 2.1.1 (Keyboard)**

- No `TabIndex` set on any control (relies on visual order, which is correct for LTR layouts)
- No `IsTabStop="False"` on decorative/interactive-only elements except ChatView ListBoxItem containers
- Modal overlays (CommandPalette, Settings, DiffView, ProviderBrowser, FocusSession, TokenUsage) lack explicit focus trapping — `FocusManager` should move focus INTO the modal on open and back to the trigger on close
- `Expander` toggle in `ToolCallCardView` has no explicit keyboard affordance beyond default (Space/Enter toggles, but no visible focus indicator in the header)
- `SessionCardView` kebab menu button has no explicit `AutomationProperties.Name` — screen reader announces only "Button"

### 4. Live regions / dynamic announcements

**Priority: P2 — Required for WCAG 2.1.1 (Keyboard) and 2.1.2 (No Keyboard Trap)**

- Agent status changes ("Agent is running…", streaming, thinking) not announced to screen readers
- Toast notifications not announced — should use `AutomationProperties.LiveSetting="Assertive"` on toast container
- Token usage changes in status bar not announced
- Compaction status not announced
- Streaming buffer updates not announced

### 5. Color contrast

**Priority: P0 — Required for WCAG 1.4.3 (Contrast Minimum)**

**Catppuccin Mocha (default dark theme):**

| Pair | Ratio | Pass AA? |
|---|---|---|
| MochaText (#CDD6F4) on MochaBase (#1E1E2E) | ~13.5:1 | ✅ |
| MochaSubtext0 (#A6ADC8) on MochaBase (#1E1E2E) | ~8.5:1 | ✅ |
| MochaBlue (#89B4FA) on MochaBase (#1E1E2E) | ~8.0:1 | ✅ |
| MochaRed (#F38BA8) on MochaBase (#1E1E2E) | ~6.8:1 | ✅ |
| #FFFFFF on MochaBase (#1E1E2E) | ~16:1 | ✅ |
| AccentForeground (#11111B) on AccentBrush (#89B4FA) | ~8.5:1 | ✅ |
| MochaOverlay1 (#8B90A6) on MochaBase (#1E1E2E) | ~5.2:1 | ✅ (AA minimum) |
| MochaYellow (#F9E2AF) on MochaBase (#1E1E2E) | ~10.2:1 | ✅ |

**HarborDark (Orca-inspired theme):**

| Pair | Ratio | Pass AA? |
|---|---|---|
| TextPrimary (#ECECEF) on BgApp (#0D0D0F) | ~16:1 | ✅ |
| TextSecondary (#9A9AA3) on BgApp (#0D0D0F) | ~6.8:1 | ✅ |
| TextTertiary (#6B6B75) on BgApp (#0D0D0F) | ~4.2:1 | ⚠️ **FAIL** — below 4.5:1 for normal text |
| TextMuted (#6B6B75) on BgApp (#0D0D0F) | ~4.2:1 | ⚠️ **FAIL** — below 4.5:1 for normal text |
| AccentPrimary (#F5A623) on BgApp (#0D0D0F) | ~8.5:1 | ✅ |
| TextOnAccent (#0D0D0F) on AccentPrimary (#F5A623) | ~8.5:1 | ✅ |

**HarborDark contrast failures:**
- `TextTertiaryBrush` / `TextMutedBrush` on `BgAppBrush` fails WCAG AA (4.2:1 < 4.5:1)
- These are used for secondary/disabled text and subtle UI chrome
- Fix: raise `TextTertiaryColor` to `#7A7A85` (~4.8:1) and `TextMutedColor` to `#7A7A85`

## Recommendations

1. **P0:** Add `AutomationProperties.Name` to all 70+ interactive controls listed in §1
2. **P0:** Fix HarborDark theme `TextTertiaryColor` / `TextMutedColor` contrast to ≥ 4.5:1
3. **P1:** Add `AutomationProperties.HelpText` to complex widgets listed in §2
4. **P1:** Implement focus trapping for all modal overlays (CommandPalette, Settings, DiffView, ProviderBrowser, FocusSession, TokenUsage)
5. **P1:** Add `TabIndex` / `IsTabStop` to non-obvious interactive elements
6. **P2:** Add live-region announcements for agent status, toasts, and streaming state
7. **P2:** Add `AutomationProperties.ControlType` overrides for custom controls (Expander, ListBox with custom ItemContainerStyle)

## Test Commands

```bash
dotnet test tests/Harbor.App.Avalonia.Tests/ -c Release --no-build
dotnet test tests/Harbor.E2E.App.Avalonia/ -c Release --no-build
```

## References

- [WCAG 2.1 AA — Contrast Minimum](https://www.w3.org/WAI/WCAG21/Understanding/contrast-minimum.html)
- [Avalonia Accessibility](https://docs.avaloniaui.com/docs/controls/accessibility)
- [Avalonia AutomationProperties](https://docs.avaloniaui.com/docs/controls/accessibility/automationproperties)
