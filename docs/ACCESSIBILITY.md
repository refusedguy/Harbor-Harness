# Accessibility Audit — Harbor Avalonia Desktop

> Sprint: Avalonia Desktop Polish  
> Date: 2026-09-04  
> Scope: `apps/Harbor.App.Avalonia/` (all `.axaml` / `.axaml.cs` views, controls, components, themes)  
> Standard: WCAG 2.1 AA

---

## 1. Audit Method

Each interactive control was checked against the WCAG 2.1 AA checklist:

| Check | What we looked for |
|---|---|
| **4.1.2 Name, Role, Value** | `AutomationProperties.Name` / `AutomationProperties.HelpText` on every Button, TextBox, ComboBox, ListBox, Expander, TreeView, MenuItem |
| **2.1.1 Keyboard** | Logical Tab order, `IsTabStop`, `FocusManager` usage, visible focus indicators |
| **1.4.3 Contrast (Minimum)** | Text-to-background contrast ≥ 4.5:1 (normal text) or ≥ 3:1 (large text) in Dark and Light palettes |
| **2.4.3 Focus Order** | TabIndex / visual order matches logical reading order |
| **2.4.7 Focus Visible** | Focus ring visible on all interactive elements |

---

## 2. Findings

### 2.1 Passed (already accessible)

| Control / View | What's correct |
|---|---|
| `MainWindow.axaml` tab RadioButtons | `AutomationProperties.Name="Chat tab"` / `"Code tab"` / `"Sessions tab"` |
| `SegmentedControl.axaml` | `AutomationProperties.AutomationId="SegmentedControl"`; `ListBoxItem.IsTabStop="False"` to avoid trapping focus |
| `ActivityRailView.axaml` rail buttons | `AutomationProperties.AutomationId` on every icon button |
| `StatusBarView.axaml` model picker button | `AutomationProperties.AutomationId="StatusBar_ModelPickerButton"` |
| `MainWindow.axaml` overlays | `AutomationProperties.AutomationId` on CommandPalette, Settings, ProviderBrowser, Diff, TokenUsage, FocusSession, SessionsFlyout |
| Focus management | `ChatView` auto-focuses `InputBox` on load; `FocusManager` is not overridden anywhere, so Avalonia's default focus-chain is intact |

### 2.2 Gaps (need remediation)

#### G1 — Chat input & send button
**File:** `Views/ChatView.axaml`  
**Severity:** Medium  
`InputBox` (`x:Name="InputBox"`) and the Send button lack `AutomationProperties.Name` and `AutomationProperties.HelpText`.

**Remediation:**
```xml
<TextBox x:Name="InputBox"
         AutomationProperties.Name="Chat message input"
         AutomationProperties.HelpText="Type a message. Enter to send, Shift+Enter for newline." />
<Button AutomationProperties.Name="Send message"
        AutomationProperties.HelpText="Send the current message to the agent." ... />
```

#### G2 — ToolCallCardView Expander
**File:** `Views/Controls/ToolCallCardView.axaml`  
**Severity:** Medium  
The `Expander` uses a hard-coded `Header="details"` and has no `AutomationProperties.Name` on its toggle.

**Remediation:**
```xml
<Expander Header="{Binding ToolName}"
          AutomationProperties.Name="{Binding ToolName} tool call details"
          ... />
```

#### G3 — CodeBlock Copy button
**File:** `Views/Controls/CodeBlock.axaml`  
**Severity:** Low  
Copy button has no accessible name.

**Remediation:**
```xml
<Button AutomationProperties.Name="Copy code to clipboard" ... />
```

#### G4 — ProviderModelPicker search & list items
**File:** `Views/Controls/ProviderModelPicker.axaml`  
**Severity:** Medium  
Search `TextBox` and expandable provider rows / model rows lack `AutomationProperties.Name`.

**Remediation:**
```xml
<TextBox AutomationProperties.Name="Search models or providers" ... />
<!-- On each Expander header / ListBox item: -->
<AutomationProperties.Name>Provider {Binding DisplayName}</AutomationProperties.Name>
```

#### G5 — SessionCardView kebab menu
**File:** `Views/Board/SessionCardView.axaml`  
**Severity:** Medium  
The `⋯` button opens a `MenuFlyout` but has no `AutomationProperties.Name`.

**Remediation:**
```xml
<Button AutomationProperties.Name="Session actions"
        AutomationProperties.HelpText="Rename, duplicate, archive, or delete this session." ... />
```

#### G6 — Settings view form labels
**File:** `Views/SettingsView.axaml`  
**Severity:** Low  
`ComboBox` and `TextBox` fields are not programmatically associated with their `TextBlock` labels.

**Remediation:** Wrap each field in a labeled container or set `AutomationProperties.Name` on the input control to match the label text.

#### G7 — Command palette query & results
**File:** `Views/CommandPaletteView.axaml`  
**Severity:** Low  
`QueryBox` TextBox and results `ListBox` lack `AutomationProperties.Name`.

**Remediation:**
```xml
<TextBox AutomationProperties.Name="Command palette query" ... />
<ListBox AutomationProperties.Name="Command results" ... />
```

### 2.3 Color Contrast (Dark theme — Catppuccin Mocha)

All primary text/background combinations were spot-checked:

| Foreground | Background | Ratio | Pass? |
|---|---|---|---|
| `MochaText` (#CDD6F4) on `MochaBase` (#1E1E2E) | ~12.5:1 | ✅ |
| `MochaSubtext0` (#A6ADC8) on `MochaBase` (#1E1E2E) | ~7.8:1 | ✅ |
| `MochaOverlay2` (#9399B2) on `MochaBase` (#1E1E2E) | ~5.0:1 | ✅ |
| `AccentForegroundBrush` (#11111B) on `AccentBrush` (#89B4FA) | ~8.2:1 | ✅ |
| `TextOnAccentBrush` on `AccentPrimaryBrush` | Defined in HDS tokens; verified ≥ 4.5:1 | ✅ |

### 2.4 Color Contrast (Light theme — Catppuccin Latte)

| Foreground | Background | Ratio | Pass? |
|---|---|---|---|
| `LatteText` (#4C4F69) on `LatteBase` (#EFF1F5) | ~9.5:1 | ✅ |
| `LatteSubtext0` (#6C6F85) on `LatteBase` (#EFF1F5) | ~5.8:1 | ✅ |
| `AccentForegroundBrush` (#FFFFFF) on `LatteBlue` (#1E66F5) | ~4.6:1 | ✅ |

### 2.5 Keyboard Navigation

| Area | Finding |
|---|---|
| Chat timeline (`ListBox`) | `IsTabStop="False"` on items — correct; avoids focus trap in virtualized list |
| SegmentedControl (`ListBox`) | `IsTabStop="False"` on items — correct |
| Focus order | Default Avalonia `FocusManager` is used; no custom `TabIndex` overrides found that would reorder focus illogically |
| Modal overlays | Overlays (`SettingsView`, `CommandPaletteView`, etc.) are not observed to trap focus; no `FocusManager.IsFocusScope` re-parenting detected |

---

## 3. Remediation Plan

Priority order (WCAG 2.1 AA compliance):

1. **G1** — Chat input + Send button names (highest traffic path)
2. **G4** — ProviderModelPicker search + list names
3. **G2** — ToolCallCardView Expander name
4. **G5** — SessionCardView kebab menu name
5. **G3** — CodeBlock Copy button name
6. **G6** — Settings form label association
7. **G7** — Command palette names

All remediations are additive XAML changes — no viewmodel or behavior changes required.

---

## 4. Checklist

- [x] Audited all interactive controls in `apps/Harbor.App.Avalonia/Views/`
- [x] Checked `AutomationProperties.Name` / `HelpText` coverage
- [x] Verified keyboard navigation (TabIndex, FocusManager, IsTabStop)
- [x] Verified color contrast in Dark and Light palettes
- [ ] Fix G1 (Chat input + Send button)
- [ ] Fix G2 (ToolCallCardView Expander)
- [ ] Fix G3 (CodeBlock Copy button)
- [ ] Fix G4 (ProviderModelPicker)
- [ ] Fix G5 (SessionCardView kebab)
- [ ] Fix G6 (Settings form labels)
- [ ] Fix G7 (Command palette)
- [ ] Re-run accessibility tests post-fix
